using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.Modules.Payments.Providers.Paymob;

/// <summary>Validated Paymob settings (built only by <see cref="PaymobOptionsFactory"/>). Nothing here comes from a client.</summary>
public sealed record PaymobOptions(
    string SecretKey,
    string PublicKey,
    string HmacSecret,
    IReadOnlyList<long> IntegrationIds,
    IReadOnlyList<string> AllowedCurrencies,
    bool LiveMode,
    Uri ApiBaseUrl,
    Uri CheckoutBaseUrl,
    Uri ReturnBaseUrl,
    Uri NotificationUrl)
{
    // Per Paymob's documentation (https://developers.paymob.com/): Egypt API host and Unified Checkout host.
    public const string DefaultApiBaseUrl = "https://accept.paymob.com/";
    public const string DefaultCheckoutBaseUrl = "https://eg.checkout.paymob.com/";
}

/// <summary>
/// Payments through Paymob's Intention API and Unified Checkout (redirect), so PulseReserve never touches card data.
/// <list type="bullet">
/// <item>Initiation: <c>POST /v1/intention/</c> with <c>Authorization: Token {secret key}</c>; the amount, currency and
/// reference come from the persisted order. The customer is redirected to Paymob's hosted checkout.</item>
/// <item>Result: learned ONLY from Paymob's HMAC-verified transaction callback (<see cref="PaymobHmac"/>).</item>
/// <item>NOT implemented: payment status lookup and refunds. The reachable official documentation does not specify those
/// endpoints for Egypt, and none is guessed here. Such attempts stay unresolved until a verified callback or an operator
/// decides, and refunds go to an operator (NeedsReview) to be made in the Paymob dashboard.</item>
/// </list>
/// </summary>
public sealed class PaymobPaymentProvider(PaymobOptions options, HttpClient http) : IPaymentProvider, IPaymentWebhookHandler
{
    public const string ProviderName = "Paymob";

    public string Name => ProviderName;
    public bool IsSimulated => false;
    public bool IsAvailable => true;
    public bool SupportsPaymentLookup => false;
    public bool SupportsRefunds => false;

    // Paymob intentions have no idempotency key in this integration: a repeated call may create a second intention.
    public bool IdempotentInitiation => false;

    public bool SupportsCurrency(string currency) =>
        options.AllowedCurrencies.Contains(currency, StringComparer.OrdinalIgnoreCase);

    public async Task<ProviderSession> InitiateAsync(PaymentInitiation initiation, CancellationToken cancellationToken = default)
    {
        if (!SupportsCurrency(initiation.Currency))
            throw new ProviderRejectedException("currency_not_enabled");

        long cents;
        try
        {
            cents = CurrencyAmounts.ToMinorUnits(initiation.Amount, initiation.Currency);
        }
        catch (ArgumentException)
        {
            throw new ProviderRejectedException("amount_not_chargeable");
        }

        var orderPath = $"{options.ReturnBaseUrl.ToString().TrimEnd('/')}/account/orders/{initiation.OrderId}";
        var payload = new
        {
            amount = cents,
            currency = initiation.Currency.ToUpperInvariant(),
            payment_methods = options.IntegrationIds,
            // The documented rule: the amount equals the sum of the items; one item carries the whole total.
            items = new[] { new { name = "PulseReserve tickets", amount = cents, quantity = 1 } },
            // Returned on the callback as order.merchant_order_id: how the verified callback finds its attempt.
            special_reference = initiation.AttemptId.ToString(),
            notification_url = options.NotificationUrl.ToString(),
            redirection_url = $"{orderPath}?payment=return",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/intention/") { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {options.SecretKey}");

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // 400/401/403/404 are definitive (bad request, credentials, or an integration id that does not match the key's
            // mode); throttling, timeouts and server errors are ambiguous and must not be treated as a refusal.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new ProviderRejectedException($"http_{(int)response.StatusCode}");
            throw new HttpRequestException($"Paymob intention call was not conclusive (HTTP {(int)response.StatusCode}).");
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var clientSecret = root.TryGetProperty("client_secret", out var cs) && cs.ValueKind == JsonValueKind.String ? cs.GetString() : null;
        string? orderId = root.TryGetProperty("intention_order_id", out var oid)
            ? oid.ValueKind switch
            {
                JsonValueKind.Number => oid.GetRawText(),
                JsonValueKind.String => oid.GetString(),
                _ => null
            }
            : null;
        if (string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(orderId))
            throw new InvalidOperationException("Paymob returned an intention without a client secret or order id.");

        // https://developers.paymob.com/ : {region}.checkout.paymob.com/?publicKey=...&clientSecret=...
        var url = $"{options.CheckoutBaseUrl.ToString().TrimEnd('/')}/?publicKey={Uri.EscapeDataString(options.PublicKey)}&clientSecret={Uri.EscapeDataString(clientSecret)}";

        // The Paymob ORDER id is the provider reference: callbacks carry it as order.id and must match what we stored.
        return new ProviderSession(orderId, url);
    }

    public Task<ProviderPaymentState> GetPaymentStatusAsync(Guid attemptId, string? providerReference, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Paymob payment status lookup is not implemented: no verified endpoint is documented.");

    public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Paymob refunds are not implemented through the API; refund in the Paymob dashboard.");

    public Task<ProviderRefundState> GetRefundStatusAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Paymob refund lookup is not implemented.");

    // ───────────── callbacks ─────────────

    public WebhookParseResult Parse(WebhookRequest request)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(request.RawBody);
        }
        catch (JsonException)
        {
            // Unparseable and unsigned-by-definition: indistinguishable from forgery.
            return WebhookParseResult.Invalid();
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("obj", out var obj) || obj.ValueKind != JsonValueKind.Object)
                return WebhookParseResult.Invalid();

            // Only transaction callbacks are handled; anything else (for example card tokens) is acknowledged and ignored,
            // which changes nothing, so it needs no signature check.
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (!string.Equals(type, "TRANSACTION", StringComparison.OrdinalIgnoreCase))
                return WebhookParseResult.Ignored($"paymob:{Raw(obj, "id") ?? string.Empty}:{type}");

            if (!PaymobHmac.IsValid(obj, request.QueryValue("hmac"), options.HmacSecret))
                return WebhookParseResult.Invalid();

            var eventId = $"paymob:{Raw(obj, "id") ?? string.Empty}";

            // Merchant context: the integration must be one of ours (and so, via the key pair, in the configured mode).
            if (!long.TryParse(Raw(obj, "integration_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var integration)
                || !options.IntegrationIds.Contains(integration))
                return WebhookParseResult.WrongContext();

            if (!obj.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Object
                || Raw(order, "id") is not { Length: > 0 } paymobOrderId
                || !Guid.TryParse(Raw(order, "merchant_order_id"), out var attemptId))
                return WebhookParseResult.Malformed();

            // Success only when Paymob says the money was captured: not pending, not an error, not voided/refunded, and
            // not an authorisation that was never captured. Refund transactions are not payments. A declined card is
            // deliberately NOT turned into a failed attempt: the customer may retry within the same intention and still pay.
            var captured = Flag(obj, "success") && !Flag(obj, "pending") && !Flag(obj, "error_occured")
                           && !Flag(obj, "is_voided") && !Flag(obj, "is_refunded") && !Flag(obj, "has_parent_transaction")
                           && (!Flag(obj, "is_auth") || Flag(obj, "is_capture"));
            if (!captured)
                return WebhookParseResult.Ignored(eventId);

            long? cents = long.TryParse(Raw(obj, "amount_cents"), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : null;
            var currency = obj.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;

            return new WebhookParseResult(WebhookVerification.Verified,
                new VerifiedPaymentEvent(eventId, VerifiedEventKind.PaymentSucceeded, attemptId, null, paymobOrderId, cents, currency, null));
        }
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Raw(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;
}

/// <summary>
/// HMAC verification of Paymob's transaction processed callback, exactly as documented
/// (https://developers.paymob.com/paymob-docs/developers/webhook-callbacks-and-hmac/hmac/hmac-transaction-callback):
/// the 20 listed values of <c>obj</c> are concatenated IN THE LISTED ORDER with no separators, hashed with
/// HMAC-SHA512 keyed with the integration's HMAC secret, and compared (hex) with the <c>hmac</c> query parameter.
/// Booleans appear as <c>true</c>/<c>false</c>; numbers and strings as in the JSON. The documentation does not say how a
/// null or missing value is written, so it is written as an empty string: if Paymob does otherwise the signature will
/// not match and the callback is REJECTED (fail-closed), never accepted.
/// </summary>
public static class PaymobHmac
{
    /// <summary>The documented field order. Paths are relative to <c>obj</c>.</summary>
    public static readonly IReadOnlyList<string> Fields =
    [
        "amount_cents", "created_at", "currency", "error_occured", "has_parent_transaction", "id", "integration_id",
        "is_3d_secure", "is_auth", "is_capture", "is_refunded", "is_standalone_payment", "is_voided", "order.id", "owner",
        "pending", "source_data.pan", "source_data.sub_type", "source_data.type", "success"
    ];

    public static string BuildPayload(JsonElement obj)
    {
        var sb = new StringBuilder();
        foreach (var path in Fields)
            sb.Append(Value(obj, path));
        return sb.ToString();
    }

    public static string Compute(JsonElement obj, string secret) =>
        Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(BuildPayload(obj)))).ToLowerInvariant();

    public static bool IsValid(JsonElement obj, string? received, string secret)
    {
        if (string.IsNullOrWhiteSpace(received) || string.IsNullOrEmpty(secret))
            return false;
        var expected = Encoding.ASCII.GetBytes(Compute(obj, secret));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(received.Trim().ToLowerInvariant()));
    }

    private static string Value(JsonElement element, string path)
    {
        var current = element;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return string.Empty;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }
}

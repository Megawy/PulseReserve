using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.Modules.Payments.Providers.Stripe;

/// <summary>
/// Validated Stripe settings. Built only by <see cref="StripeOptionsFactory"/>, which refuses incomplete or
/// contradictory configuration; nothing here is ever sent by a client.
/// </summary>
public sealed record StripeOptions(
    string SecretKey,
    string WebhookSecret,
    bool LiveMode,
    IReadOnlyList<string> AllowedCurrencies,
    Uri ReturnBaseUrl,
    Uri ApiBaseUrl)
{
    public const string DefaultApiBaseUrl = "https://api.stripe.com/";

    /// <summary>Webhook timestamps older (or newer) than this are rejected as replays. Stripe's libraries default to 5 minutes.</summary>
    public TimeSpan WebhookTolerance { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Payments through Stripe-hosted Checkout (https://docs.stripe.com/api/checkout/sessions). PulseReserve never sees
/// card data. Everything charged comes from the persisted order; the customer is sent to a Stripe-hosted page and the
/// result is learned ONLY from a signature-verified webhook or from asking Stripe (never from the browser redirect).
/// Plain HTTP against the documented REST API: no SDK dependency.
/// </summary>
public sealed class StripePaymentProvider(StripeOptions options, HttpClient http, TimeProvider clock) : IPaymentProvider, IPaymentWebhookHandler
{
    public const string ProviderName = "Stripe";

    // Checkout Sessions may expire 30 minutes to 24 hours after creation (https://docs.stripe.com/api/checkout/sessions/create).
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(31);

    public string Name => ProviderName;
    public bool IsSimulated => false;
    public bool IsAvailable => true;
    public bool SupportsPaymentLookup => true;
    public bool SupportsRefunds => true;

    public bool SupportsCurrency(string currency) =>
        options.AllowedCurrencies.Contains(currency, StringComparer.OrdinalIgnoreCase);

    // ───────────── initiation ─────────────

    public async Task<ProviderSession> InitiateAsync(PaymentInitiation initiation, CancellationToken cancellationToken = default)
    {
        if (!SupportsCurrency(initiation.Currency))
            throw new ProviderRejectedException("currency_not_enabled");

        long minor;
        try
        {
            minor = CurrencyAmounts.ToMinorUnits(initiation.Amount, initiation.Currency);
        }
        catch (ArgumentException)
        {
            throw new ProviderRejectedException("amount_not_chargeable");
        }

        var orderPath = $"{options.ReturnBaseUrl.ToString().TrimEnd('/')}/account/orders/{initiation.OrderId}";
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("client_reference_id", initiation.AttemptId.ToString()),
            new("success_url", $"{orderPath}?payment=return"),
            new("cancel_url", $"{orderPath}?payment=cancelled"),
            new("expires_at", clock.GetUtcNow().Add(SessionLifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", initiation.Currency.ToLowerInvariant()),
            new("line_items[0][price_data][unit_amount]", minor.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][product_data][name]", "PulseReserve tickets"),
            new("metadata[attempt_id]", initiation.AttemptId.ToString()),
            new("metadata[order_id]", initiation.OrderId.ToString()),
            new("payment_intent_data[metadata][attempt_id]", initiation.AttemptId.ToString()),
            new("payment_intent_data[metadata][order_id]", initiation.OrderId.ToString()),
        };

        // Stripe deduplicates on the Idempotency-Key, so retrying after a lost answer returns the SAME session.
        using var request = NewRequest(HttpMethod.Post, "v1/checkout/sessions", form);
        request.Headers.Add("Idempotency-Key", $"pulsereserve-attempt-{initiation.AttemptId:N}");

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfFailed(response.StatusCode, body, initiation: true);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var id = Str(root, "id");
        var url = Str(root, "url");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var checkout) || checkout.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Stripe returned a session without a usable id or URL.");

        // The session must describe exactly what we asked for; otherwise nothing is trusted or shown to the customer.
        if (Str(root, "client_reference_id") != initiation.AttemptId.ToString()
            || Long(root, "amount_total") != minor
            || !string.Equals(Str(root, "currency"), initiation.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stripe returned a session that does not match the requested payment.");

        return new ProviderSession(id, url);
    }

    // ───────────── status lookup ─────────────

    public Task<ProviderPaymentState> GetPaymentStatusAsync(Guid attemptId, string? providerReference, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Stripe lookups must verify amount and currency: use the PaymentStatusQuery overload.");

    public async Task<ProviderPaymentState> GetPaymentStatusAsync(PaymentStatusQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query.ProviderReference))
            return ProviderPaymentState.NotFound;

        var session = await GetSessionAsync(query.ProviderReference, cancellationToken);
        if (session is null)
            return ProviderPaymentState.NotFound;

        using (session)
        {
            var root = session.RootElement;
            var minor = CurrencyAmounts.ToMinorUnits(query.Amount, query.Currency);

            // Facts that must match OUR record before the answer means anything. A mismatch is "unknown" (the caller
            // keeps the attempt unresolved), never success.
            if (Str(root, "client_reference_id") != query.AttemptId.ToString()
                || Long(root, "amount_total") != minor
                || !string.Equals(Str(root, "currency"), query.Currency, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Stripe session does not match the attempt's amount, currency or reference.");

            return (Str(root, "status"), Str(root, "payment_status")) switch
            {
                ("complete", "paid") => ProviderPaymentState.Succeeded,
                ("complete", "unpaid") => ProviderPaymentState.Pending, // delayed payment method still settling
                ("expired", _) => ProviderPaymentState.Failed,
                ("open", _) => ProviderPaymentState.Pending,
                var other => throw new InvalidOperationException($"Unexpected Stripe session state {other.Item1}/{other.Item2}.")
            };
        }
    }

    // ───────────── refunds ─────────────

    public async Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken = default)
    {
        var paymentIntent = await GetPaymentIntentIdAsync(request.PaymentProviderReference, cancellationToken);
        if (paymentIntent is null)
            return new ProviderRefundResult(false, false, null, "no_payment_intent", false);

        long minor;
        try
        {
            minor = CurrencyAmounts.ToMinorUnits(request.Amount, request.Currency);
        }
        catch (ArgumentException)
        {
            return new ProviderRefundResult(false, false, null, "amount_not_refundable", false);
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("payment_intent", paymentIntent),
            new("amount", minor.ToString(CultureInfo.InvariantCulture)),
            new("metadata[refund_id]", request.RefundId.ToString()),
            new("metadata[idempotency_key]", request.IdempotencyKey),
        };

        using var http1 = NewRequest(HttpMethod.Post, "v1/refunds", form);
        http1.Headers.Add("Idempotency-Key", request.IdempotencyKey);
        using var response = await http.SendAsync(http1, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Only a clear, non-ambiguous refusal is a rejection. A refund Stripe may already have made
            // (charge_already_refunded), rate limits and server errors stay ambiguous so the lookup decides.
            var (kind, code) = Classify(response.StatusCode, body);
            if (kind == FailureKind.Definitive && code != "charge_already_refunded")
                return new ProviderRefundResult(false, false, null, code, false);
            throw new HttpRequestException($"Stripe refund call was not conclusive (HTTP {(int)response.StatusCode}).");
        }

        using var json = JsonDocument.Parse(body);
        var refundId = Str(json.RootElement, "id");
        return MapRefundStatus(Str(json.RootElement, "status")) switch
        {
            ProviderRefundState.Succeeded => new ProviderRefundResult(true, true, refundId, null, false),
            ProviderRefundState.Pending => new ProviderRefundResult(true, false, refundId, null, false),
            _ => new ProviderRefundResult(false, false, refundId, $"refund_{Str(json.RootElement, "status")}", false)
        };
    }

    public Task<ProviderRefundState> GetRefundStatusAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Stripe refund lookups need the payment reference: use the RefundStatusQuery overload.");

    public async Task<ProviderRefundState> GetRefundStatusAsync(RefundStatusQuery query, CancellationToken cancellationToken = default)
    {
        var paymentIntent = await GetPaymentIntentIdAsync(query.PaymentProviderReference, cancellationToken);
        if (paymentIntent is null)
            return ProviderRefundState.NotFound;

        // Stripe cannot look a refund up by idempotency key (keys also expire after 24 hours), so every refund is
        // tagged with its own key in metadata and found by listing the payment's refunds.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/refunds?payment_intent={Uri.EscapeDataString(paymentIntent)}&limit=100");
        Authorize(request);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Stripe refund lookup failed (HTTP {(int)response.StatusCode}).");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        foreach (var refund in root.GetProperty("data").EnumerateArray())
        {
            if (refund.TryGetProperty("metadata", out var metadata)
                && Str(metadata, "idempotency_key") == query.IdempotencyKey
                && Str(metadata, "refund_id") == query.RefundId.ToString())
                return MapRefundStatus(Str(refund, "status"));
        }

        // Not in this page: only conclusive when the list is complete.
        if (root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("Stripe returned more refunds than one page; the lookup is not conclusive.");
        return ProviderRefundState.NotFound;
    }

    private static ProviderRefundState MapRefundStatus(string? status) => status switch
    {
        "succeeded" => ProviderRefundState.Succeeded,
        "pending" or "requires_action" => ProviderRefundState.Pending,
        "failed" or "canceled" => ProviderRefundState.Failed,
        _ => throw new InvalidOperationException($"Unexpected Stripe refund status '{status}'.")
    };

    // ───────────── webhooks ─────────────

    public WebhookParseResult Parse(WebhookRequest request)
    {
        if (!StripeSignature.IsValid(request.RawBody, request.Header("Stripe-Signature"), options.WebhookSecret, clock.GetUtcNow(), options.WebhookTolerance))
            return WebhookParseResult.Invalid();

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(request.RawBody);
        }
        catch (JsonException)
        {
            return WebhookParseResult.Malformed();
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return WebhookParseResult.Malformed();

            var eventId = Str(root, "id");
            var type = Str(root, "type");
            if (string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(type))
                return WebhookParseResult.Malformed();

            // Test and live events must never mix, and events of a connected account are not ours.
            if (!root.TryGetProperty("livemode", out var live) || live.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || live.GetBoolean() != options.LiveMode)
                return WebhookParseResult.WrongContext();
            if (root.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.String)
                return WebhookParseResult.WrongContext();

            if (type is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded" or "checkout.session.expired"))
                return WebhookParseResult.Ignored(eventId);

            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var session) || session.ValueKind != JsonValueKind.Object)
                return WebhookParseResult.Malformed();

            var reference = Str(session, "id");
            if (string.IsNullOrEmpty(reference) || !Guid.TryParse(Str(session, "client_reference_id"), out var attemptId))
                return WebhookParseResult.Malformed();

            Guid? orderId = null;
            if (session.TryGetProperty("metadata", out var metadata) && Guid.TryParse(Str(metadata, "order_id"), out var parsedOrder))
                orderId = parsedOrder;

            if (type == "checkout.session.expired")
                return new WebhookParseResult(WebhookVerification.Verified,
                    new VerifiedPaymentEvent(eventId, VerifiedEventKind.PaymentFailed, attemptId, orderId, reference, null, null, "checkout_session_expired"));

            // checkout.session.completed arrives for delayed payment methods too, before the money has moved.
            if (Str(session, "payment_status") != "paid")
                return WebhookParseResult.Ignored(eventId);

            return new WebhookParseResult(WebhookVerification.Verified,
                new VerifiedPaymentEvent(eventId, VerifiedEventKind.PaymentSucceeded, attemptId, orderId, reference,
                    Long(session, "amount_total"), Str(session, "currency"), null));
        }
    }

    // ───────────── plumbing ─────────────

    private async Task<string?> GetPaymentIntentIdAsync(string? sessionReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sessionReference))
            return null;
        var session = await GetSessionAsync(sessionReference, cancellationToken);
        if (session is null)
            return null;
        using (session)
        {
            // payment_intent is a plain id unless expanded (we never expand it).
            var pi = session.RootElement.TryGetProperty("payment_intent", out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            return string.IsNullOrEmpty(pi) ? null : pi;
        }
    }

    private async Task<JsonDocument?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/checkout/sessions/{Uri.EscapeDataString(sessionId)}");
        Authorize(request);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Stripe session lookup failed (HTTP {(int)response.StatusCode}).");
        return JsonDocument.Parse(body);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>> form)
    {
        var request = new HttpRequestMessage(method, path) { Content = new FormUrlEncodedContent(form) };
        Authorize(request);
        return request;
    }

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);

    private enum FailureKind { Definitive, Ambiguous }

    private static (FailureKind Kind, string Code) Classify(HttpStatusCode status, string body)
    {
        var code = $"http_{(int)status}";
        string? type = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                code = Str(error, "code") ?? Str(error, "type") ?? code;
                type = Str(error, "type");
            }
        }
        catch (JsonException)
        {
            // Not JSON (for example a gateway page): treat by status alone.
        }

        var ambiguous = (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict
                        || type is "idempotency_error" or "api_error" or "rate_limit_error";
        return (ambiguous ? FailureKind.Ambiguous : FailureKind.Definitive, code);
    }

    private static void ThrowIfFailed(HttpStatusCode status, string body, bool initiation)
    {
        if ((int)status is >= 200 and < 300)
            return;
        var (kind, code) = Classify(status, body);
        if (initiation && kind == FailureKind.Definitive)
            throw new ProviderRejectedException(code);
        throw new HttpRequestException($"Stripe call was not conclusive (HTTP {(int)status}).");
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}

/// <summary>
/// Manual verification of the <c>Stripe-Signature</c> header exactly as documented
/// (https://docs.stripe.com/webhooks#verify-manually): <c>t=</c> timestamp and one or more <c>v1=</c> HMAC-SHA256
/// signatures of <c>"{t}.{rawBody}"</c> keyed with the endpoint secret; other schemes are ignored (downgrade protection),
/// comparison is constant-time and the timestamp must be within the tolerance (replay protection).
/// </summary>
public static class StripeSignature
{
    public static bool IsValid(string rawBody, string? header, string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret))
            return false;

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (key == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
                timestamp = t;
            else if (key == "v1")
                signatures.Add(value);
        }

        if (timestamp is null || signatures.Count == 0)
            return false;

        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if (age.Duration() > tolerance)
            return false;

        var expected = Compute(rawBody, timestamp.Value, secret);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        return signatures.Any(sig => CryptographicOperations.FixedTimeEquals(expectedBytes, Encoding.ASCII.GetBytes(sig.ToLowerInvariant())));
    }

    /// <summary>The lowercase hex HMAC-SHA256 of <c>"{timestamp}.{body}"</c>.</summary>
    public static string Compute(string rawBody, long timestamp, string secret)
    {
        var signedPayload = $"{timestamp.ToString(CultureInfo.InvariantCulture)}.{rawBody}";
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signedPayload))).ToLowerInvariant();
    }
}

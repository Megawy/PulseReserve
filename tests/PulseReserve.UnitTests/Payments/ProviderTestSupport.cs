using System.Net;
using System.Text;
using PulseReserve.Modules.Payments.Providers.Paymob;
using PulseReserve.Modules.Payments.Providers.Stripe;

namespace PulseReserve.UnitTests.Payments;

/// <summary>A scripted HTTP handler: no network, every request recorded. No test here ever reaches a real provider.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];
    public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    public sealed record RecordedRequest(HttpMethod Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        /// <summary>Form-encoded body as key/value pairs (Stripe's request format).</summary>
        public Dictionary<string, string> Form() =>
            Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString((p.Length > 1 ? p[1] : string.Empty).Replace('+', ' ')));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, headers, body);
        Requests.Add(recorded);
        return Respond(recorded);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class TestProviders
{
    // Obviously fake values: no real credential is ever needed or used.
    public const string StripeSecret = "sk_test_unit_test_only_0000";
    public const string StripeWebhookSecret = "whsec_unit_test_only_0000";
    public const string PaymobSecret = "egy_sk_test_unit_test_only_0000";
    public const string PaymobPublic = "egy_pk_test_unit_test_only_0000";
    public const string PaymobHmacSecret = "paymob_hmac_unit_test_only_0000";
    public static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static StripeOptions StripeOptionsFor(params string[] currencies) => new(
        StripeSecret, StripeWebhookSecret, false, currencies.Length == 0 ? ["USD"] : currencies,
        new Uri("https://shop.example.test"), new Uri(StripeOptions.DefaultApiBaseUrl));

    public static (StripePaymentProvider Provider, StubHandler Handler, FixedClock Clock) Stripe(params string[] currencies)
    {
        var handler = new StubHandler();
        var clock = new FixedClock(Now);
        var http = new HttpClient(handler) { BaseAddress = new Uri(StripeOptions.DefaultApiBaseUrl) };
        return (new StripePaymentProvider(StripeOptionsFor(currencies), http, clock), handler, clock);
    }

    public static PaymobOptions PaymobOptionsFor(params string[] currencies) => new(
        PaymobSecret, PaymobPublic, PaymobHmacSecret, [4097558L, 4097559L], currencies.Length == 0 ? ["EGP"] : currencies, false,
        new Uri(PaymobOptions.DefaultApiBaseUrl), new Uri(PaymobOptions.DefaultCheckoutBaseUrl),
        new Uri("https://shop.example.test"), new Uri("https://api.example.test/api/webhooks/paymob"));

    public static (PaymobPaymentProvider Provider, StubHandler Handler) Paymob(params string[] currencies)
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(PaymobOptions.DefaultApiBaseUrl) };
        return (new PaymobPaymentProvider(PaymobOptionsFor(currencies), http), handler);
    }
}

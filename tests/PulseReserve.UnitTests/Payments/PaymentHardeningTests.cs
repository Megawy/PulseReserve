using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using PulseReserve.AppHost.Api;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers.Stripe;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>Collects everything a component logs (message text and structured values) so tests can prove nothing sensitive is in it.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
            text += " | " + string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"));
        if (exception is not null)
            text += " | " + exception;
        Lines.Add(text);
    }
}

public sealed class CheckoutUrlPolicyTests
{
    private const string Url = "https://checkout.example.test/pay?clientSecret=s";

    private static PaymentAttempt Attempt(string status = PaymentAttemptStatus.Initiated, bool simulated = false, string? url = Url) =>
        new(Guid.NewGuid(), 1, status, simulated ? "Simulator" : "Stripe", simulated, "ref", DateTime.UtcNow, null, url);

    [Fact]
    public void ARealProvidersInFlightAttempt_OnAnOrderAwaitingPayment_ShowsItsHostedPage()
    {
        CheckoutUrlPolicy.Visible(OrderStatus.PendingPayment, Attempt()).Should().Be(Url);
    }

    [Theory]
    [InlineData(PaymentAttemptStatus.Succeeded)]
    [InlineData(PaymentAttemptStatus.Failed)]
    [InlineData(PaymentAttemptStatus.Cancelled)]
    public void AnAttemptThatIsNoLongerInFlight_NeverShowsALink(string status)
    {
        CheckoutUrlPolicy.Visible(OrderStatus.PendingPayment, Attempt(status)).Should().BeNull();
    }

    [Theory]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.RefundRequired)]
    [InlineData(OrderStatus.Refunded)]
    public void AnOrderThatIsNoLongerAwaitingPayment_NeverShowsALink_EvenIfTheAttemptRowIsStillInitiated(string orderStatus)
    {
        // An overdue order is reported as Expired while its attempt row can still say Initiated until the worker closes it.
        CheckoutUrlPolicy.Visible(orderStatus, Attempt()).Should().BeNull();
    }

    [Fact]
    public void TheSimulator_AndMissingUrls_NeverProduceALink()
    {
        CheckoutUrlPolicy.Visible(OrderStatus.PendingPayment, Attempt(simulated: true)).Should().BeNull();
        CheckoutUrlPolicy.Visible(OrderStatus.PendingPayment, Attempt(url: null)).Should().BeNull();
        CheckoutUrlPolicy.Visible(OrderStatus.PendingPayment, Attempt(url: "")).Should().BeNull();
        CheckoutUrlPolicy.Visible(OrderStatus.PendingPayment, null).Should().BeNull();
    }
}

public sealed class PaymentExposureTests
{
    [Fact]
    public void CustomerPaymentResponses_CarryNoProviderReferencesKeysOrSecrets()
    {
        // The public contract is checked by shape: if someone adds a sensitive field to a customer response, this fails.
        var forbidden = new[] { "reference", "secret", "idempotency", "hmac", "signature", "apikey", "webhook", "credential", "password" };
        var allowedExceptions = new[] { "SimulatorToken" }; // the simulator's per-attempt token: owner only, development/test only

        foreach (var type in new[] { typeof(PaymentAttemptResponse), typeof(OrderResponse), typeof(PaymentOptionResponse), typeof(PaymentOptionsResponse), typeof(OrderLineResponse), typeof(StartPaymentApiRequest) })
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => !allowedExceptions.Contains(p.Name)))
                forbidden.Should().NotContain(f => property.Name.Contains(f, StringComparison.OrdinalIgnoreCase), $"{type.Name}.{property.Name}");
        }
    }

    [Fact]
    public void StartingAPayment_AcceptsOnlyAProviderName_NoAmountCurrencyOrUrl()
    {
        typeof(StartPaymentApiRequest).GetProperties().Select(p => p.Name).Should().Equal("Provider");
    }

    [Fact]
    public void AWebhookResponse_NeverEchoesTheEventOrAnySecret()
    {
        // The endpoint answers with { status } or { status, reason } and a code from this closed set only.
        var reasons = typeof(PaymentWebhookService).Assembly.GetType("PulseReserve.Modules.Payments.Features.WebhookResult");
        reasons!.GetProperties().Select(p => p.Name).Should().BeEquivalentTo("Outcome", "Reason");
    }
}

public sealed class StripeLivemodeTests
{
    [Fact]
    public void AnEventThatDoesNotStateItsMode_IsRefused_BecauseItCannotBePlacedInTestOrLive()
    {
        var (provider, _, clock) = TestProviders.Stripe();
        var body = JsonSerializer.Serialize(new
        {
            id = "evt_1",
            type = "checkout.session.completed",
            data = new { @object = new { id = "cs_1", payment_status = "paid", amount_total = 100, currency = "usd", client_reference_id = Guid.NewGuid().ToString() } }
        });
        var t = clock.Now.ToUnixTimeSeconds();
        var request = new WebhookRequest(
            body, new Dictionary<string, string> { ["Stripe-Signature"] = $"t={t},v1={StripeSignature.Compute(body, t, TestProviders.StripeWebhookSecret)}" },
            new Dictionary<string, string>());

        provider.Parse(request).Status.Should().Be(WebhookVerification.WrongContext);
    }
}

public sealed class NothingSensitiveIsLoggedTests
{
    private static readonly string[] Secrets =
    [
        TestProviders.StripeSecret, TestProviders.StripeWebhookSecret, TestProviders.PaymobSecret, TestProviders.PaymobPublic,
        TestProviders.PaymobHmacSecret, "egy_csk_test_x"
    ];

    [Fact]
    public async Task WebhookHandling_LogsIdsAndOutcomesOnly_NeverSecretsSignaturesOrClientSecrets()
    {
        var logger = new CapturingLogger<PaymentWebhookService>();
        var store = new InMemoryOrderStore("USD", 91m);
        var gateway = new ConfigurableGateway();
        var clock = new FixedClock(TestProviders.Now);
        var stripeHttp = new StubHandler();
        var paymobHttp = new StubHandler();
        var stripe = new StripePaymentProvider(TestProviders.StripeOptionsFor("USD"), new HttpClient(stripeHttp) { BaseAddress = new Uri("https://api.stripe.com/") }, clock);
        var paymob = new Modules.Payments.Providers.Paymob.PaymobPaymentProvider(TestProviders.PaymobOptionsFor("USD"), new HttpClient(paymobHttp) { BaseAddress = new Uri("https://accept.paymob.com/") });
        var registry = new PaymentProviderRegistry([stripe, paymob]);
        var payments = new PaymentService(store, registry, gateway);
        var webhooks = new PaymentWebhookService(registry, new InMemoryAttemptLookup(store), store, payments, new InMemoryAnomalyRecorder(), logger);

        stripeHttp.Respond = r => StubHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            id = "cs_test_1", status = "open", payment_status = "unpaid", url = "https://checkout.stripe.com/c/pay/cs_test_1", amount_total = 9100,
            currency = "usd", client_reference_id = r.Form()["client_reference_id"]
        }));
        var started = await payments.InitiateAsync(store.CustomerId, store.Order.OrderId, "k1", "Stripe");
        var attempt = store.Attempts.Single();
        var t = clock.Now.ToUnixTimeSeconds();

        string Event(long amount, string type = "checkout.session.completed") => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "evt_1", ["type"] = type, ["livemode"] = false,
            ["data"] = new { @object = new Dictionary<string, object?> { ["id"] = "cs_test_1", ["payment_status"] = "paid", ["amount_total"] = amount, ["currency"] = "usd", ["client_reference_id"] = attempt.Id.ToString(), ["metadata"] = new { attempt_id = attempt.Id.ToString(), order_id = store.Order.OrderId.ToString() } } }
        });
        WebhookRequest Signed(string body, string secret) => new(body,
            new Dictionary<string, string> { ["Stripe-Signature"] = $"t={t},v1={StripeSignature.Compute(body, t, secret)}" }, new Dictionary<string, string>());

        var signatures = new List<string>();
        foreach (var request in new[]
                 {
                     Signed(Event(9100), "whsec_wrong_secret_value"),   // forged
                     Signed(Event(1), TestProviders.StripeWebhookSecret), // amount mismatch
                     Signed(Event(9100), TestProviders.StripeWebhookSecret), // success
                     Signed(Event(9100), TestProviders.StripeWebhookSecret), // duplicate
                 })
        {
            signatures.Add(request.Header("Stripe-Signature")!);
            await webhooks.HandleAsync("Stripe", request);
        }

        await webhooks.HandleAsync("Paymob", new WebhookRequest("{\"type\":\"TRANSACTION\",\"obj\":{}}", new Dictionary<string, string>(), new Dictionary<string, string> { ["hmac"] = "deadbeef" }));
        await webhooks.HandleAsync("Stripe", new WebhookRequest("not json", new Dictionary<string, string>(), new Dictionary<string, string>()));

        started.IsSuccess.Should().BeTrue();
        logger.Lines.Should().NotBeEmpty("rejections and outcomes are logged");
        var everything = string.Join("\n", logger.Lines);
        foreach (var secret in Secrets.Concat(signatures.SelectMany(s => s.Split(',').Select(p => p.Split('=', 2)[^1]).Where(v => v.Length > 20))))
            everything.Should().NotContain(secret);
        everything.Should().NotContain("deadbeef").And.NotContain("customer", "no customer identifiers either");
        everything.Should().NotContain(store.CustomerId.ToString());
    }
}

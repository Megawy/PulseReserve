using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Webhook query-string logging, against the real ASP.NET Core hosting pipeline (in-memory test server).
/// </summary>
public sealed class WebhookLoggingTests
{
    private const string Secret = "HMAC_SIGNATURE_VALUE_abc123XYZ";

    private static string AllLogs(PaymentHttpHost host)
    {
        lock (host.Logs.Lines)
            return string.Join("\n", host.Logs.Lines);
    }

    [Fact]
    public async Task WithoutRedaction_AspNetCoreHostingLogsTheWebhookQueryString_ThisIsTheDefectBeingFixed()
    {
        await using var host = await PaymentHttpHost.StartAsync(redaction: false, paymob: true, verboseFramework: true);

        await host.Client.PostAsync($"/api/webhooks/paymob?hmac={Secret}", new StringContent("{}", Encoding.UTF8, "application/json"));

        AllLogs(host).Should().Contain(Secret, "the hosting layer logs 'Request starting ... path?query' before any middleware can intervene");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task WithRedaction_TheWebhookSignatureIsNotInAnyLogLine_InEveryEnvironment(string environment)
    {
        await using var host = await PaymentHttpHost.StartAsync(environment: environment, paymob: true, verboseFramework: true);

        var response = await host.Client.PostAsync($"/api/webhooks/paymob?hmac={Secret}&other=VALUE_TWO", new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest); // not a valid callback: rejected, and still not logged
        var logs = AllLogs(host);
        logs.Should().NotContain(Secret).And.NotContain("VALUE_TWO");
        logs.Should().Contain("/api/webhooks/paymob", "the request is still logged, only its query string is hidden");
    }

    [Fact]
    public async Task TheRedactionIsInvisibleToTheEndpoint_AValidSignedCallbackStillVerifiesAndPaysTheOrder()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);
        host.PaymobHttp.Respond = _ => StubHandler.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { id = "pi", client_secret = "egy_csk_test_x", intention_order_id = 217503754 }));
        (await host.StartPaymentAsync("{\"provider\":\"Paymob\"}")).StatusCode.Should().Be(HttpStatusCode.Created);
        var attempt = host.Store.Attempts.Single();

        var request = host.Webhook.Paymob(attempt);
        var hmac = request.RequestUri!.OriginalString[(request.RequestUri.OriginalString.IndexOf('?') + 1)..]; // "hmac=<signature>"
        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        host.Store.Order.Status.Should().Be(OrderStatus.Paid);
        AllLogs(host).Should().NotContain(hmac[5..], "the verified signature is not in the logs either");
    }

    [Fact]
    public async Task OnlyWebhookRequestsAreRedacted_OtherRequestsKeepTheirUsefulLogging()
    {
        await using var host = await PaymentHttpHost.StartAsync();

        await host.Client.GetAsync("/api/orders/00000000-0000-4000-8000-000000000001/payment-options?trace=visible_marker");

        AllLogs(host).Should().Contain("trace=visible_marker");
    }

    [Fact]
    public async Task StripeSignaturesAndBodies_AreNeverWrittenToLogs()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();
        (await host.StartPaymentAsync(null)).StatusCode.Should().Be(HttpStatusCode.Created);
        var attempt = host.Store.Attempts.Single();
        var request = host.Webhook.Stripe(attempt, signature: $"t=1,v1={PaymentHttpHost.FakeSignature}");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var logs = AllLogs(host);
        logs.Should().NotContain(PaymentHttpHost.FakeSignature).And.NotContain(TestProviders.StripeWebhookSecret).And.NotContain(TestProviders.StripeSecret);
        logs.Should().NotContain("checkout.session.completed", "webhook payloads are never logged");
        logs.Should().NotContain(host.CustomerId.ToString(), "no customer identifier either");
    }
}

/// <summary>
/// HTTP-level behaviour of the start-payment and webhook endpoints, against the REAL endpoints, policies and antiforgery
/// filter in an in-memory host with an in-memory store. Status codes, binding and "nothing was created" are proven here;
/// transactional guarantees are not (see the integration-test checklist).
/// </summary>
public sealed class PaymentEndpointHttpTests
{
    private static async Task<string> ProblemTitleAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.TryGetProperty("title", out var title) ? title.GetString()! : body;
    }

    private static void NothingChanged(PaymentHttpHost host)
    {
        host.Store.Attempts.Should().BeEmpty("a rejected request must not create a partial payment attempt");
        host.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
        host.Store.Calls.Should().NotContain(c => c.StartsWith("Begin"));
    }

    // ───────────── start payment ─────────────

    [Fact]
    public async Task AnEmptyBodyWithNoContentType_IsFine_WhenOnlyOneProviderIsEnabled()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();

        var response = await host.StartPaymentAsync(body: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        host.Store.Attempts.Should().ContainSingle().Which.Provider.Should().Be("Stripe");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task AnEmptyOrNullJsonBody_IsTreatedAsNoProviderChosen(string body)
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();

        var response = await host.StartPaymentAsync(body);

        // Empty body with a JSON content type is the one shape the framework may refuse; either way it must be predictable.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.BadRequest);
        if (response.StatusCode == HttpStatusCode.BadRequest)
            NothingChanged(host);
        else
            host.Store.Attempts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"provider\": ")]
    [InlineData("{\"provider\": 5}")]
    [InlineData("{\"provider\": [\"Stripe\"]}")]
    [InlineData("[]")]
    [InlineData("\"Stripe\"")]
    public async Task MalformedOrWronglyTypedJson_IsA400_AndCreatesNothing(string body)
    {
        // Production behaviour: only Development shows the framework's developer exception page.
        await using var host = await PaymentHttpHost.StartAsync(environment: "Production");
        host.StripeBehaves();

        var response = await host.StartPaymentAsync(body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        NothingChanged(host);
        host.StripeHttp.Requests.Should().BeEmpty("the provider is never called for a request that did not bind");
        (await response.Content.ReadAsStringAsync()).Should().NotContainAny("Exception", "   at ", "StackTrace");
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/xml")]
    public async Task ANonJsonContentTypeWithABody_IsA415_AndCreatesNothing(string contentType)
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();

        var response = await host.StartPaymentAsync("{\"provider\":\"Stripe\"}", contentType);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        NothingChanged(host);
    }

    [Fact]
    public async Task AnUnsupportedProvider_IsA400_WithoutAnAttempt()
    {
        await using var host = await PaymentHttpHost.StartAsync();

        foreach (var provider in new[] { "Adyen", "Simulator", "https://evil.example/pay", "stripe; DROP TABLE", new string('x', 500) })
        {
            var response = await host.StartPaymentAsync(JsonSerializer.Serialize(new { provider }), key: $"k-{provider.Length}");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ProblemTitleAsync(response)).Should().Be("Payment.ProviderNotEnabled");
        }

        NothingChanged(host);
    }

    [Fact]
    public async Task AClientCannotInfluenceTheAmountCurrencyOrUrl_ExtraFieldsAreIgnoredAndTheChargeComesFromTheOrder()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();

        var response = await host.StartPaymentAsync("{\"provider\":\"Stripe\",\"amount\":1,\"currency\":\"EUR\",\"successUrl\":\"https://evil.example\",\"orderId\":\"00000000-0000-0000-0000-000000000001\"}");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var form = host.StripeHttp.Requests.Single().Form();
        form["line_items[0][price_data][unit_amount]"].Should().Be("9100");
        form["line_items[0][price_data][currency]"].Should().Be("usd");
        form["success_url"].Should().StartWith("https://shop.example.test/");
    }

    [Fact]
    public async Task AMissingOrOverlongIdempotencyKey_IsA400_WithoutAnAttempt()
    {
        await using var host = await PaymentHttpHost.StartAsync();

        var missing = await host.StartPaymentAsync(null, key: null);
        var tooLong = await host.StartPaymentAsync(null, key: new string('k', OrderPolicy.MaxIdempotencyKeyLength + 1));

        (await ProblemTitleAsync(missing)).Should().Be("Order.MissingIdempotencyKey");
        (await ProblemTitleAsync(tooLong)).Should().Be("Order.IdempotencyKeyTooLong");
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        NothingChanged(host);
    }

    [Fact]
    public async Task WithoutASession_ACsrfTokenOrOwnership_NothingHappens()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();

        var anonymous = await host.StartPaymentAsync(null, signedIn: false, csrf: false);
        var noCsrf = await host.StartPaymentAsync(null, csrf: false);
        var stranger = await host.StartPaymentAsync(null, user: Guid.NewGuid());
        var otherOrder = await host.StartPaymentAsync(null, orderId: Guid.NewGuid());

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        noCsrf.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemTitleAsync(noCsrf)).Should().Be("Csrf.Invalid");
        stranger.StatusCode.Should().Be(HttpStatusCode.NotFound, "someone else's order looks exactly like a missing one");
        otherOrder.StatusCode.Should().Be(HttpStatusCode.NotFound);
        NothingChanged(host);
        host.StripeHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WithSeveralProvidersEnabled_ARequestWithoutAProviderIsRefused_AndCreatesNothing()
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);

        var response = await host.StartPaymentAsync(null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTitleAsync(response)).Should().Be("Payment.ProviderRequired");
        NothingChanged(host);
    }

    [Fact]
    public async Task WithNoProviderEnabled_PaymentIsA503_AndCreatesNothing()
    {
        await using var host = await PaymentHttpHost.StartAsync(stripe: false);

        var response = await host.StartPaymentAsync(null);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemTitleAsync(response)).Should().Be("Payment.ProviderUnavailable");
        NothingChanged(host);
    }

    [Fact]
    public async Task APaymentResponse_NeverContainsTheProviderReferenceOrAnySecret_AndOnlyShowsAUsableCheckoutLink()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();

        var response = await host.StartPaymentAsync(null);
        var body = await response.Content.ReadAsStringAsync();

        JsonDocument.Parse(body).RootElement.GetProperty("payment").TryGetProperty("providerReference", out _).Should().BeFalse("the provider reference is not a response field");
        body.Should().NotContain(TestProviders.StripeSecret).And.NotContain(TestProviders.StripeWebhookSecret).And.NotContain("providerReference");
        JsonDocument.Parse(body).RootElement.GetProperty("payment").GetProperty("checkoutUrl").GetString().Should().StartWith("https://checkout.stripe.com/");
    }

    // ───────────── webhooks ─────────────

    [Fact]
    public async Task Webhooks_AreAnonymous_NeedNoCookieOrCsrf_AndAreDecidedBySignatureAlone()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();
        await host.StartPaymentAsync(null);
        var attempt = host.Store.Attempts.Single();

        var forged = await host.Client.SendAsync(host.Webhook.Stripe(attempt, signature: "t=1,v1=00"));
        var genuine = await host.Client.SendAsync(host.Webhook.Stripe(attempt));

        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        genuine.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Store.Order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task AWebhookWithTheWrongContentType_IsStillDecidedBySignature_NotByHeaders()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();
        await host.StartPaymentAsync(null);
        var attempt = host.Store.Attempts.Single();

        var response = await host.Client.SendAsync(host.Webhook.Stripe(attempt, contentType: "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the signature covers the exact bytes; the content type is irrelevant");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"id\":\"evt\"}")]
    public async Task AnEmptyOrMalformedWebhookBody_IsA400_AndChangesNothing(string body)
    {
        await using var host = await PaymentHttpHost.StartAsync(paymob: true);
        host.StripeBehaves();
        await host.StartPaymentAsync(null);

        foreach (var path in new[] { "/api/webhooks/stripe", "/api/webhooks/paymob?hmac=00" })
        {
            var response = await host.Client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, path);
        }

        host.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
        host.Store.FinalizeCalls.Should().Be(0);
    }

    [Fact]
    public async Task UnsupportedProvidersAndMethods_ArePredictableClientErrors()
    {
        await using var host = await PaymentHttpHost.StartAsync();

        (await host.Client.PostAsync("/api/webhooks/adyen", new StringContent("{}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.Client.PostAsync("/api/webhooks/paymob", new StringContent("{}"))).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "Paymob is not enabled here");
        (await host.Client.GetAsync("/api/webhooks/stripe")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task OversizedWebhookBodies_AreRefusedWith413_BothWithAndWithoutAContentLength()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        var big = new string('x', PulseReserve.AppHost.Api.PaymentProviderEndpoints.MaxWebhookBytes + 10);

        var declared = await host.Client.PostAsync("/api/webhooks/stripe", new StringContent(big));
        var chunked = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big))) };
        chunked.Headers.TransferEncodingChunked = true;
        var undeclared = await host.Client.SendAsync(chunked);

        declared.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        undeclared.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        host.Store.FinalizeCalls.Should().Be(0);
    }

    [Fact]
    public async Task AWebhookAnswer_CarriesOnlyAStatusAndAReasonCode()
    {
        await using var host = await PaymentHttpHost.StartAsync();
        host.StripeBehaves();
        await host.StartPaymentAsync(null);
        var attempt = host.Store.Attempts.Single();

        var rejected = await host.Client.SendAsync(host.Webhook.Stripe(attempt, amount: 5));
        var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement;

        rejected.StatusCode.Should().Be(HttpStatusCode.OK, "the evidence is stored, so the provider must not retry it");
        json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("status", "reason");
        json.GetProperty("status").GetString().Should().Be("recorded_for_review");
        json.GetProperty("reason").GetString().Should().Be("amount_or_currency_mismatch");
        host.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
    }
}

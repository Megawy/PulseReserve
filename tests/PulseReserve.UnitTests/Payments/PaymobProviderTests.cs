using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers.Paymob;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Paymob adapter contract tests against a fake HTTP handler. The request shape follows Paymob's documented Intention API
/// and Unified Checkout redirect (https://developers.paymob.com/), and the HMAC rules follow the documented transaction
/// callback procedure, including its published sample string. Nothing here talks to Paymob: this is NOT sandbox
/// verification. Payment status lookup and refunds are deliberately not implemented (no verified endpoint is documented),
/// and the tests pin that behaviour.
/// </summary>
public sealed class PaymobProviderTests
{
    private static readonly Guid Attempt = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Order = Guid.Parse("22222222-2222-4222-8222-222222222222");

    // ───────────── initiation ─────────────

    private const string IntentionResponse =
        """{"id":"pi_abc","client_secret":"egy_csk_test_secret_value","intention_order_id":217503754,"status":"intended"}""";

    [Fact]
    public async Task Initiate_PostsTheIntention_WithServerAuthoritativeFields_AndRedirectsToHostedCheckout()
    {
        var (provider, handler) = TestProviders.Paymob("EGP");
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.Created, IntentionResponse);

        var session = await provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 91.5m, "EGP"));

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/v1/intention/");
        request.Headers["Authorization"].Should().Be($"Token {TestProviders.PaymobSecret}");
        var body = JsonNode.Parse(request.Body)!;
        ((long)body["amount"]!).Should().Be(9150, "91.50 EGP in piasters, from the order");
        ((string)body["currency"]!).Should().Be("EGP");
        body["payment_methods"]!.AsArray().Select(n => (long)n!).Should().Equal(4097558L, 4097559L);
        ((string)body["special_reference"]!).Should().Be(Attempt.ToString());
        ((string)body["notification_url"]!).Should().Be("https://api.example.test/api/webhooks/paymob");
        ((string)body["redirection_url"]!).Should().Be($"https://shop.example.test/account/orders/{Order}?payment=return");
        var items = body["items"]!.AsArray();
        items.Should().ContainSingle();
        ((long)items[0]!["amount"]!).Should().Be(9150, "the documented rule: the amount equals the sum of the items");
        request.Body.Should().NotContain(TestProviders.PaymobSecret);
        request.Body.Should().NotContain("billing_data", "no customer data is sent");

        session.ProviderReference.Should().Be("217503754", "the Paymob order id is what callbacks carry as order.id");
        session.CheckoutUrl.Should().Be(
            $"https://eg.checkout.paymob.com/?publicKey={TestProviders.PaymobPublic}&clientSecret=egy_csk_test_secret_value");
    }

    [Fact]
    public async Task Initiate_RefusesACurrencyThatIsNotEnabled_WithoutCallingPaymob()
    {
        var (provider, handler) = TestProviders.Paymob("EGP");

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "USD"));

        (await act.Should().ThrowAsync<ProviderRejectedException>()).Which.Code.Should().Be("currency_not_enabled");
        handler.Requests.Should().BeEmpty();
        provider.SupportsCurrency("egp").Should().BeTrue();
        provider.SupportsCurrency("USD").Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Initiate_ADefinitiveRejection_IsReportedAsRejected(HttpStatusCode status)
    {
        var (provider, handler) = TestProviders.Paymob();
        handler.Respond = _ => StubHandler.Json(status, """{"detail":"nope"}""");

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "EGP"));

        (await act.Should().ThrowAsync<ProviderRejectedException>()).Which.Code.Should().Be($"http_{(int)status}");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Initiate_AnAmbiguousFailure_IsNeverTreatedAsARefusal(HttpStatusCode status)
    {
        var (provider, handler) = TestProviders.Paymob();
        handler.Respond = _ => StubHandler.Json(status, "{}");

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "EGP"));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<ProviderRejectedException>();
    }

    [Fact]
    public async Task Initiate_ATimeout_IsAmbiguous()
    {
        var (provider, handler) = TestProviders.Paymob();
        handler.Respond = _ => throw new TaskCanceledException("timed out");

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "EGP"));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<ProviderRejectedException>();
    }

    [Theory]
    [InlineData("""{"id":"x","intention_order_id":1}""")]
    [InlineData("""{"id":"x","client_secret":"s"}""")]
    [InlineData("not json")]
    public async Task Initiate_AnUnusableAnswer_IsNotARefusalAndNeverYieldsACheckoutUrl(string body)
    {
        var (provider, handler) = TestProviders.Paymob();
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.Created, body);

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "EGP"));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<ProviderRejectedException>();
    }

    [Fact]
    public async Task Initiate_EscapesTheKeysInTheCheckoutUrl()
    {
        var (provider, handler) = TestProviders.Paymob();
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.Created, """{"client_secret":"a b&c","intention_order_id":"77"}""");

        var session = await provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "EGP"));

        session.CheckoutUrl.Should().EndWith("clientSecret=a%20b%26c");
        session.ProviderReference.Should().Be("77");
    }

    // ───────────── what is NOT implemented ─────────────

    [Fact]
    public async Task Lookup_AndRefunds_AreNotImplemented_AndSaySoInsteadOfGuessing()
    {
        var (provider, handler) = TestProviders.Paymob();

        provider.SupportsPaymentLookup.Should().BeFalse();
        provider.SupportsRefunds.Should().BeFalse();
        await ((Func<Task>)(() => provider.GetPaymentStatusAsync(Attempt, "1"))).Should().ThrowAsync<NotSupportedException>();
        await ((Func<Task>)(() => provider.RefundAsync(new ProviderRefundRequest(Guid.NewGuid(), "k", "1", 1m, "EGP")))).Should().ThrowAsync<NotSupportedException>();
        await ((Func<Task>)(() => provider.GetRefundStatusAsync("k"))).Should().ThrowAsync<NotSupportedException>();
        handler.Requests.Should().BeEmpty("no endpoint is invented");
        provider.IsSimulated.Should().BeFalse();
    }

    // ───────────── HMAC ─────────────

    // The sample published with the documentation's HMAC procedure: its concatenated string is the fixture. The HMAC value
    // on that page is keyed with Paymob's own secret, so it cannot be reproduced here; the concatenation (field list,
    // order and stringification) is what these tests pin to the documentation.
    private const string DocumentedConcatenation =
        "1000002024-06-13T11:33:44.592345EGPfalsefalse1920364654097558truefalsefalsefalsetruefalse217503754302852false2346MasterCardcardtrue";

    private static JsonNode DocumentedSampleObj() => JsonNode.Parse(
        """
        {"id":192036465,"pending":false,"amount_cents":100000,"success":true,"is_auth":false,"is_capture":false,"is_standalone_payment":true,
         "is_voided":false,"is_refunded":false,"is_3d_secure":true,"integration_id":4097558,"has_parent_transaction":false,"order":{"id":217503754,"merchant_order_id":null},
         "created_at":"2024-06-13T11:33:44.592345","currency":"EGP","error_occured":false,"owner":302852,
         "source_data":{"type":"card","pan":"2346","sub_type":"MasterCard"}}
        """)!;

    [Fact]
    public void Hmac_ConcatenatesTheDocumentedFieldsInTheDocumentedOrder()
    {
        var obj = JsonDocument.Parse(DocumentedSampleObj().ToJsonString()).RootElement;

        PaymobHmac.BuildPayload(obj).Should().Be(DocumentedConcatenation);
        PaymobHmac.Fields.Should().HaveCount(20);
    }

    [Fact]
    public void Hmac_IsHmacSha512OfThatString_LowercaseHex_ComparedInConstantTime()
    {
        var obj = JsonDocument.Parse(DocumentedSampleObj().ToJsonString()).RootElement;
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA512.HashData(
            Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes(DocumentedConcatenation))).ToLowerInvariant();

        PaymobHmac.Compute(obj, "secret").Should().Be(expected).And.HaveLength(128);
        PaymobHmac.IsValid(obj, expected, "secret").Should().BeTrue();
        PaymobHmac.IsValid(obj, expected.ToUpperInvariant(), "secret").Should().BeTrue("hex case is not significant");
        PaymobHmac.IsValid(obj, expected, "other secret").Should().BeFalse();
        PaymobHmac.IsValid(obj, null, "secret").Should().BeFalse();
        PaymobHmac.IsValid(obj, "", "secret").Should().BeFalse();
    }

    // ───────────── callbacks ─────────────

    private static WebhookRequest Callback(Action<JsonObject>? change = null, bool sign = true, string secret = TestProviders.PaymobHmacSecret,
        string type = "TRANSACTION", string? hmacOverride = null)
    {
        var obj = (JsonObject)JsonNode.Parse(
            $$$"""
            {"id":192036465,"pending":false,"amount_cents":9100,"success":true,"is_auth":false,"is_capture":false,"is_standalone_payment":true,
             "is_voided":false,"is_refunded":false,"is_3d_secure":true,"integration_id":4097558,"has_parent_transaction":false,
             "order":{"id":217503754,"merchant_order_id":"{{{Attempt}}}"},"created_at":"2030-01-01T11:33:44.592345","currency":"EGP",
             "error_occured":false,"owner":302852,"source_data":{"type":"card","pan":"2346","sub_type":"MasterCard"}}
            """)!;
        change?.Invoke(obj);
        var body = new JsonObject { ["type"] = type, ["obj"] = obj }.ToJsonString();
        var hmac = hmacOverride ?? (sign ? PaymobHmac.Compute(JsonDocument.Parse(obj.ToJsonString()).RootElement, secret) : null);
        var query = hmac is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["hmac"] = hmac };
        return new WebhookRequest(body, new Dictionary<string, string>(), query);
    }

    [Fact]
    public void Callback_AValidSuccess_IsVerifiedAndReducedToTheFactsWeCheck()
    {
        var (provider, _) = TestProviders.Paymob();

        var result = provider.Parse(Callback());

        result.Status.Should().Be(WebhookVerification.Verified);
        var evt = result.Event!;
        evt.Kind.Should().Be(VerifiedEventKind.PaymentSucceeded);
        evt.AttemptId.Should().Be(Attempt);
        evt.ProviderReference.Should().Be("217503754");
        evt.AmountMinor.Should().Be(9100);
        evt.Currency.Should().Be("EGP");
        evt.EventId.Should().Be("paymob:192036465");
    }

    [Fact]
    public void Callback_ABadOrMissingHmac_IsRejected()
    {
        var (provider, _) = TestProviders.Paymob();

        provider.Parse(Callback(sign: false)).Status.Should().Be(WebhookVerification.InvalidSignature);
        provider.Parse(Callback(secret: "someone else's secret")).Status.Should().Be(WebhookVerification.InvalidSignature);
        provider.Parse(Callback(hmacOverride: "00")).Status.Should().Be(WebhookVerification.InvalidSignature);
    }

    [Theory]
    [InlineData("amount_cents")]
    [InlineData("currency")]
    [InlineData("success")]
    public void Callback_ATamperedSignedField_BreaksTheSignature(string field)
    {
        var (provider, _) = TestProviders.Paymob();
        var signed = Callback();
        var tampered = JsonNode.Parse(signed.RawBody)!;
        tampered["obj"]![field] = field switch { "amount_cents" => 1, "currency" => "USD", _ => false };

        provider.Parse(signed with { RawBody = tampered.ToJsonString() }).Status.Should().Be(WebhookVerification.InvalidSignature);
    }

    [Fact]
    public void Callback_AnIntegrationThatIsNotOurs_IsAWrongContext_EvenWithAValidSignature()
    {
        var (provider, _) = TestProviders.Paymob();

        provider.Parse(Callback(o => o["integration_id"] = 999)).Status.Should().Be(WebhookVerification.WrongContext);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData(null)]
    public void Callback_WithoutOurAttemptReference_IsMalformed(string? merchantOrderId)
    {
        var (provider, _) = TestProviders.Paymob();

        provider.Parse(Callback(o => o["order"]!["merchant_order_id"] = merchantOrderId)).Status.Should().Be(WebhookVerification.Malformed);
    }

    [Theory]
    [InlineData("success", false)] // a decline: deliberately NOT a failed attempt, the customer may retry inside the same intention
    [InlineData("pending", true)]
    [InlineData("error_occured", true)]
    [InlineData("is_voided", true)]
    [InlineData("is_refunded", true)]
    [InlineData("has_parent_transaction", true)] // a refund/void/capture child transaction is not a payment
    public void Callback_ANonCapturedOrNonPaymentTransaction_IsIgnored_NeverASuccessNorAFailure(string flag, bool value)
    {
        var (provider, _) = TestProviders.Paymob();

        var result = provider.Parse(Callback(o => o[flag] = value));

        result.Status.Should().Be(WebhookVerification.Verified);
        result.Event!.Kind.Should().Be(VerifiedEventKind.Ignored);
    }

    [Fact]
    public void Callback_AnAuthorizationThatWasNeverCaptured_IsIgnored_ButACapturedOneCounts()
    {
        var (provider, _) = TestProviders.Paymob();

        provider.Parse(Callback(o => o["is_auth"] = true)).Event!.Kind.Should().Be(VerifiedEventKind.Ignored);
        provider.Parse(Callback(o => { o["is_auth"] = true; o["is_capture"] = true; })).Event!.Kind.Should().Be(VerifiedEventKind.PaymentSucceeded);
    }

    [Fact]
    public void Callback_OtherCallbackTypes_AreAcknowledgedAndIgnored()
    {
        var (provider, _) = TestProviders.Paymob();

        var result = provider.Parse(Callback(type: "TOKEN", sign: false));

        result.Status.Should().Be(WebhookVerification.Verified);
        result.Event!.Kind.Should().Be(VerifiedEventKind.Ignored);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Callback_AnUnparseableBody_IsTreatedAsForgery(string body)
    {
        var (provider, _) = TestProviders.Paymob();

        provider.Parse(new WebhookRequest(body, new Dictionary<string, string>(), new Dictionary<string, string>())).Status
            .Should().Be(WebhookVerification.InvalidSignature);
    }

    [Fact]
    public void Callback_ADuplicateDelivery_ParsesIdentically_SoTheLifecycleCanDeduplicate()
    {
        var (provider, _) = TestProviders.Paymob();
        var request = Callback();

        var first = provider.Parse(request).Event!;
        var second = provider.Parse(request).Event!;

        second.Should().Be(first);
    }
}

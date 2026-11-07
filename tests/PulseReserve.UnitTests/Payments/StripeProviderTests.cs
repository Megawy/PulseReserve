using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers.Stripe;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Stripe adapter contract tests against a fake HTTP handler. Request shapes follow Stripe's documented REST API
/// (https://docs.stripe.com/api); nothing here talks to Stripe, so these prove the adapter's logic, NOT a live or
/// sandbox integration.
/// </summary>
public sealed class StripeProviderTests
{
    private static readonly Guid Attempt = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Order = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static string SessionJson(string id = "cs_test_1", string status = "open", string payment = "unpaid", long amount = 9100,
        string currency = "usd", string? reference = null, string? paymentIntent = null, string url = "https://checkout.stripe.com/c/pay/cs_test_1") =>
        $$"""
        {"id":"{{id}}","object":"checkout.session","status":"{{status}}","payment_status":"{{payment}}","amount_total":{{amount}},
         "currency":"{{currency}}","client_reference_id":"{{reference ?? Attempt.ToString()}}","url":"{{url}}",
         "payment_intent":{{(paymentIntent is null ? "null" : $"\"{paymentIntent}\"")}}}
        """;

    // ───────────── initiation ─────────────

    [Fact]
    public async Task Initiate_SendsServerAuthoritativeAmounts_AndReturnsTheHostedCheckoutUrl()
    {
        var (provider, handler, clock) = TestProviders.Stripe("USD");
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.OK, SessionJson());

        var session = await provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 91m, "USD"));

        session.ProviderReference.Should().Be("cs_test_1");
        session.CheckoutUrl.Should().StartWith("https://checkout.stripe.com/");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/v1/checkout/sessions");
        request.Headers["Authorization"].Should().Be($"Bearer {TestProviders.StripeSecret}");
        request.Headers["Idempotency-Key"].Should().Be($"pulsereserve-attempt-{Attempt:N}");

        var form = request.Form();
        form["mode"].Should().Be("payment");
        form["line_items[0][price_data][unit_amount]"].Should().Be("9100", "91.00 USD in cents, from the order");
        form["line_items[0][price_data][currency]"].Should().Be("usd");
        form["client_reference_id"].Should().Be(Attempt.ToString());
        form["metadata[attempt_id]"].Should().Be(Attempt.ToString());
        form["metadata[order_id]"].Should().Be(Order.ToString());
        form["payment_intent_data[metadata][attempt_id]"].Should().Be(Attempt.ToString());
        form["success_url"].Should().Be($"https://shop.example.test/account/orders/{Order}?payment=return");
        form["cancel_url"].Should().Be($"https://shop.example.test/account/orders/{Order}?payment=cancelled");
        form["expires_at"].Should().Be(clock.Now.AddMinutes(31).ToUnixTimeSeconds().ToString());
        request.Body.Should().NotContain(TestProviders.StripeSecret, "the secret travels only in the header");
    }

    [Theory]
    [InlineData("JPY", 1500, 1500)] // zero-decimal currencies are not multiplied
    [InlineData("KWD", 1.234, 1234)] // three-decimal currencies
    public async Task Initiate_UsesTheCurrencyMinorUnit(string currency, double major, long expectedMinor)
    {
        var (provider, handler, _) = TestProviders.Stripe(currency);
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.OK, SessionJson(amount: expectedMinor, currency: currency.ToLowerInvariant()));

        await provider.InitiateAsync(new PaymentInitiation(Attempt, Order, (decimal)major, currency));

        handler.Requests[0].Form()["line_items[0][price_data][unit_amount]"].Should().Be(expectedMinor.ToString());
    }

    [Fact]
    public async Task Initiate_RefusesACurrencyThatIsNotEnabled_WithoutCallingStripe()
    {
        var (provider, handler, _) = TestProviders.Stripe("USD");

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "EUR"));

        (await act.Should().ThrowAsync<ProviderRejectedException>()).Which.Code.Should().Be("currency_not_enabled");
        handler.Requests.Should().BeEmpty();
        provider.SupportsCurrency("usd").Should().BeTrue();
        provider.SupportsCurrency("EUR").Should().BeFalse();
    }

    [Fact]
    public async Task Initiate_RefusesAnAmountThatIsNotRepresentable_NeverRounding()
    {
        var (provider, handler, _) = TestProviders.Stripe("JPY");

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10.5m, "JPY"));

        (await act.Should().ThrowAsync<ProviderRejectedException>()).Which.Code.Should().Be("amount_not_chargeable");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"parameter_invalid_integer"}}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"type":"invalid_request_error","message":"Invalid API Key provided"}}""")]
    public async Task Initiate_ADefinitiveRejection_IsReportedAsRejected(HttpStatusCode status, string body)
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => StubHandler.Json(status, body);

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "USD"));

        await act.Should().ThrowAsync<ProviderRejectedException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"type":"api_error"}}""")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"type":"rate_limit_error"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"type":"idempotency_error","message":"keys must be unique per request"}}""")]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>")]
    public async Task Initiate_AnAmbiguousFailure_IsNeverTreatedAsARefusal(HttpStatusCode status, string body)
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => StubHandler.Json(status, body);

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "USD"));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<ProviderRejectedException>();
    }

    [Fact]
    public async Task Initiate_ATimeout_IsAmbiguous()
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => throw new TaskCanceledException("timed out");

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 10m, "USD"));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<ProviderRejectedException>();
    }

    [Theory]
    [InlineData(9000, "usd", null)] // wrong amount
    [InlineData(9100, "eur", null)] // wrong currency
    [InlineData(9100, "usd", "99999999-9999-4999-8999-999999999999")] // wrong reference
    public async Task Initiate_ASessionThatDoesNotMatchTheRequest_IsNeverShownToTheCustomer(long amount, string currency, string? reference)
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.OK, SessionJson(amount: amount, currency: currency, reference: reference));

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 91m, "USD"));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().NotBeOfType<ProviderRejectedException>();
    }

    [Fact]
    public async Task Initiate_ASessionWithoutAnHttpsUrl_IsRefused()
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.OK, SessionJson(url: "http://evil.example/pay"));

        var act = () => provider.InitiateAsync(new PaymentInitiation(Attempt, Order, 91m, "USD"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ───────────── status lookup ─────────────

    private static PaymentStatusQuery Query(string? reference = "cs_test_1") => new(Attempt, reference, Order, 91m, "USD");

    [Theory]
    [InlineData("complete", "paid", ProviderPaymentState.Succeeded)]
    [InlineData("complete", "unpaid", ProviderPaymentState.Pending)]
    [InlineData("open", "unpaid", ProviderPaymentState.Pending)]
    [InlineData("expired", "unpaid", ProviderPaymentState.Failed)]
    public async Task Lookup_MapsStripeSessionStateToTheProviderState(string status, string payment, ProviderPaymentState expected)
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.OK, SessionJson(status: status, payment: payment));

        (await provider.GetPaymentStatusAsync(Query())).Should().Be(expected);
        handler.Requests[0].Path.Should().Be("/v1/checkout/sessions/cs_test_1");
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task Lookup_AnUnknownSession_IsNotFound_AndNoReferenceNeedsNoCall()
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.NotFound, """{"error":{"code":"resource_missing"}}""");

        (await provider.GetPaymentStatusAsync(Query())).Should().Be(ProviderPaymentState.NotFound);
        (await provider.GetPaymentStatusAsync(Query(reference: null))).Should().Be(ProviderPaymentState.NotFound);
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(9000, "usd")]
    [InlineData(9100, "eur")]
    public async Task Lookup_APaidSessionWithTheWrongAmountOrCurrency_IsUnresolved_NotSuccess(long amount, string currency)
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.OK, SessionJson(status: "complete", payment: "paid", amount: amount, currency: currency));

        var act = () => provider.GetPaymentStatusAsync(Query());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Lookup_AProviderError_IsUnknown_NotAVerdict()
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.ServiceUnavailable, "{}");

        var act = () => provider.GetPaymentStatusAsync(Query());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ───────────── refunds ─────────────

    private static ProviderRefundRequest RefundRequest() =>
        new(Guid.Parse("33333333-3333-4333-8333-333333333333"), "refund-key-1", "cs_test_1", 91m, "USD");

    private static StubHandler.RecordedRequest? Find(StubHandler h, string path) => h.Requests.FirstOrDefault(r => r.Path.StartsWith(path, StringComparison.Ordinal));

    [Theory]
    [InlineData("succeeded", true, true)]
    [InlineData("pending", true, false)]
    [InlineData("requires_action", true, false)]
    public async Task Refund_IsCompleteOnlyWhenStripeSaysSucceeded(string status, bool accepted, bool completed)
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = r => r.Path.StartsWith("/v1/refunds")
            ? StubHandler.Json(HttpStatusCode.OK, $$"""{"id":"re_1","status":"{{status}}"}""")
            : StubHandler.Json(HttpStatusCode.OK, SessionJson(status: "complete", payment: "paid", paymentIntent: "pi_1"));

        var result = await provider.RefundAsync(RefundRequest());

        result.Accepted.Should().Be(accepted);
        result.Completed.Should().Be(completed);
        result.ProviderRefundId.Should().Be("re_1");
        var refund = Find(handler, "/v1/refunds")!;
        refund.Headers["Idempotency-Key"].Should().Be("refund-key-1");
        var form = refund.Form();
        form["payment_intent"].Should().Be("pi_1");
        form["amount"].Should().Be("9100");
        form["metadata[idempotency_key]"].Should().Be("refund-key-1");
        form["metadata[refund_id]"].Should().Be("33333333-3333-4333-8333-333333333333");
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    public async Task Refund_AFailedRefund_IsRejected_NotCompleted(string status)
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = r => r.Path.StartsWith("/v1/refunds")
            ? StubHandler.Json(HttpStatusCode.OK, $$"""{"id":"re_1","status":"{{status}}"}""")
            : StubHandler.Json(HttpStatusCode.OK, SessionJson(status: "complete", payment: "paid", paymentIntent: "pi_1"));

        var result = await provider.RefundAsync(RefundRequest());

        result.Accepted.Should().BeFalse();
        result.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task Refund_ForASessionWithoutAPaymentIntent_IsRejected_WithoutCallingRefunds()
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = _ => StubHandler.Json(HttpStatusCode.OK, SessionJson(status: "open"));

        var result = await provider.RefundAsync(RefundRequest());

        result.Accepted.Should().BeFalse();
        result.RejectionCode.Should().Be("no_payment_intent");
        Find(handler, "/v1/refunds").Should().BeNull();
    }

    [Fact]
    public async Task Refund_ADefinitiveStripeError_IsARejection()
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = r => r.Path.StartsWith("/v1/refunds")
            ? StubHandler.Json(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"amount_too_large"}}""")
            : StubHandler.Json(HttpStatusCode.OK, SessionJson(status: "complete", payment: "paid", paymentIntent: "pi_1"));

        var result = await provider.RefundAsync(RefundRequest());

        result.Accepted.Should().BeFalse();
        result.RejectionCode.Should().Be("amount_too_large");
        result.Retryable.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"type":"api_error"}}""")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"type":"rate_limit_error"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"charge_already_refunded"}}""")]
    public async Task Refund_AnAmbiguousAnswer_ThrowsSoTheWorkflowAsksBeforeActing(HttpStatusCode status, string body)
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = r => r.Path.StartsWith("/v1/refunds")
            ? StubHandler.Json(status, body)
            : StubHandler.Json(HttpStatusCode.OK, SessionJson(status: "complete", payment: "paid", paymentIntent: "pi_1"));

        var act = () => provider.RefundAsync(RefundRequest());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static RefundStatusQuery RefundQuery() =>
        new("refund-key-1", Guid.Parse("33333333-3333-4333-8333-333333333333"), "cs_test_1");

    private static string RefundList(string key, string refundId, string status, bool hasMore = false) =>
        $$$"""
        {"object":"list","has_more":{{{hasMore.ToString().ToLowerInvariant()}}},"data":[
          {"id":"re_other","status":"succeeded","metadata":{"idempotency_key":"another","refund_id":"44444444-4444-4444-8444-444444444444"}},
          {"id":"re_1","status":"{{{status}}}","metadata":{"idempotency_key":"{{{key}}}","refund_id":"{{{refundId}}}"}}]}
        """;

    [Theory]
    [InlineData("succeeded", ProviderRefundState.Succeeded)]
    [InlineData("pending", ProviderRefundState.Pending)]
    [InlineData("failed", ProviderRefundState.Failed)]
    public async Task RefundLookup_FindsTheRefundByItsOwnKey_NotByAnyRefundOfThePayment(string status, ProviderRefundState expected)
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = r => r.Path.StartsWith("/v1/refunds")
            ? StubHandler.Json(HttpStatusCode.OK, RefundList("refund-key-1", "33333333-3333-4333-8333-333333333333", status))
            : StubHandler.Json(HttpStatusCode.OK, SessionJson(status: "complete", payment: "paid", paymentIntent: "pi_1"));

        (await provider.GetRefundStatusAsync(RefundQuery())).Should().Be(expected);
        Find(handler, "/v1/refunds")!.Path.Should().Contain("payment_intent=pi_1");
    }

    [Fact]
    public async Task RefundLookup_ARefundUnderAnotherKey_IsNotThisRefund()
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = r => r.Path.StartsWith("/v1/refunds")
            ? StubHandler.Json(HttpStatusCode.OK, RefundList("refund-key-0-previous-submission", "33333333-3333-4333-8333-333333333333", "failed"))
            : StubHandler.Json(HttpStatusCode.OK, SessionJson(status: "complete", payment: "paid", paymentIntent: "pi_1"));

        (await provider.GetRefundStatusAsync(RefundQuery())).Should().Be(ProviderRefundState.NotFound);
    }

    [Fact]
    public async Task RefundLookup_AnIncompleteList_IsInconclusive_NotNotFound()
    {
        var (provider, handler, _) = TestProviders.Stripe();
        handler.Respond = r => r.Path.StartsWith("/v1/refunds")
            ? StubHandler.Json(HttpStatusCode.OK, RefundList("some-other-key", "33333333-3333-4333-8333-333333333333", "failed", hasMore: true))
            : StubHandler.Json(HttpStatusCode.OK, SessionJson(status: "complete", payment: "paid", paymentIntent: "pi_1"));

        var act = () => provider.GetRefundStatusAsync(RefundQuery());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ───────────── webhooks ─────────────

    private static string CompletedEvent(string eventId = "evt_1", string payment = "paid", long amount = 9100, string currency = "usd",
        string? reference = null, bool livemode = false, string type = "checkout.session.completed", string? order = null, string? account = null)
    {
        var evt = new Dictionary<string, object?>
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["type"] = type,
            ["livemode"] = livemode,
            ["data"] = new
            {
                @object = new Dictionary<string, object?>
                {
                    ["id"] = "cs_test_1",
                    ["object"] = "checkout.session",
                    ["payment_status"] = payment,
                    ["amount_total"] = amount,
                    ["currency"] = currency,
                    ["client_reference_id"] = reference ?? Attempt.ToString(),
                    ["metadata"] = new { attempt_id = Attempt.ToString(), order_id = order ?? Order.ToString() }
                }
            }
        };
        if (account is not null)
            evt["account"] = account;
        return System.Text.Json.JsonSerializer.Serialize(evt);
    }

    private static WebhookRequest Signed(string body, FixedClock clock, string secret = TestProviders.StripeWebhookSecret, long? timestamp = null)
    {
        var t = timestamp ?? clock.Now.ToUnixTimeSeconds();
        var header = $"t={t},v1={StripeSignature.Compute(body, t, secret)},v0=ignored-test-scheme";
        return new WebhookRequest(body, new Dictionary<string, string> { ["Stripe-Signature"] = header }, new Dictionary<string, string>());
    }

    [Fact]
    public void Webhook_AValidPaidEvent_IsVerifiedAndReducedToTheFactsWeCheck()
    {
        var (provider, _, clock) = TestProviders.Stripe();

        var result = provider.Parse(Signed(CompletedEvent(), clock));

        result.Status.Should().Be(WebhookVerification.Verified);
        var evt = result.Event!;
        evt.Kind.Should().Be(VerifiedEventKind.PaymentSucceeded);
        evt.EventId.Should().Be("evt_1");
        evt.AttemptId.Should().Be(Attempt);
        evt.OrderId.Should().Be(Order);
        evt.ProviderReference.Should().Be("cs_test_1");
        evt.AmountMinor.Should().Be(9100);
        evt.Currency.Should().Be("usd");
    }

    [Fact]
    public void Webhook_AsyncPaymentSucceeded_CountsAsPaid()
    {
        var (provider, _, clock) = TestProviders.Stripe();

        var result = provider.Parse(Signed(CompletedEvent(type: "checkout.session.async_payment_succeeded"), clock));

        result.Event!.Kind.Should().Be(VerifiedEventKind.PaymentSucceeded);
    }

    [Fact]
    public void Webhook_CompletedButUnpaid_IsIgnored_BecauseTheMoneyHasNotMoved()
    {
        var (provider, _, clock) = TestProviders.Stripe();

        provider.Parse(Signed(CompletedEvent(payment: "unpaid"), clock)).Event!.Kind.Should().Be(VerifiedEventKind.Ignored);
    }

    [Fact]
    public void Webhook_AnExpiredSession_IsAFailure_AndOtherEventsAreIgnored()
    {
        var (provider, _, clock) = TestProviders.Stripe();

        var expired = provider.Parse(Signed(CompletedEvent(type: "checkout.session.expired", payment: "unpaid"), clock));
        expired.Event!.Kind.Should().Be(VerifiedEventKind.PaymentFailed);
        expired.Event.FailureCode.Should().Be("checkout_session_expired");

        provider.Parse(Signed("""{"id":"evt_9","type":"payment_intent.created","livemode":false,"data":{"object":{}}}""", clock))
            .Event!.Kind.Should().Be(VerifiedEventKind.Ignored);
    }

    [Fact]
    public void Webhook_ABadSignature_IsRejected_BeforeAnythingIsRead()
    {
        var (provider, _, clock) = TestProviders.Stripe();
        var body = CompletedEvent();
        var tampered = Signed(body, clock) with { RawBody = body.Replace("9100", "1") };

        provider.Parse(tampered).Status.Should().Be(WebhookVerification.InvalidSignature);
        provider.Parse(Signed(body, clock, secret: "whsec_the_wrong_secret")).Status.Should().Be(WebhookVerification.InvalidSignature);
        provider.Parse(new WebhookRequest(body, new Dictionary<string, string>(), new Dictionary<string, string>())).Status.Should().Be(WebhookVerification.InvalidSignature);
        provider.Parse(new WebhookRequest(body, new Dictionary<string, string> { ["Stripe-Signature"] = "garbage" }, new Dictionary<string, string>())).Status
            .Should().Be(WebhookVerification.InvalidSignature);
    }

    [Fact]
    public void Webhook_OnlyTheV1Scheme_CountsAndAnyMatchingV1Passes()
    {
        var (provider, _, clock) = TestProviders.Stripe();
        var body = CompletedEvent();
        var t = clock.Now.ToUnixTimeSeconds();
        var good = StripeSignature.Compute(body, t, TestProviders.StripeWebhookSecret);
        var rolled = StripeSignature.Compute(body, t, "whsec_previous_secret_still_active");

        WebhookRequest With(string header) => new(body, new Dictionary<string, string> { ["stripe-signature"] = header }, new Dictionary<string, string>());

        provider.Parse(With($"t={t},v1={rolled},v1={good}")).Status.Should().Be(WebhookVerification.Verified, "a rolled secret yields several v1 signatures");
        provider.Parse(With($"t={t},v0={good}")).Status.Should().Be(WebhookVerification.InvalidSignature, "other schemes are ignored (downgrade protection)");
        provider.Parse(With($"v1={good}")).Status.Should().Be(WebhookVerification.InvalidSignature, "no timestamp");
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void Webhook_AReplayOutsideTheTolerance_IsRejected_EvenWithAValidSignature(int offsetSeconds)
    {
        var (provider, _, clock) = TestProviders.Stripe();

        var result = provider.Parse(Signed(CompletedEvent(), clock, timestamp: clock.Now.ToUnixTimeSeconds() + offsetSeconds));

        result.Status.Should().Be(WebhookVerification.InvalidSignature);
    }

    [Fact]
    public void Webhook_WithinTheTolerance_IsAccepted()
    {
        var (provider, _, clock) = TestProviders.Stripe();

        provider.Parse(Signed(CompletedEvent(), clock, timestamp: clock.Now.ToUnixTimeSeconds() - 299)).Status.Should().Be(WebhookVerification.Verified);
    }

    [Fact]
    public void Webhook_ALiveEventOnATestIntegration_IsAWrongContext()
    {
        var (provider, _, clock) = TestProviders.Stripe();

        provider.Parse(Signed(CompletedEvent(livemode: true), clock)).Status.Should().Be(WebhookVerification.WrongContext);
    }

    [Fact]
    public void Webhook_AConnectedAccountEvent_IsAWrongContext()
    {
        var (provider, _, clock) = TestProviders.Stripe();

        provider.Parse(Signed(CompletedEvent(account: "acct_other"), clock)).Status.Should().Be(WebhookVerification.WrongContext);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"id":"evt_1"}""")]
    public void Webhook_ASignedButUnreadableBody_IsMalformed(string body)
    {
        var (provider, _, clock) = TestProviders.Stripe();

        provider.Parse(Signed(body, clock)).Status.Should().Be(WebhookVerification.Malformed);
    }

    [Fact]
    public void Webhook_ASessionWithoutOurAttemptReference_IsMalformed()
    {
        var (provider, _, clock) = TestProviders.Stripe();

        provider.Parse(Signed(CompletedEvent(reference: "not-a-guid"), clock)).Status.Should().Be(WebhookVerification.Malformed);
    }

    [Fact]
    public void Signature_IsCheckedOverTheExactBytes_IncludingWhitespace()
    {
        var clock = new FixedClock(TestProviders.Now);
        var body = "{ \"id\": \"evt_1\" }";
        var t = clock.Now.ToUnixTimeSeconds();
        var header = $"t={t},v1={StripeSignature.Compute(body, t, TestProviders.StripeWebhookSecret)}";

        StripeSignature.IsValid(body, header, TestProviders.StripeWebhookSecret, clock.Now, TimeSpan.FromMinutes(5)).Should().BeTrue();
        StripeSignature.IsValid(body.Replace(" ", string.Empty), header, TestProviders.StripeWebhookSecret, clock.Now, TimeSpan.FromMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void Signature_MatchesTheDocumentedConstruction_HmacSha256OfTimestampDotBody()
    {
        // Independent computation of https://docs.stripe.com/webhooks#verify-manually: HMAC-SHA256, key = secret,
        // message = "{t}.{payload}", lowercase hex.
        const string secret = "whsec_test_secret";
        const string payload = "{\"id\":\"evt_test\"}";
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("1492774577." + payload))).ToLowerInvariant();

        StripeSignature.Compute(payload, 1492774577, secret).Should().Be(expected);
    }

    [Fact]
    public void Provider_IsRealNotSimulated_AndSupportsLookupAndRefunds()
    {
        var (provider, _, _) = TestProviders.Stripe();

        provider.IsSimulated.Should().BeFalse();
        provider.Name.Should().Be("Stripe");
        provider.SupportsPaymentLookup.Should().BeTrue();
        provider.SupportsRefunds.Should().BeTrue();
    }
}

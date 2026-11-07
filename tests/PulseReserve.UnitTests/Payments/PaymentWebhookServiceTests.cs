using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers.Paymob;
using PulseReserve.Modules.Payments.Providers.Stripe;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Verified provider events applied to the ONE payment lifecycle, with the real Stripe and Paymob adapters (fake HTTP) and an
/// in-memory model of the store. Nothing here reaches a provider: these prove PulseReserve's handling, NOT a live or sandbox
/// integration.
/// </summary>
public sealed class PaymentWebhookServiceTests
{
    private const string Currency = "USD";
    private const decimal Total = 91m;

    private sealed class Harness
    {
        public InMemoryOrderStore Store { get; } = new(Currency, Total);
        public ConfigurableGateway Gateway { get; } = new();
        public StripePaymentProvider Stripe { get; }
        public PaymobPaymentProvider Paymob { get; }
        public StubHandler StripeHttp { get; } = new();
        public StubHandler PaymobHttp { get; } = new();
        public FixedClock Clock { get; } = new(TestProviders.Now);
        public PaymentService Payments { get; }
        public PaymentWebhookService Webhooks { get; }
        public InMemoryAnomalyRecorder Anomalies { get; } = new();

        public Harness(Microsoft.Extensions.Logging.ILogger<PaymentWebhookService>? logger = null)
        {
            var stripeHttp = new HttpClient(StripeHttp) { BaseAddress = new Uri(StripeOptions.DefaultApiBaseUrl) };
            var paymobHttp = new HttpClient(PaymobHttp) { BaseAddress = new Uri(PaymobOptions.DefaultApiBaseUrl) };
            Stripe = new StripePaymentProvider(TestProviders.StripeOptionsFor(Currency), stripeHttp, Clock);
            Paymob = new PaymobPaymentProvider(TestProviders.PaymobOptionsFor(Currency), paymobHttp);
            var registry = new PaymentProviderRegistry([Stripe, Paymob]);
            Payments = new PaymentService(Store, registry, Gateway);
            Webhooks = new PaymentWebhookService(registry, new InMemoryAttemptLookup(Store), Store, Payments, Anomalies, logger ?? NullLogger<PaymentWebhookService>.Instance);
        }

        public async Task<InMemoryOrderStore.AttemptRow> StartStripeAsync(string key = "k1", string session = "cs_test_1")
        {
            StripeHttp.Respond = r => StubHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                id = session, status = "open", payment_status = "unpaid", url = "https://checkout.stripe.com/c/pay/" + session,
                amount_total = 9100, currency = "usd", client_reference_id = r.Form()["client_reference_id"]
            }));
            var result = await Payments.InitiateAsync(Store.CustomerId, Store.Order.OrderId, key, "Stripe");
            result.IsSuccess.Should().BeTrue();
            return Store.Attempts.Single(a => a.Id == result.Value.Attempt.AttemptId);
        }

        public async Task<InMemoryOrderStore.AttemptRow> StartPaymobAsync(string key = "k1", long paymobOrder = 217503754)
        {
            PaymobHttp.Respond = _ => StubHandler.Json(HttpStatusCode.Created,
                JsonSerializer.Serialize(new { id = "pi_1", client_secret = "egy_csk_test_x", intention_order_id = paymobOrder }));
            var result = await Payments.InitiateAsync(Store.CustomerId, Store.Order.OrderId, key, "Paymob");
            result.IsSuccess.Should().BeTrue();
            return Store.Attempts.Single(a => a.Id == result.Value.Attempt.AttemptId);
        }

        public WebhookRequest StripeEvent(InMemoryOrderStore.AttemptRow attempt, string type = "checkout.session.completed", string payment = "paid",
            long amount = 9100, string currency = "usd", string? sessionId = null, bool live = false, Guid? order = null, string? eventId = null)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = eventId ?? "evt_" + Guid.NewGuid().ToString("N")[..8],
                ["type"] = type,
                ["livemode"] = live,
                ["data"] = new
                {
                    @object = new Dictionary<string, object?>
                    {
                        ["id"] = sessionId ?? attempt.Reference,
                        ["payment_status"] = payment,
                        ["amount_total"] = amount,
                        ["currency"] = currency,
                        ["client_reference_id"] = attempt.Id.ToString(),
                        ["metadata"] = new { attempt_id = attempt.Id.ToString(), order_id = (order ?? Store.Order.OrderId).ToString() }
                    }
                }
            });
            var t = Clock.Now.ToUnixTimeSeconds();
            var header = $"t={t},v1={StripeSignature.Compute(body, t, TestProviders.StripeWebhookSecret)}";
            return new WebhookRequest(body, new Dictionary<string, string> { ["Stripe-Signature"] = header }, new Dictionary<string, string>());
        }

        public WebhookRequest PaymobCallback(Guid attemptId, string paymobOrderId = "217503754", long cents = 9100, string currency = "USD",
            long integration = 4097558, string? secret = null, Action<JsonObject>? change = null)
        {
            var obj = new JsonObject
            {
                ["id"] = 192036465, ["pending"] = false, ["amount_cents"] = cents, ["success"] = true, ["is_auth"] = false, ["is_capture"] = false,
                ["is_standalone_payment"] = true, ["is_voided"] = false, ["is_refunded"] = false, ["is_3d_secure"] = true,
                ["integration_id"] = integration, ["has_parent_transaction"] = false, ["created_at"] = "2030-01-01T11:33:44.592345",
                ["currency"] = currency, ["error_occured"] = false, ["owner"] = 302852,
                ["order"] = new JsonObject { ["id"] = long.Parse(paymobOrderId), ["merchant_order_id"] = attemptId.ToString() },
                ["source_data"] = new JsonObject { ["type"] = "card", ["pan"] = "2346", ["sub_type"] = "MasterCard" }
            };
            change?.Invoke(obj);
            var body = new JsonObject { ["type"] = "TRANSACTION", ["obj"] = obj }.ToJsonString();
            var hmac = PaymobHmac.Compute(JsonDocument.Parse(obj.ToJsonString()).RootElement, secret ?? TestProviders.PaymobHmacSecret);
            return new WebhookRequest(body, new Dictionary<string, string>(), new Dictionary<string, string> { ["hmac"] = hmac });
        }
    }

    private static void Untouched(Harness h, InMemoryOrderStore.AttemptRow attempt)
    {
        attempt.Status.Should().Be(PaymentAttemptStatus.Initiated);
        h.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
        h.Store.FinalizeCalls.Should().Be(0);
        h.Gateway.ConfirmCalls.Should().Be(0);
    }

    // ───────────── success ─────────────

    [Fact]
    public async Task AVerifiedStripeSuccess_MovesTheOrderToPaid_ThroughTheExistingLifecycle()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt));

        result.Outcome.Should().Be(WebhookOutcome.Applied);
        attempt.Status.Should().Be(PaymentAttemptStatus.Succeeded);
        h.Store.Order.Status.Should().Be(OrderStatus.Paid);
        h.Gateway.ConfirmCalls.Should().Be(1, "the sale is confirmed by the Reservation module, not decided by the webhook");
    }

    [Fact]
    public async Task AVerifiedPaymobSuccess_MovesTheOrderToPaid_UsingTheStoredPaymobOrderReference()
    {
        var h = new Harness();
        var attempt = await h.StartPaymobAsync();
        attempt.Reference.Should().Be("217503754");

        var result = await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id));

        result.Outcome.Should().Be(WebhookOutcome.Applied);
        h.Store.Order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task DuplicateDeliveries_AreIdempotent_NoSecondTransitionAndNoSecondPaid()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();
        var delivery = h.StripeEvent(attempt, eventId: "evt_same");

        for (var i = 0; i < 4; i++)
            (await h.Webhooks.HandleAsync("Stripe", delivery)).Outcome.Should().Be(WebhookOutcome.Applied);

        h.Store.PaidTransitions.Should().Be(1, "tickets and notifications hang off this single transition");
        attempt.Status.Should().Be(PaymentAttemptStatus.Succeeded);
        h.Store.Order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task ASuccessAfterTheReservationExpired_FollowsTheRefundRequiredPath_NotPaid()
    {
        var h = new Harness { Gateway = { Sale = SaleResult.Expired } };
        var attempt = await h.StartStripeAsync();

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt));

        result.Outcome.Should().Be(WebhookOutcome.Applied);
        h.Store.Order.Status.Should().Be(OrderStatus.RefundRequired);
        h.Store.PaidTransitions.Should().Be(0, "no tickets for seats that are no longer owned");
        h.Store.RefundRequiredTransitions.Should().Be(1);
    }

    [Fact]
    public async Task ALateSuccessForACancelledAttempt_StillRecordsThatTheMoneyMoved()
    {
        var h = new Harness { Gateway = { Sale = SaleResult.Expired } };
        var attempt = await h.StartStripeAsync();
        attempt.Status = PaymentAttemptStatus.Cancelled; // abandoned locally, then Stripe reports the payment

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt));

        result.Outcome.Should().Be(WebhookOutcome.Applied);
        attempt.Status.Should().Be(PaymentAttemptStatus.Succeeded);
        h.Store.Order.Status.Should().Be(OrderStatus.RefundRequired);
    }

    // ───────────── authenticity ─────────────

    [Fact]
    public async Task ABadSignature_IsRejected_AndChangesNothing()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();
        var forged = h.StripeEvent(attempt) with { Headers = new Dictionary<string, string> { ["Stripe-Signature"] = "t=1,v1=deadbeef" } };

        (await h.Webhooks.HandleAsync("Stripe", forged)).Should().Be(new WebhookResult(WebhookOutcome.Rejected, "invalid_signature"));
        Untouched(h, attempt);
    }

    [Fact]
    public async Task ABadPaymobHmac_IsRejected_AndChangesNothing()
    {
        var h = new Harness();
        var attempt = await h.StartPaymobAsync();

        (await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id, secret: "wrong"))).Reason.Should().Be("invalid_signature");
        Untouched(h, attempt);
    }

    [Theory]
    [InlineData("OtherProvider")]
    [InlineData("Simulator")]
    [InlineData("")]
    public async Task AProviderThatIsNotEnabledOrHasNoWebhook_IsUnavailable(string provider)
    {
        var h = new Harness();

        (await h.Webhooks.HandleAsync(provider, new WebhookRequest("{}", new Dictionary<string, string>(), new Dictionary<string, string>())))
            .Outcome.Should().Be(WebhookOutcome.ProviderUnavailable);
    }

    // ───────────── cross-checks against our own records ─────────────

    [Theory]
    [InlineData(100, "usd", "amount_or_currency_mismatch")]
    [InlineData(9100, "eur", "amount_or_currency_mismatch")]
    [InlineData(9101, "usd", "amount_or_currency_mismatch")]
    public async Task AVerifiedStripeSuccess_WithTheWrongAmountOrCurrency_IsNeverApplied(long amount, string currency, string reason)
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: amount, currency: currency));

        result.Should().Be(new WebhookResult(WebhookOutcome.AnomalyRecorded, reason), "the evidence is stored, so the event is acknowledged rather than retried");
        Untouched(h, attempt);
    }

    [Theory]
    [InlineData(9000, "USD")]
    [InlineData(9100, "EUR")]
    public async Task AVerifiedPaymobSuccess_WithTheWrongAmountOrCurrency_IsNeverApplied(long cents, string currency)
    {
        var h = new Harness();
        var attempt = await h.StartPaymobAsync();

        var result = await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id, cents: cents, currency: currency));

        result.Reason.Should().Be("amount_or_currency_mismatch");
        Untouched(h, attempt);
    }

    [Fact]
    public async Task AnEventWithADifferentProviderReference_IsNotApplied_AndChangesNothing()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, sessionId: "cs_test_someone_elses"));

        result.Reason.Should().Be("reference_mismatch");
        Untouched(h, attempt);

        var h2 = new Harness();
        var paymob = await h2.StartPaymobAsync("k-paymob");
        (await h2.Webhooks.HandleAsync("Paymob", h2.PaymobCallback(paymob.Id, paymobOrderId: "999"))).Reason.Should().Be("reference_mismatch");
        Untouched(h2, paymob);
    }

    [Fact]
    public async Task AnEventThatNamesAnotherOrder_IsNotApplied()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, order: Guid.NewGuid()));

        result.Reason.Should().Be("order_mismatch");
        Untouched(h, attempt);
    }

    [Fact]
    public async Task AnEventForAnUnknownAttempt_IsNotApplied()
    {
        var h = new Harness();
        var phantom = new InMemoryOrderStore.AttemptRow { Id = Guid.NewGuid(), Reference = "cs_x", Provider = "Stripe" };

        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(phantom))).Reason.Should().Be("unknown_attempt");
    }

    [Fact]
    public async Task CrossProviderIsolation_AStripeEventCannotCompleteAPaymobAttempt_AndViceVersa()
    {
        var h = new Harness();
        var paymobAttempt = await h.StartPaymobAsync();

        // A perfectly valid, signed Stripe event that names the Paymob attempt.
        var stripeEvent = h.StripeEvent(paymobAttempt, sessionId: paymobAttempt.Reference);
        (await h.Webhooks.HandleAsync("Stripe", stripeEvent)).Reason.Should().Be("wrong_provider_for_attempt");
        Untouched(h, paymobAttempt);

        // And a valid Paymob callback that names a Stripe attempt.
        var h2 = new Harness();
        var stripeAttempt = await h2.StartStripeAsync();
        (await h2.Webhooks.HandleAsync("Paymob", h2.PaymobCallback(stripeAttempt.Id, paymobOrderId: "217503754"))).Reason.Should().Be("wrong_provider_for_attempt");
        Untouched(h2, stripeAttempt);
    }

    [Fact]
    public async Task AWrongMerchantContext_IsRejected()
    {
        var h = new Harness();
        var stripeAttempt = await h.StartStripeAsync();

        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(stripeAttempt, live: true))).Reason.Should().Be("wrong_context");

        var h2 = new Harness();
        var paymobAttempt = await h2.StartPaymobAsync();
        (await h2.Webhooks.HandleAsync("Paymob", h2.PaymobCallback(paymobAttempt.Id, integration: 123))).Reason.Should().Be("wrong_context");
        Untouched(h2, paymobAttempt);
    }

    // ───────────── ordering and non-conclusive events ─────────────

    [Fact]
    public async Task ACompletedButUnpaidSession_ChangesNothing_BecauseTheMoneyHasNotMoved()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, payment: "unpaid"))).Should().Be(new WebhookResult(WebhookOutcome.Acknowledged, "ignored_event"));
        Untouched(h, attempt);

        // …and the delayed payment method's later success then applies.
        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, type: "checkout.session.async_payment_succeeded"))).Outcome.Should().Be(WebhookOutcome.Applied);
        h.Store.Order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task AnExpiredSession_FailsTheAttemptOnly_AndTheCustomerMayTryAgain()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, type: "checkout.session.expired", payment: "unpaid"))).Outcome.Should().Be(WebhookOutcome.Applied);

        attempt.Status.Should().Be(PaymentAttemptStatus.Failed);
        h.Store.Order.Status.Should().Be(OrderStatus.PendingPayment, "a failed attempt never changes the order");
        (await h.Payments.InitiateAsync(h.Store.CustomerId, h.Store.Order.OrderId, "k2", "Stripe")).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task OutOfOrderDelivery_NeverOverwritesARecordedVerdict()
    {
        var failedFirst = new Harness();
        var a1 = await failedFirst.StartStripeAsync();
        await failedFirst.Webhooks.HandleAsync("Stripe", failedFirst.StripeEvent(a1, type: "checkout.session.expired", payment: "unpaid"));

        // A verified success after a recorded failure is no longer only logged: it becomes a durable exception (see the
        // late-success tests below). The Failed verdict and the order are untouched.
        var late = await failedFirst.Webhooks.HandleAsync("Stripe", failedFirst.StripeEvent(a1));
        late.Should().Be(new WebhookResult(WebhookOutcome.Acknowledged, "late_success_recorded_for_review"));
        a1.Status.Should().Be(PaymentAttemptStatus.Failed, "the conflict is surfaced, nothing is silently rewritten");
        failedFirst.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
        failedFirst.Store.Exceptions.Should().ContainSingle();

        var paidFirst = new Harness();
        var a2 = await paidFirst.StartStripeAsync();
        await paidFirst.Webhooks.HandleAsync("Stripe", paidFirst.StripeEvent(a2));
        await paidFirst.Webhooks.HandleAsync("Stripe", paidFirst.StripeEvent(a2, type: "checkout.session.expired", payment: "unpaid"));
        a2.Status.Should().Be(PaymentAttemptStatus.Succeeded);
        paidFirst.Store.Order.Status.Should().Be(OrderStatus.Paid);
    }

    // ───────────── late verified success after a recorded failure ─────────────

    private static async Task<(Harness H, InMemoryOrderStore.AttemptRow Attempt)> FailedStripeAttemptAsync()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();
        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, type: "checkout.session.expired", payment: "unpaid"))).Outcome.Should().Be(WebhookOutcome.Applied);
        attempt.Status.Should().Be(PaymentAttemptStatus.Failed);
        return (h, attempt);
    }

    [Fact]
    public async Task ALateVerifiedStripeSuccess_AfterAnExpiredVerdict_IsRecordedForReview_AndNothingElseChanges()
    {
        var (h, attempt) = await FailedStripeAttemptAsync();

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, eventId: "evt_late_1"));

        result.Should().Be(new WebhookResult(WebhookOutcome.Acknowledged, "late_success_recorded_for_review"));
        h.Store.Exceptions.Should().ContainSingle().Which.Should().Be((attempt.Id, "evt_late_1"));
        attempt.Status.Should().Be(PaymentAttemptStatus.Failed, "the original verdict is preserved, never rewritten");
        h.Store.Order.Status.Should().Be(OrderStatus.PendingPayment, "the order is not paid by an exception");
        h.Store.FinalizeCalls.Should().Be(0, "no finalisation, so no outbox events, tickets or refund records");
        h.Gateway.ConfirmCalls.Should().Be(0, "the reservation is never asked to sell on the strength of a contradicted attempt");
        h.Store.PaidTransitions.Should().Be(0);
        h.Store.RefundRequiredTransitions.Should().Be(0, "no refund is claimed or created");
    }

    [Fact]
    public async Task ARepeatedOrRedeliveredLateSuccess_CreatesExactlyOneException()
    {
        var (h, attempt) = await FailedStripeAttemptAsync();

        for (var i = 0; i < 3; i++)
            (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, eventId: "evt_late_same"))).Reason.Should().Be("late_success_recorded_for_review");
        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, type: "checkout.session.async_payment_succeeded", eventId: "evt_late_other"))).Reason.Should().Be("late_success_recorded_for_review");

        h.Store.Exceptions.Should().ContainSingle("one exception per attempt however often the provider repeats itself");
        h.Store.Exceptions[0].EventId.Should().Be("evt_late_same", "the first verified event is the one on record");
        h.Store.FinalizeCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("order")]
    public async Task ALateSuccessThatIsNotVerifiedOrDoesNotMatch_CreatesNoException(string defect)
    {
        var (h, attempt) = await FailedStripeAttemptAsync();
        var good = h.StripeEvent(attempt);
        var request = defect switch
        {
            "signature" => good with { Headers = new Dictionary<string, string> { ["Stripe-Signature"] = "t=1,v1=00" } },
            "amount" => h.StripeEvent(attempt, amount: 1),
            "currency" => h.StripeEvent(attempt, currency: "eur"),
            _ => h.StripeEvent(attempt, order: Guid.NewGuid())
        };

        var result = await h.Webhooks.HandleAsync("Stripe", request);

        result.Outcome.Should().Be(defect == "signature" ? WebhookOutcome.Rejected : WebhookOutcome.AnomalyRecorded,
            "an unauthenticated event is refused; an authentic one that does not match is kept as evidence, never as an exception");
        h.Store.Exceptions.Should().BeEmpty("only a fully verified, matching success may create an exception");
        attempt.Status.Should().Be(PaymentAttemptStatus.Failed);
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public async Task ALateSuccess_ForAClosedOrder_IsRecorded_ButTheOrderIsNeverFinalisedOrSold(string closed)
    {
        var (h, attempt) = await FailedStripeAttemptAsync();
        h.Store.Order = h.Store.Order with { Status = closed };
        h.Gateway.Sale = SaleResult.Confirmed; // even if the seats happened to be sellable, nothing may be sold

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt));

        result.Reason.Should().Be("late_success_recorded_for_review");
        h.Store.Exceptions.Should().ContainSingle();
        h.Store.Order.Status.Should().Be(closed);
        h.Gateway.ConfirmCalls.Should().Be(0);
        h.Store.FinalizeCalls.Should().Be(0);
    }

    [Fact]
    public async Task WhileAnExceptionIsOpen_TheOrderCannotBePaidAgain_NoProviderIsCalled_AndNothingSwitchesProvider()
    {
        var (h, attempt) = await FailedStripeAttemptAsync();
        await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt));
        var stripeCalls = h.StripeHttp.Requests.Count;

        var again = await h.Payments.InitiateAsync(h.Store.CustomerId, h.Store.Order.OrderId, "k-new", "Stripe");
        var other = await h.Payments.InitiateAsync(h.Store.CustomerId, h.Store.Order.OrderId, "k-other", "Paymob");

        again.Error.Should().Be(PaymentsErrors.OrderHeldForReview);
        other.Error.Should().Be(PaymentsErrors.OrderHeldForReview, "a generic fallback to another provider would charge the customer twice");
        h.StripeHttp.Requests.Count.Should().Be(stripeCalls);
        h.PaymobHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAttemptAlreadyInFlight_IsWithdrawn_WhenTheExceptionIsCreated_SoTheCustomerIsNotChargedTwice()
    {
        var (h, failed) = await FailedStripeAttemptAsync();
        var second = await h.StartStripeAsync("k2", "cs_test_2"); // the customer started again after the failure
        second.Status.Should().Be(PaymentAttemptStatus.Initiated);

        await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(failed));

        h.Store.Exceptions.Should().ContainSingle();
        second.Status.Should().Be(PaymentAttemptStatus.Cancelled);
        second.CheckoutUrl.Should().BeNull("the hosted payment link is withdrawn");
        failed.Status.Should().Be(PaymentAttemptStatus.Failed);
        h.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);

        // A verified success for the withdrawn attempt is still honoured by the existing late-success rules.
        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(second))).Outcome.Should().Be(WebhookOutcome.Applied);
        h.Store.Order.Status.Should().Be(OrderStatus.Paid);
        h.Store.Exceptions.Should().ContainSingle("the earlier charge stays an open exception for an administrator");
    }

    [Fact]
    public async Task APaymobCallback_CanNeverCreateAnException_BecauseItNeverRecordsAFailedAttempt()
    {
        var h = new Harness();
        var attempt = await h.StartPaymobAsync();

        await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id, change: o => o["success"] = false));
        await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id, change: o => o["error_occured"] = true));
        var success = await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id));

        success.Outcome.Should().Be(WebhookOutcome.Applied);
        h.Store.Exceptions.Should().BeEmpty();
        attempt.Status.Should().Be(PaymentAttemptStatus.Succeeded);
    }

    [Fact]
    public async Task TheNormalSuccessPath_AndAFailureAfterSuccess_AreUnchanged()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt))).Outcome.Should().Be(WebhookOutcome.Applied);
        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, type: "checkout.session.expired", payment: "unpaid")))
            .Should().Be(new WebhookResult(WebhookOutcome.Acknowledged, "conflicts_with_recorded_outcome"));

        h.Store.Order.Status.Should().Be(OrderStatus.Paid);
        h.Store.Exceptions.Should().BeEmpty("a failure after a success keeps the success; only a success after a failure needs a person");
    }

    // ───────────── what the transition history will say about a webhook ─────────────

    [Fact]
    public async Task AVerifiedStripeSuccess_ReachesTheStoreLabelledAsAProviderFact_AndTheSaleAsAnApplicationDecision()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, eventId: "evt_hist_1"));

        var verdict = h.Store.Seen.Single(x => x.Method == "RecordAttemptOutcome").Info!;
        (verdict.Source, verdict.FactBasis, verdict.Provider, verdict.ProviderEventId, verdict.Reason)
            .Should().Be((TransitionSource.ProviderWebhook, FactBasis.ProviderVerified, "Stripe", "evt_hist_1", TransitionReason.ProviderVerdict));
        var sale = h.Store.Seen.Single(x => x.Method == "FinalizeAfterSale").Info!;
        (sale.Source, sale.FactBasis, sale.ProviderEventId, sale.Reason)
            .Should().Be((TransitionSource.ProviderWebhook, FactBasis.ApplicationDecision, "evt_hist_1", TransitionReason.SaleConfirmation));
        sale.CorrelationId.Should().Be(verdict.CorrelationId);
        FinancialTransitionContext.Current.Should().BeNull("the scope ends with the request");
    }

    [Fact]
    public async Task AVerifiedPaymobSuccess_CarriesItsOwnEventIdentity()
    {
        var h = new Harness();
        var attempt = await h.StartPaymobAsync();

        await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id));

        var verdict = h.Store.Seen.Single(x => x.Method == "RecordAttemptOutcome").Info!;
        verdict.Provider.Should().Be("Paymob");
        verdict.ProviderEventId.Should().StartWith("paymob:");
        verdict.FactBasis.Should().Be(FactBasis.ProviderVerified);
    }

    [Fact]
    public async Task ARejectedEvent_ReachesNoStateChangingMethod_SoItLeavesNoTransition()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();
        h.Store.Seen.Clear();

        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 1))).Outcome.Should().NotBe(WebhookOutcome.Applied);
        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt) with { Headers = new Dictionary<string, string> { ["Stripe-Signature"] = "t=1,v1=00" } })).Outcome.Should().Be(WebhookOutcome.Rejected);

        h.Store.Seen.Should().BeEmpty("a mismatch or a forged event changes no financial state");
    }

    // ───────────── verified-signature events that disagree with our records ─────────────

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<PaymentWebhookService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                text += " | " + string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"));
            Lines.Add(text);
        }
    }

    private static void NothingWasApplied(Harness h, InMemoryOrderStore.AttemptRow attempt, string expectedAttemptStatus = PaymentAttemptStatus.Initiated)
    {
        attempt.Status.Should().Be(expectedAttemptStatus, "the recorded verdict is never touched by a mismatching event");
        h.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
        h.Store.FinalizeCalls.Should().Be(0, "no order payment, no outbox event, no ticket, no refund");
        h.Gateway.ConfirmCalls.Should().Be(0, "the reservation is never asked to sell");
        h.Store.Exceptions.Should().BeEmpty("a mismatch is not a trusted late success");
        h.Store.PaidTransitions.Should().Be(0);
        h.Store.RefundRequiredTransitions.Should().Be(0);
    }

    [Fact]
    public async Task AValidSignatureWithTheRightAmountAndCurrency_IsApplied_AndRecordsNoAnomaly()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt))).Outcome.Should().Be(WebhookOutcome.Applied);

        h.Anomalies.Records.Should().BeEmpty();
    }

    [Theory]
    [InlineData(100, "usd", "USD")]
    [InlineData(9101, "usd", "USD")]
    [InlineData(9100, "eur", "EUR")]
    public async Task AValidSignatureWithTheWrongAmountOrCurrency_IsRecordedAsEvidence_AndAppliesNothing(long amount, string currency, string receivedCurrency)
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: amount, currency: currency, eventId: "evt_mismatch_1"));

        result.Should().Be(new WebhookResult(WebhookOutcome.AnomalyRecorded, "amount_or_currency_mismatch"));
        var record = h.Anomalies.Records.Should().ContainSingle().Subject;
        (record.Kind, record.ReasonCode, record.Provider, record.ProviderEventId).Should().Be((PaymentAnomalyKind.AmountOrCurrencyMismatch, "amount_or_currency_mismatch", "Stripe", "evt_mismatch_1"));
        (record.AttemptId, record.OrderId).Should().Be((attempt.Id, h.Store.Order.OrderId), "the event matched a known attempt of this provider and order");
        (record.ReceivedAmountMinor, record.ReceivedCurrency).Should().Be((amount, receivedCurrency), "what the event said, kept as untrusted received data");
        (record.ExpectedAmountMinor, record.ExpectedCurrency).Should().Be((9100L, "USD"), "what our own order says");
        NothingWasApplied(h, attempt);
    }

    [Fact]
    public async Task AnInvalidSignature_CreatesNoAnomaly_NoMatterWhatTheBodySays()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();
        var forged = h.StripeEvent(attempt, amount: 1) with { Headers = new Dictionary<string, string> { ["Stripe-Signature"] = "t=1,v1=00" } };

        (await h.Webhooks.HandleAsync("Stripe", forged)).Outcome.Should().Be(WebhookOutcome.Rejected);

        h.Anomalies.Records.Should().BeEmpty("unauthenticated input must never create a record");
        NothingWasApplied(h, attempt);
    }

    [Fact]
    public async Task AnEventForAnAttemptWeDoNotKnow_IsKeptWithoutAnyAssociation()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();
        var phantom = new InMemoryOrderStore.AttemptRow { Id = Guid.NewGuid(), OrderId = h.Store.Order.OrderId, Reference = "cs_other" };

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(phantom, eventId: "evt_phantom"));

        result.Reason.Should().Be("unknown_attempt");
        var record = h.Anomalies.Records.Should().ContainSingle().Subject;
        record.Kind.Should().Be(PaymentAnomalyKind.UnknownPaymentReference);
        (record.AttemptId, record.OrderId).Should().Be((null, null), "an event that matched nothing is never linked to a local order or attempt");
        (record.ClaimedAttemptId, record.ClaimedOrderId).Should().Be((phantom.Id, h.Store.Order.OrderId), "the claimed identifiers are kept as claimed, not as fact");
        (record.ExpectedAmountMinor, record.ExpectedCurrency).Should().Be((null, null));
        NothingWasApplied(h, attempt);
    }

    [Fact]
    public async Task AnEventNamingAnotherProvidersAttempt_OrAnotherOrder_IsAnInconsistentAssociation_NotALink()
    {
        var h = new Harness();
        var stripeAttempt = await h.StartStripeAsync();

        (await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(stripeAttempt.Id, paymobOrderId: "217503754"))).Reason.Should().Be("wrong_provider_for_attempt");
        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(stripeAttempt, order: Guid.NewGuid(), eventId: "evt_other_order"))).Reason.Should().Be("order_mismatch");

        h.Anomalies.Records.Select(r => (r.Kind, r.ReasonCode, r.AttemptId, r.OrderId))
            .Should().BeEquivalentTo(
            [
                (PaymentAnomalyKind.InconsistentAssociation, "wrong_provider_for_attempt", (Guid?)null, (Guid?)null),
                (PaymentAnomalyKind.InconsistentAssociation, "order_mismatch", (Guid?)null, (Guid?)null)
            ]);
        NothingWasApplied(h, stripeAttempt);
    }

    [Fact]
    public async Task AReferenceThatDoesNotMatchTheStoredOne_IsKeptAsEvidenceOfThatAttempt_AndAppliesNothing()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, sessionId: "cs_someone_elses", eventId: "evt_ref"));

        result.Reason.Should().Be("reference_mismatch");
        var record = h.Anomalies.Records.Should().ContainSingle().Subject;
        (record.Kind, record.AttemptId, record.OrderId).Should().Be((PaymentAnomalyKind.ReferenceMismatch, (Guid?)attempt.Id, (Guid?)h.Store.Order.OrderId));
        NothingWasApplied(h, attempt);
    }

    [Fact]
    public async Task ARedeliveredMismatchEvent_IsOneAnomaly_ADifferentEventIsAnother()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();

        for (var i = 0; i < 3; i++)
            await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 1, eventId: "evt_same"));
        await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 1, eventId: "evt_other"));

        h.Anomalies.Records.Select(r => r.ProviderEventId).Should().BeEquivalentTo("evt_same", "evt_other");
        NothingWasApplied(h, attempt);
    }

    [Fact]
    public async Task AMismatch_AfterAFailedVerdict_NeitherRewritesItNorBecomesATrustedLateSuccess()
    {
        var (h, attempt) = await FailedStripeAttemptAsync();

        await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 1, eventId: "evt_late_wrong"));

        h.Anomalies.Records.Should().ContainSingle().Which.Kind.Should().Be(PaymentAnomalyKind.AmountOrCurrencyMismatch);
        h.Store.Exceptions.Should().BeEmpty("only a fully matching verified success may create a payment exception");
        NothingWasApplied(h, attempt, PaymentAttemptStatus.Failed);
    }

    [Fact]
    public async Task AMismatch_AfterASuccessfulPayment_LeavesThePaidOrderAlone()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();
        await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt));

        await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 12345, eventId: "evt_after_paid"));

        h.Anomalies.Records.Should().ContainSingle();
        attempt.Status.Should().Be(PaymentAttemptStatus.Succeeded);
        h.Store.Order.Status.Should().Be(OrderStatus.Paid);
        h.Store.PaidTransitions.Should().Be(1, "the earlier trusted payment stands, and nothing was paid twice");
    }

    [Fact]
    public async Task AFailureEvent_NeverCreatesAnAnomaly_BecauseNoMoneyMoved()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();
        var phantom = new InMemoryOrderStore.AttemptRow { Id = Guid.NewGuid(), OrderId = h.Store.Order.OrderId, Reference = "cs_other" };

        await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(phantom, type: "checkout.session.expired", payment: "unpaid"));
        await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, type: "checkout.session.expired", payment: "unpaid", order: Guid.NewGuid()));

        h.Anomalies.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task APaymobMismatch_IsRecordedWithItsOwnEventIdentity_AndTheCallbackCurrency()
    {
        var h = new Harness();
        var attempt = await h.StartPaymobAsync();

        var result = await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id, cents: 5, currency: "EGP"));

        result.Reason.Should().Be("amount_or_currency_mismatch");
        var record = h.Anomalies.Records.Should().ContainSingle().Subject;
        record.Provider.Should().Be("Paymob");
        record.ProviderEventId.Should().StartWith("paymob:");
        (record.ReceivedAmountMinor, record.ReceivedCurrency, record.ExpectedAmountMinor, record.ExpectedCurrency).Should().Be((5L, "EGP", 9100L, "USD"));
        NothingWasApplied(h, attempt);
    }

    [Fact]
    public async Task IfTheEvidenceCannotBeStored_TheWebhookIsNotAcknowledged_SoTheProviderRedeliversIt()
    {
        var h = new Harness();
        var attempt = await h.StartStripeAsync();
        h.Anomalies.Fail = true;

        var act = () => h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 1));

        await act.Should().ThrowAsync<InvalidOperationException>();
        NothingWasApplied(h, attempt);

        h.Anomalies.Fail = false;
        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 1, eventId: "evt_retry"))).Reason.Should().Be("amount_or_currency_mismatch");
        h.Anomalies.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task TheAnomalyRecord_AndTheLogs_CarryNoSignatureSecretPayloadOrReference()
    {
        var logger = new ListLogger();
        var h = new Harness(logger);
        var attempt = await h.StartStripeAsync(session: "cs_test_reference_must_not_leak");
        var request = h.StripeEvent(attempt, amount: 1, eventId: "evt_secrets");

        await h.Webhooks.HandleAsync("Stripe", request);
        var paymob = new Harness(logger);
        var pm = await paymob.StartPaymobAsync();
        var callback = paymob.PaymobCallback(pm.Id, cents: 5);
        await paymob.Webhooks.HandleAsync("Paymob", callback);

        var evidence = System.Text.Json.JsonSerializer.Serialize(h.Anomalies.Records.Concat(paymob.Anomalies.Records));
        var everything = evidence + "\n" + string.Join("\n", logger.Lines);
        var forbidden = new[]
        {
            request.Headers["Stripe-Signature"], request.RawBody, callback.Query["hmac"], callback.RawBody,
            "cs_test_reference_must_not_leak", "egy_csk_test_x", TestProviders.StripeWebhookSecret, TestProviders.PaymobHmacSecret, "4097558"
        };
        foreach (var secret in forbidden)
            everything.Should().NotContain(secret);
        typeof(PaymentAnomalyRecord).GetProperties().Select(x => x.Name)
            .Should().NotContain(n => n.Contains("Payload") || n.Contains("Signature") || n.Contains("Secret") || n.Contains("Reference") && n != "ReasonCode" || n.Contains("Customer"));
    }

    [Fact]
    public async Task AFailedPaymobTransaction_IsNotAFailedAttempt_SoALaterSuccessInTheSameIntentionStillCounts()
    {
        var h = new Harness();
        var attempt = await h.StartPaymobAsync();

        var declined = await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id, change: o => o["success"] = false));
        declined.Outcome.Should().Be(WebhookOutcome.Acknowledged);
        Untouched(h, attempt);

        (await h.Webhooks.HandleAsync("Paymob", h.PaymobCallback(attempt.Id))).Outcome.Should().Be(WebhookOutcome.Applied);
        h.Store.Order.Status.Should().Be(OrderStatus.Paid);
    }

    // ───────────── operational signals: logs with stable event ids, and counters ─────────────

    [Fact]
    public async Task ANewAnomaly_IsCountedAndLoggedOnceAtWarning_WithAStableEventId_AndNoSensitiveText()
    {
        using var meter = new MeterCapture();
        var logger = new RecordingLogger<PaymentWebhookService>();
        var h = new Harness(logger);
        var attempt = await h.StartStripeAsync();

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 100, eventId: "evt_sig_1"));

        result.Outcome.Should().Be(WebhookOutcome.AnomalyRecorded);
        meter.Sum(PaymentsTelemetry.AnomaliesRecordedCounter, ("provider", "Stripe"), ("kind", PaymentAnomalyKind.AmountOrCurrencyMismatch)).Should().Be(1);
        meter.Sum(PaymentsTelemetry.AnomaliesDeduplicatedCounter).Should().Be(0);
        meter.Sum(PaymentsTelemetry.WebhookEventsCounter, ("provider", "Stripe"), ("outcome", "AnomalyRecorded")).Should().Be(1);

        var entry = logger.With(PaymentsLogEvents.AnomalyRecorded).Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Event.Name.Should().Be("AnomalyRecorded");
        entry.Message.Should().Contain("evt_sig_1").And.Contain("Stripe").And.Contain(PaymentAnomalyKind.AmountOrCurrencyMismatch).And.Contain("amount_or_currency_mismatch");
        foreach (var line in logger.Entries.Select(e => e.Message))
        {
            line.ToLowerInvariant().Should().NotContain("whsec").And.NotContain("signature").And.NotContain("secret").And.NotContain("hmac")
                .And.NotContain("cs_test").And.NotContain(TestProviders.StripeWebhookSecret);
        }
    }

    [Fact]
    public async Task ARedeliveredAnomaly_IsCountedAsADuplicate_AndLoggedQuietly_NotAsANewIncident()
    {
        using var meter = new MeterCapture();
        var logger = new RecordingLogger<PaymentWebhookService>();
        var h = new Harness(logger);
        var attempt = await h.StartStripeAsync();

        for (var i = 0; i < 3; i++)
            (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 100, eventId: "evt_sig_dup"))).Outcome.Should().Be(WebhookOutcome.AnomalyRecorded);

        meter.Sum(PaymentsTelemetry.AnomaliesRecordedCounter).Should().Be(1, "one new anomaly");
        meter.Sum(PaymentsTelemetry.AnomaliesDeduplicatedCounter, ("provider", "Stripe")).Should().Be(2, "two redeliveries absorbed by the existing record");
        logger.With(PaymentsLogEvents.AnomalyRecorded).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        logger.With(PaymentsLogEvents.AnomalyDuplicate).Should().HaveCount(2).And.OnlyContain(e => e.Level == LogLevel.Information, "a redelivery must not raise an alert");
        h.Anomalies.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task APersistenceFailure_IsCountedAndLoggedAtError_WithoutTheExceptionTextOrObject_AndStillPropagates()
    {
        using var meter = new MeterCapture();
        var logger = new RecordingLogger<PaymentWebhookService>();
        var h = new Harness(logger);
        var attempt = await h.StartStripeAsync();
        h.Anomalies.Fail = true;

        var act = () => h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt, amount: 100, eventId: "evt_sig_fail"));

        await act.Should().ThrowAsync<InvalidOperationException>("the host must answer 5xx so the provider delivers the event again");
        meter.Sum(PaymentsTelemetry.AnomalyPersistenceFailuresCounter, ("provider", "Stripe")).Should().Be(1);
        meter.Sum(PaymentsTelemetry.AnomaliesRecordedCounter).Should().Be(0, "nothing was stored, so nothing is counted as recorded");
        meter.Sum(PaymentsTelemetry.WebhookEventsCounter, ("outcome", "Error")).Should().Be(1);
        var entry = logger.With(PaymentsLogEvents.AnomalyPersistenceFailed).Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.HasException.Should().BeFalse("a storage exception can carry connection details; only its type is logged");
        entry.Message.Should().Contain("evt_sig_fail").And.Contain("InvalidOperationException").And.NotContain("storage unavailable");
        logger.With(PaymentsLogEvents.AnomalyRecorded).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnmatchedFailureEvent_IsLoggedAtInformation_AndCountedAsAcknowledged_NotAsAnAnomaly()
    {
        using var meter = new MeterCapture();
        var logger = new RecordingLogger<PaymentWebhookService>();
        var h = new Harness(logger);
        var phantom = new InMemoryOrderStore.AttemptRow { Id = Guid.NewGuid(), Reference = "cs_x", Provider = "Stripe" };

        var result = await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(phantom, type: "checkout.session.expired", payment: "unpaid"));

        result.Should().Be(new WebhookResult(WebhookOutcome.Acknowledged, "unmatched_event_ignored"));
        logger.With(PaymentsLogEvents.UnmatchedEventIgnored).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
        meter.Sum(PaymentsTelemetry.WebhookEventsCounter, ("outcome", "Acknowledged")).Should().Be(1);
        meter.Sum(PaymentsTelemetry.AnomaliesRecordedCounter).Should().Be(0);
        h.Anomalies.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task AMatchingPayment_RaisesNoAnomalySignalAtAll()
    {
        using var meter = new MeterCapture();
        var logger = new RecordingLogger<PaymentWebhookService>();
        var h = new Harness(logger);
        var attempt = await h.StartStripeAsync();

        (await h.Webhooks.HandleAsync("Stripe", h.StripeEvent(attempt))).Outcome.Should().Be(WebhookOutcome.Applied);

        meter.Sum(PaymentsTelemetry.AnomaliesRecordedCounter).Should().Be(0);
        meter.Sum(PaymentsTelemetry.AnomalyPersistenceFailuresCounter).Should().Be(0);
        meter.Sum(PaymentsTelemetry.WebhookEventsCounter, ("outcome", "Applied")).Should().Be(1);
        logger.Entries.Should().NotContain(e => e.Event.Id >= 7101 && e.Event.Id <= 7105);
    }
}

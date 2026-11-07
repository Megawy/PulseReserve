using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Reconciliation and refund workers with several providers registered: they must always consult the provider recorded on
/// the attempt/refund, treat an ambiguous answer as "unknown", never complete anything on an unverified answer, and send
/// providers that cannot look up or refund through the API to an operator instead of guessing.
/// </summary>
public sealed class ProviderWorkflowTests
{
    private static readonly WorkflowOptions Options = new();

    // ───────────── reconciliation ─────────────

    private sealed class Rec
    {
        public InMemoryOrderStore Store { get; } = new("USD", 91m);
        public ConfigurableGateway Gateway { get; } = new();
        public ScriptedProvider Stripe { get; } = new("Stripe", false, "USD");
        public ScriptedProvider Paymob { get; } = new("Paymob", false, "USD") { Lookup = false, Refunds = false };
        public DispatchFake Fake { get; private set; } = null!;
        public List<string> Outcomes { get; } = [];
        public bool AttemptAbandoned { get; private set; }
        public bool AttemptCancelled { get; private set; }
        public ReconciliationService Service { get; private set; } = null!;
        public TimeSpan AttemptAge { get; set; } = TimeSpan.FromMinutes(30);

        public async Task<Rec> StartAsync(string provider, string? reference = "ref_1", bool orderStillPayable = true)
        {
            var registry = new PaymentProviderRegistry([Stripe, Paymob]);
            var payments = new PaymentService(Store, registry, Gateway);
            var started = await payments.InitiateAsync(Store.CustomerId, Store.Order.OrderId, "k", provider);
            started.IsSuccess.Should().BeTrue();
            var row = Store.Attempts.Single();
            if (reference is null)
                row.Reference = null;
            else
                row.Reference = reference;
            if (!orderStillPayable)
                Store.Order = Store.Order with { Status = OrderStatus.Cancelled };

            var workflow = DispatchFake.Create<IWorkflowStore>(out var fake);
            Fake = fake;
            var task = new ReconciliationTask(Guid.NewGuid(), ReconciliationKind.ResolveStaleAttempt, row.Id, WorkState.Processing, 1, null, null,
                DateTime.UtcNow, DateTime.UtcNow, null);
            fake.Handlers["DiscoverReconciliationTasksAsync"] = _ => 0;
            fake.Handlers["EnsureRefundsForRefundRequiredOrdersAsync"] = _ => 0;
            fake.Handlers["ClaimTasksAsync"] = _ => (IReadOnlyList<ReconciliationTask>)[task];
            fake.Handlers["GetAttemptAsync"] = _ => new AttemptRecord(row.Id, row.OrderId, row.Status, row.Provider, row.Reference, DateTime.UtcNow.AddMinutes(-30), AttemptAge);
            fake.Handlers["CompleteTaskAsync"] = a => { Outcomes.Add("done:" + a[1]); return null; };
            fake.Handlers["RetryTaskLaterAsync"] = a => { Outcomes.Add("retry:" + a[1]); return null; };
            fake.Handlers["AbandonAttemptAsync"] = _ => { AttemptAbandoned = true; return null; };
            fake.Handlers["CancelAttemptAsync"] = _ => { AttemptCancelled = true; return null; };
            Service = new ReconciliationService(workflow, Store, registry, Gateway, payments, Options, NullLogger<ReconciliationService>.Instance);
            return this;
        }
    }

    [Fact]
    public async Task AStaleAttempt_IsResolvedByAskingThe_ProviderItWasCreatedWith_WithTheOrderFactsToVerify()
    {
        var r = await new Rec().StartAsync("Stripe");
        r.Stripe.OnStatus = _ => ProviderPaymentState.Succeeded;

        await r.Service.RunAsync();

        r.Stripe.StatusCalls.Should().Be(1);
        r.Paymob.StatusCalls.Should().Be(0, "an attempt is never examined by another provider");
        r.Stripe.LastStatusQuery!.Amount.Should().Be(91m);
        r.Stripe.LastStatusQuery.Currency.Should().Be("USD");
        r.Stripe.LastStatusQuery.OrderId.Should().Be(r.Store.Order.OrderId);
        r.Store.Order.Status.Should().Be(OrderStatus.Paid);
        r.Outcomes.Should().Equal("done:Provider reported Succeeded");
    }

    [Fact]
    public async Task AnAmbiguousLookup_IsUnknown_NeverASuccessOrAFailure()
    {
        var r = await new Rec().StartAsync("Stripe");
        r.Stripe.OnStatus = _ => throw new TimeoutException("provider timed out");

        await r.Service.RunAsync();

        r.Outcomes.Should().Equal("retry:Provider status unknown");
        r.Store.Attempts.Single().Status.Should().Be(PaymentAttemptStatus.Initiated);
        r.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
        r.Store.FinalizeCalls.Should().Be(0);
    }

    [Fact]
    public async Task AnHttpTimeoutOnALookup_IsUnknownToo_NotAnUnhandledCancellation()
    {
        var r = await new Rec().StartAsync("Stripe");
        r.Stripe.OnStatus = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());

        await r.Service.RunAsync();

        r.Outcomes.Should().Equal("retry:Provider status unknown");
        r.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
        r.Store.FinalizeCalls.Should().Be(0);
    }

    [Fact]
    public async Task APaymentThatDoesNotMatchTheOrder_StaysUnresolved()
    {
        var r = await new Rec().StartAsync("Stripe");
        r.Stripe.OnStatus = _ => throw new InvalidOperationException("The Stripe session does not match the attempt's amount, currency or reference.");

        await r.Service.RunAsync();

        r.Outcomes.Should().ContainSingle().Which.Should().StartWith("retry:");
        r.Store.Order.Status.Should().Be(OrderStatus.PendingPayment);
    }

    [Fact]
    public async Task AProviderWithoutAVerifiedLookup_IsNotAskedAndStaysPending_UntilAWebhookOrAnOperatorDecides()
    {
        var r = await new Rec().StartAsync("Paymob");

        await r.Service.RunAsync();

        r.Paymob.StatusCalls.Should().Be(0, "no lookup endpoint is invented");
        r.Outcomes.Should().Equal("retry:Provider still pending");
        r.Store.Attempts.Single().Status.Should().Be(PaymentAttemptStatus.Initiated);
        r.AttemptAbandoned.Should().BeFalse();
    }

    [Fact]
    public async Task AnAbandonedPaymobAttempt_OnAnUnpayableOrder_StopsBeingTracked_ButALateWebhookIsStillAccepted()
    {
        var r = await new Rec { AttemptAge = TimeSpan.FromDays(2) }.StartAsync("Paymob", orderStillPayable: false);

        await r.Service.RunAsync();

        r.AttemptAbandoned.Should().BeTrue();
        r.Outcomes.Should().Equal("done:Abandoned: unpayable order, provider never answered");
    }

    [Fact]
    public async Task AnAttemptThatNeverGotAReference_IsCancelled_BecauseNoCheckoutUrlWasEverShown()
    {
        var r = await new Rec().StartAsync("Paymob", reference: null);

        await r.Service.RunAsync();

        r.AttemptCancelled.Should().BeTrue();
        r.Paymob.StatusCalls.Should().Be(0);
    }

    [Fact]
    public async Task AnAttemptWhoseProviderIsNoLongerConfigured_IsRetriedLater_NotHandedToAnotherProvider()
    {
        var r = await new Rec().StartAsync("Stripe");
        // Rebuild the service with a registry that no longer contains Stripe.
        var registry = new PaymentProviderRegistry([r.Paymob]);
        var service = new ReconciliationService(DispatchFake.Create<IWorkflowStore>(out var fake), r.Store, registry, r.Gateway,
            new PaymentService(r.Store, registry, r.Gateway), Options, NullLogger<ReconciliationService>.Instance);
        foreach (var (name, handler) in r.Fake.Handlers)
            fake.Handlers[name] = handler;

        await service.RunAsync();

        r.Outcomes.Should().Equal("retry:The attempt's provider is not configured");
        r.Paymob.StatusCalls.Should().Be(0);
    }

    // ───────────── refunds ─────────────

    private static Refund RefundRow(string provider, string state = RefundState.Requested, int submission = 1) =>
        new(Guid.Parse("33333333-3333-4333-8333-333333333333"), Guid.NewGuid(), Guid.Parse("44444444-4444-4444-8444-444444444444"), 91m, "USD",
            RefundReason.PaymentAfterSaleFailed, state, provider, false, submission, null, 1, null, null, DateTime.UtcNow, null, DateTime.UtcNow, DateTime.UtcNow);

    private sealed class Ref
    {
        public ScriptedProvider Stripe { get; } = new("Stripe", false, "USD");
        public ScriptedProvider Paymob { get; } = new("Paymob", false, "USD") { Lookup = false, Refunds = false };
        public DispatchFake Fake { get; private set; } = null!;
        public List<string> Events { get; } = [];
        public RefundService Service { get; private set; } = null!;

        public Ref(Refund refund, string? attemptReference = "cs_ref")
        {
            var workflow = DispatchFake.Create<IWorkflowStore>(out var fake);
            Fake = fake;
            fake.Handlers["ClaimRefundsAsync"] = _ => (IReadOnlyList<Refund>)[refund];
            fake.Handlers["MarkRefundUnknownAsync"] = _ => { Events.Add("unknown"); return true; };
            fake.Handlers["GetAttemptAsync"] = _ => new AttemptRecord(refund.PaymentAttemptId, refund.OrderId, "Succeeded", refund.Provider, attemptReference, DateTime.UtcNow, TimeSpan.Zero);
            fake.Handlers["MarkRefundSucceededAsync"] = a => { Events.Add("succeeded:" + a[1]); return null; };
            fake.Handlers["MarkRefundPendingAsync"] = a => { Events.Add("pending:" + a[1]); return null; };
            fake.Handlers["MarkRefundRejectedAsync"] = a => { Events.Add("rejected:" + a[1]); return null; };
            fake.Handlers["DeferRefundAsync"] = a => { Events.Add("deferred"); return null; };
            fake.Handlers["MarkRefundNeedsReviewAsync"] = a => { Events.Add("needs-review:" + a[1]); return null; };
            Service = new RefundService(workflow, new PaymentProviderRegistry([Stripe, Paymob]), Options, NullLogger<RefundService>.Instance);
        }
    }

    [Fact]
    public async Task ARefund_GoesThroughTheProviderThatTookThePayment_OnlyCompletedWhenItConfirms()
    {
        var r = new Ref(RefundRow("Stripe"));

        await r.Service.RunAsync();

        r.Stripe.RefundCalls.Should().Be(1);
        r.Paymob.RefundCalls.Should().Be(0);
        r.Events.Should().Equal("unknown", "succeeded:re_1");
    }

    [Fact]
    public async Task ARefundTheProviderOnlyAccepted_IsPending_NotComplete()
    {
        var r = new Ref(RefundRow("Stripe"));
        r.Stripe.OnRefund = _ => new ProviderRefundResult(true, false, "re_9", null, false);

        await r.Service.RunAsync();

        r.Events.Should().Equal("unknown", "pending:re_9");
    }

    [Fact]
    public async Task TheRefundIntent_IsPersistedAsUnknown_BEFORE_TheProviderIsContacted()
    {
        var r = new Ref(RefundRow("Stripe"));
        List<string>? seenAtCall = null;
        r.Stripe.OnRefund = _ =>
        {
            seenAtCall = [.. r.Events]; // what was already durable when the provider call started
            return new ProviderRefundResult(true, true, "re_1", null, false);
        };

        await r.Service.RunAsync();

        seenAtCall.Should().Equal("unknown");
    }

    [Fact]
    public async Task ARefund_IsNeverCompleteOnAnAmbiguousAnswerOrATimeout()
    {
        foreach (Func<ProviderRefundRequest, ProviderRefundResult> answer in new Func<ProviderRefundRequest, ProviderRefundResult>[]
                 {
                     _ => throw new TimeoutException("answer lost"),
                     _ => throw new HttpRequestException("503"),
                     _ => new ProviderRefundResult(false, false, null, "declined", false),
                     _ => new ProviderRefundResult(true, false, "re_1", null, false)
                 })
        {
            var r = new Ref(RefundRow("Stripe"));
            r.Stripe.OnRefund = answer;

            await r.Service.RunAsync();

            r.Events.Should().NotContain(e => e.StartsWith("succeeded"));
        }
    }

    [Fact]
    public async Task AnHttpTimeoutOnARefund_StaysUnknown_LikeAnyOtherLostAnswer()
    {
        var r = new Ref(RefundRow("Stripe"));
        r.Stripe.OnRefund = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());

        await r.Service.RunAsync();

        r.Events.Should().Equal("unknown", "deferred");
        r.Stripe.RefundCalls.Should().Be(1);
    }

    [Fact]
    public async Task AnAmbiguousRefundAnswer_StaysUnknown_AndIsNeverResubmittedBlindly()
    {
        var r = new Ref(RefundRow("Stripe"));
        r.Stripe.OnRefund = _ => throw new TimeoutException("answer lost");

        await r.Service.RunAsync();

        r.Events.Should().Equal("unknown", "deferred");
        r.Stripe.RefundCalls.Should().Be(1);
    }

    [Fact]
    public async Task AnUnknownRefund_IsLookedUpWithItsOwnContext_AndOnlyResubmittedWhenTheProviderHasNoRecord()
    {
        var found = new Ref(RefundRow("Stripe", RefundState.Unknown));
        found.Stripe.OnRefundStatus = _ => ProviderRefundState.Succeeded;
        await found.Service.RunAsync();
        found.Events.Should().Equal("succeeded:");
        found.Stripe.RefundCalls.Should().Be(0, "the provider already had it");
        found.Stripe.LastRefundStatusQuery!.IdempotencyKey.Should().Be($"refund-33333333333343338333333333333333-1");
        found.Stripe.LastRefundStatusQuery.RefundId.Should().Be(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        found.Stripe.LastRefundStatusQuery.PaymentProviderReference.Should().Be("cs_ref");

        var missing = new Ref(RefundRow("Stripe", RefundState.Unknown));
        missing.Stripe.OnRefundStatus = _ => ProviderRefundState.NotFound;
        await missing.Service.RunAsync();
        missing.Stripe.RefundCalls.Should().Be(1, "only a definitive 'no such refund' permits submitting again");

        var failing = new Ref(RefundRow("Stripe", RefundState.Unknown));
        failing.Stripe.OnRefundStatus = _ => throw new HttpRequestException("503");
        await failing.Service.RunAsync();
        failing.Events.Should().Equal("deferred");
        failing.Stripe.RefundCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(ProviderRefundState.Pending, "pending:")]
    [InlineData(ProviderRefundState.Failed, "rejected:Provider reported the refund failed")]
    public async Task AnUnconfirmedOrFailedRefundLookup_NeverCompletesTheRefund(ProviderRefundState answer, string expected)
    {
        var r = new Ref(RefundRow("Stripe", RefundState.Pending));
        r.Stripe.OnRefundStatus = _ => answer;

        await r.Service.RunAsync();

        r.Events.Should().Equal(expected);
        r.Events.Should().NotContain(e => e.StartsWith("succeeded"));
    }

    [Fact]
    public async Task ARefundForAProviderWithoutApiRefunds_GoesStraightToAnOperator_AndIsNeverMarkedComplete()
    {
        var r = new Ref(RefundRow("Paymob"));

        await r.Service.RunAsync();

        r.Events.Should().ContainSingle().Which.Should().StartWith("needs-review:").And.Contain("refund the payment in the provider dashboard");
        r.Paymob.RefundCalls.Should().Be(0);
        r.Paymob.RefundStatusCalls.Should().Be(0);
        r.Stripe.RefundCalls.Should().Be(0, "no fallback to another provider");
        r.Fake.Calls.Should().NotContain("MarkRefundUnknownAsync").And.NotContain("MarkRefundSucceededAsync");
    }

    [Fact]
    public async Task ARefundWhoseProviderIsNoLongerConfigured_IsDeferred_NotRoutedElsewhere()
    {
        var r = new Ref(RefundRow("Adyen"));

        await r.Service.RunAsync();

        r.Events.Should().Equal("deferred");
        r.Stripe.RefundCalls.Should().Be(0);
    }

    [Fact]
    public async Task ARefundWithoutAProviderReference_IsRejectedDefinitively()
    {
        var r = new Ref(RefundRow("Stripe"), attemptReference: null);

        await r.Service.RunAsync();

        r.Events.Should().Equal("unknown", "rejected:The payment has no provider reference");
        r.Stripe.RefundCalls.Should().Be(0);
    }
}

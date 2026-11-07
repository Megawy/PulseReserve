using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Every worker and administrator write reaches the store labelled with who caused it and on what basis: a provider answer is a
/// <c>ProviderVerified</c> fact, an escalation or an intent record is an <c>ApplicationDecision</c>, a command is an
/// <c>AdministratorAction</c> and a statement about the outside world an <c>AdministratorStatement</c>. (The database trigger that
/// turns these labels into history rows is exercised only by the PostgreSQL tests, which have not been executed.)
/// </summary>
public sealed class TransitionLabellingTests
{
    private static readonly WorkflowOptions Options = new();
    private static readonly Guid Admin = Guid.NewGuid();

    private static Refund RefundRow(string provider, string state = RefundState.Requested) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 91m, "USD", RefundReason.PaymentAfterSaleFailed, state, provider, false, 1, null, 1, null, null,
            DateTime.UtcNow, null, DateTime.UtcNow, DateTime.UtcNow);

    private static (RefundService Service, Dictionary<string, TransitionInfo?> Seen, ScriptedProvider Stripe, ScriptedProvider Paymob) RefundWorld(Refund refund)
    {
        var stripe = new ScriptedProvider("Stripe", false, "USD");
        var paymob = new ScriptedProvider("Paymob", false, "USD") { Lookup = false, Refunds = false };
        var seen = new Dictionary<string, TransitionInfo?>();
        var workflow = DispatchFake.Create<IWorkflowStore>(out var fake);
        object? Capture(string name)
        {
            seen[name] = FinancialTransitionContext.Current;
            return null;
        }

        fake.Handlers["ClaimRefundsAsync"] = _ => (IReadOnlyList<Refund>)[refund];
        fake.Handlers["GetAttemptAsync"] = _ => new AttemptRecord(refund.PaymentAttemptId, refund.OrderId, "Succeeded", refund.Provider, "ref_1", DateTime.UtcNow, TimeSpan.Zero);
        fake.Handlers["MarkRefundUnknownAsync"] = _ =>
        {
            Capture("unknown");
            return true;
        };
        fake.Handlers["MarkRefundSucceededAsync"] = _ => Capture("succeeded");
        fake.Handlers["MarkRefundPendingAsync"] = _ => Capture("pending");
        fake.Handlers["MarkRefundRejectedAsync"] = _ => Capture("rejected");
        fake.Handlers["DeferRefundAsync"] = _ => Capture("deferred");
        fake.Handlers["MarkRefundNeedsReviewAsync"] = _ => Capture("needs-review");
        return (new RefundService(workflow, new PaymentProviderRegistry([stripe, paymob]), Options, NullLogger<RefundService>.Instance), seen, stripe, paymob);
    }

    [Fact]
    public async Task TheRefundIntent_IsAnApplicationDecision_AndTheProviderConfirmation_IsAProviderVerifiedFact()
    {
        var (service, seen, _, _) = RefundWorld(RefundRow("Stripe"));

        await service.RunAsync();

        var intent = seen["unknown"]!;
        (intent.Source, intent.FactBasis, intent.Reason).Should().Be((TransitionSource.Worker, FactBasis.ApplicationDecision, TransitionReason.RefundIntent));
        var confirmed = seen["succeeded"]!;
        (confirmed.Source, confirmed.FactBasis, confirmed.Reason).Should().Be((TransitionSource.Worker, FactBasis.ProviderVerified, TransitionReason.ProviderAnswer));
        confirmed.CorrelationId.Should().Be(intent.CorrelationId, "one worker pass is one correlation id");
    }

    [Fact]
    public async Task AnEscalationToAnOperator_IsAnApplicationDecision_NeverAProviderFact()
    {
        var (service, seen, _, _) = RefundWorld(RefundRow("Paymob"));

        await service.RunAsync();

        var info = seen["needs-review"]!;
        (info.Source, info.FactBasis, info.Reason).Should().Be((TransitionSource.Worker, FactBasis.ApplicationDecision, TransitionReason.NeedsOperator));
        seen.Keys.Should().NotContain("succeeded", "nothing is provider-confirmed for a provider that cannot refund through the API");
    }

    [Fact]
    public async Task AnAmbiguousAnswer_AndItsEscalation_AreNotLabelledAsProviderFacts()
    {
        var (service, seen, stripe, _) = RefundWorld(RefundRow("Stripe", RefundState.Unknown));
        stripe.OnRefundStatus = _ => ProviderRefundState.NotFound;
        stripe.OnRefund = _ => throw new TimeoutException("lost");

        await service.RunAsync();

        seen["deferred"]!.FactBasis.Should().Be(FactBasis.ApplicationDecision);
        seen.Keys.Should().NotContain("succeeded").And.NotContain("rejected");
    }

    // ───────────── administrators ─────────────

    private static (PaymentOperationsService Service, Dictionary<string, TransitionInfo?> Seen) AdminWorld(string provider = "Paymob")
    {
        var seen = new Dictionary<string, TransitionInfo?>();
        var workflow = DispatchFake.Create<IWorkflowStore>(out var fake);
        var refund = RefundRow(provider, RefundState.NeedsReview);
        fake.Handlers["GetRefundAsync"] = _ => refund;
        fake.Handlers["RecordManualRefundAsync"] = _ =>
        {
            seen["manual"] = FinancialTransitionContext.Current;
            return new ManualRefundResult(ManualRefundOutcome.Recorded, refund with { State = RefundState.ManuallyRecorded });
        };
        fake.Handlers["RetryRefundAsync"] = _ =>
        {
            seen["retry"] = FinancialTransitionContext.Current;
            return new OperationResult(OperationOutcome.Done);
        };
        fake.Handlers["RequestRefundAsync"] = _ =>
        {
            seen["request"] = FinancialTransitionContext.Current;
            return (new OperationResult(OperationOutcome.Done), refund);
        };
        fake.Handlers["ResolveExceptionAsync"] = _ =>
        {
            seen["resolve"] = FinancialTransitionContext.Current;
            return new PaymentExceptionResolveResult(PaymentExceptionResolveOutcome.NotFound, null);
        };
        var registry = new PaymentProviderRegistry([new ScriptedProvider("Paymob", false, "USD") { Lookup = false, Refunds = false }, new ScriptedProvider("Stripe", false, "USD")]);
        return (new PaymentOperationsService(workflow, registry), seen);
    }

    [Fact]
    public async Task AManualRefund_IsAnAdministratorStatement_WithTheActor_NeverAProviderFact()
    {
        var (service, seen) = AdminWorld();

        await service.RecordManualRefundAsync(Guid.NewGuid(), new ManualRefundRecord(true, 91m, "USD", "ref", null), Admin);

        var info = seen["manual"]!;
        (info.Source, info.FactBasis, info.ActorUserId, info.Reason)
            .Should().Be((TransitionSource.Administrator, FactBasis.AdministratorStatement, (Guid?)Admin, TransitionReason.ManualRefundStatement));
        info.FactBasis.Should().NotBe(FactBasis.ProviderVerified);
    }

    [Fact]
    public async Task AnExceptionResolution_IsAnAdministratorStatement()
    {
        var (service, seen) = AdminWorld();

        await service.ResolveExceptionAsync(Guid.NewGuid(), new PaymentExceptionResolutionRecord(true, 91m, "USD", null, null), Admin);

        var info = seen["resolve"]!;
        (info.FactBasis, info.ActorUserId, info.Reason).Should().Be((FactBasis.AdministratorStatement, (Guid?)Admin, TransitionReason.ExceptionResolutionStatement));
    }

    [Fact]
    public async Task ARetryAndARefundRequest_AreAdministratorActions_NotStatements()
    {
        var (service, seen) = AdminWorld();

        await service.RetryRefundAsync(Guid.NewGuid(), Admin);
        await service.RequestRefundAsync(Guid.NewGuid(), Admin);

        (seen["retry"]!.FactBasis, seen["retry"]!.Reason).Should().Be((FactBasis.AdministratorAction, TransitionReason.AdministratorRetry));
        (seen["request"]!.FactBasis, seen["request"]!.Reason).Should().Be((FactBasis.AdministratorAction, TransitionReason.AdministratorRequest));
        seen.Values.Should().OnlyContain(i => i!.ActorUserId == Admin);
    }

    [Fact]
    public async Task ARejectedAdministratorRequest_NeverReachesTheStore_SoItLeavesNoHistory()
    {
        var (service, seen) = AdminWorld();

        var result = await service.RecordManualRefundAsync(Guid.NewGuid(), new ManualRefundRecord(false, 91m, "USD", null, null), Admin);

        result.IsFailure.Should().BeTrue();
        seen.Should().BeEmpty();
    }
}

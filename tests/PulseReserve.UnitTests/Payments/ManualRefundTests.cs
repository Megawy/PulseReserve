using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// The service rules for recording a refund performed in a provider dashboard. The transactional guarantees (row lock,
/// duplicate and concurrent submissions, constraints, audit row) live in SQL and are covered by the PostgreSQL integration
/// tests, which need Docker and are not run here.
/// </summary>
public sealed class ManualRefundTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private sealed class World
    {
        public required PaymentOperationsService Service { get; init; }
        public required DispatchFake Store { get; init; }
        public required ScriptedProvider Paymob { get; init; }
        public required ScriptedProvider Stripe { get; init; }
        public List<ManualRefundRecord> Recorded { get; } = [];
    }

    private static Refund RefundOf(string provider, string state = RefundState.NeedsReview, decimal amount = 91m, string currency = "USD") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount, currency, "PaymentAfterSaleFailed", state, provider, false, 1, null, 1, null, null,
            DateTime.UtcNow, null, DateTime.UtcNow, DateTime.UtcNow);

    private static World Build(Refund? stored, ManualRefundOutcome outcome = ManualRefundOutcome.Recorded)
    {
        var paymob = new ScriptedProvider("Paymob", false, "USD") { Lookup = false, Refunds = false };
        var stripe = new ScriptedProvider("Stripe", false, "USD");
        var store = DispatchFake.Create<IWorkflowStore>(out var fake);
        var world = new World { Service = new PaymentOperationsService(store, new PaymentProviderRegistry([paymob, stripe])), Store = fake, Paymob = paymob, Stripe = stripe };
        fake.Handlers["GetRefundAsync"] = _ => stored;
        fake.Handlers["RecordManualRefundAsync"] = a =>
        {
            world.Recorded.Add((ManualRefundRecord)a[2]!);
            return new ManualRefundResult(outcome, stored is null ? null : stored with { State = RefundState.ManuallyRecorded });
        };
        return world;
    }

    private static ManualRefundRecord Valid(Refund r, bool confirmed = true) => new(confirmed, r.Amount, r.Currency, "PM-1", "done in dashboard");

    [Fact]
    public async Task RecordsARefundWaitingForReview_ForAProviderWithoutApiRefunds_AndNeverCallsTheProvider()
    {
        var refund = RefundOf("Paymob");
        var w = Build(refund);

        var result = await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund), Admin);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(RefundState.ManuallyRecorded).And.NotBe(RefundState.Succeeded);
        w.Recorded.Should().ContainSingle().Which.ExternalReference.Should().Be("PM-1");
        (w.Paymob.RefundCalls + w.Paymob.RefundStatusCalls + w.Paymob.StatusCalls).Should().Be(0);
    }

    [Fact]
    public async Task WithoutTheExplicitConfirmation_NothingIsLookedUpOrWritten()
    {
        var refund = RefundOf("Paymob");
        var w = Build(refund);

        var result = await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund, confirmed: false), Admin);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(PaymentsErrors.ManualRefundNotConfirmed);
        w.Store.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ANonPositiveAmount_IsRejectedBeforeTheStore(int amount)
    {
        var refund = RefundOf("Paymob");
        var w = Build(refund);

        var result = await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund) with { Amount = amount }, Admin);

        result.Error.Should().Be(PaymentsErrors.ManualRefundInvalid);
        w.Store.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("")]
    public async Task AMalformedCurrency_IsRejectedBeforeTheStore(string currency)
    {
        var refund = RefundOf("Paymob");
        var w = Build(refund);

        var result = await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund) with { Currency = currency }, Admin);

        result.Error.Should().Be(PaymentsErrors.ManualRefundInvalid);
        w.Store.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task OverlongText_IsRejected()
    {
        var refund = RefundOf("Paymob");
        var w = Build(refund);

        (await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund) with { ExternalReference = new string('x', 201) }, Admin)).Error.Should().Be(PaymentsErrors.ManualRefundInvalid);
        (await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund) with { Note = new string('x', 301) }, Admin)).Error.Should().Be(PaymentsErrors.ManualRefundInvalid);
    }

    [Fact]
    public async Task AnAutomaticProvider_IsNeverRecordedByHand_SoARealRefundCannotBeDoubled()
    {
        var refund = RefundOf("Stripe");
        var w = Build(refund);

        var result = await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund), Admin);

        result.Error.Should().Be(PaymentsErrors.ManualRefundNotAllowed);
        w.Recorded.Should().BeEmpty();
        w.Stripe.RefundCalls.Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownProvider_IsNotAssumedToBeManual()
    {
        var refund = RefundOf("Adyen");
        var w = Build(refund);

        var result = await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund), Admin);

        result.Error.Should().Be(PaymentsErrors.ManualRefundNotAllowed);
        w.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingRefund_IsNotFound()
    {
        var w = Build(null);

        var result = await w.Service.RecordManualRefundAsync(Guid.NewGuid(), Valid(RefundOf("Paymob")), Admin);

        result.IsFailure.Should().BeTrue();
        w.Recorded.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ManualRefundOutcome.NotAllowed, "Operations.ManualRefundNotAllowed")]
    [InlineData(ManualRefundOutcome.AmountMismatch, "Operations.ManualRefundMismatch")]
    [InlineData(ManualRefundOutcome.Conflict, "Operations.ManualRefundConflict")]
    public async Task TheStoresVerdictUnderTheRowLock_IsReportedAsAConflict(ManualRefundOutcome outcome, string code)
    {
        var refund = RefundOf("Paymob");
        var w = Build(refund, outcome);

        var result = await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund) with { Amount = 1 }, Admin);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(code);
    }

    [Fact]
    public async Task AnIdempotentReplay_IsASuccess_ThatStillSaysManuallyRecorded()
    {
        var refund = RefundOf("Paymob");
        var w = Build(refund, ManualRefundOutcome.Replay);

        var result = await w.Service.RecordManualRefundAsync(refund.RefundId, Valid(refund), Admin);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(RefundState.ManuallyRecorded);
    }

    [Fact]
    public void ManuallyRecordedAndProviderConfirmedAreDifferentStates_WithSeparateFields()
    {
        RefundState.ManuallyRecorded.Should().NotBe(RefundState.Succeeded);
        typeof(Refund).GetProperties().Select(p => p.Name).Should().Contain(["ProviderRefundId", "ManualReference", "ManuallyRecordedBy", "ManuallyRecordedAtUtc"]);
    }
}

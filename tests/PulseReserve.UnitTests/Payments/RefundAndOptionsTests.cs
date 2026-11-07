using FluentAssertions;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

public class RefundAndOptionsTests
{
    private static Refund Make(int submission) =>
        new(Guid.Parse("11111111-2222-3333-4444-555555555555"), Guid.NewGuid(), Guid.NewGuid(), 10m, "USD", "PaymentAfterSaleFailed",
            "Requested", "Simulator", true, submission, null, 0, null, null, DateTime.UtcNow, null, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void RefundIdempotencyKey_IsStableForAnAmbiguousRetry_AndChangesOnlyAfterADefiniteRefusal()
    {
        Make(1).IdempotencyKey.Should().Be(Make(1).IdempotencyKey);
        Make(1).IdempotencyKey.Should().NotBe(Make(2).IdempotencyKey);
        Make(1).IdempotencyKey.Should().Be("refund-11111111222233334444555555555555-1");
    }

    [Fact]
    public async Task Simulator_Refund_IsDeterministicPerKey_AndLabelledSimulated()
    {
        var provider = new SimulatedPaymentProvider();
        var request = new ProviderRefundRequest(Guid.NewGuid(), "k-1", "sim_x", 10m, "USD");

        var a = await provider.RefundAsync(request);
        var b = await provider.RefundAsync(request);
        var c = await provider.RefundAsync(request with { IdempotencyKey = "k-2" });

        provider.IsSimulated.Should().BeTrue();
        a.Accepted.Should().BeTrue();
        a.Completed.Should().BeTrue();
        a.ProviderRefundId.Should().Be(b.ProviderRefundId).And.StartWith("simref_");
        a.ProviderRefundId.Should().NotBe(c.ProviderRefundId);
    }

    [Fact]
    public async Task Simulator_NeverReportsAPaymentAsSucceededOnItsOwn()
    {
        var provider = new SimulatedPaymentProvider();

        (await provider.GetPaymentStatusAsync(Guid.NewGuid(), "sim_x")).Should().Be(ProviderPaymentState.Pending);
        (await provider.GetPaymentStatusAsync(Guid.NewGuid(), null)).Should().Be(ProviderPaymentState.NotFound);
    }

    [Fact]
    public async Task ProductionDefaults_CannotTakePaymentsRefundOrNotify()
    {
        var provider = new UnavailablePaymentProvider();
        var sender = new UnavailableNotificationSender();

        provider.IsAvailable.Should().BeFalse();
        sender.IsAvailable.Should().BeFalse();
        await FluentActions.Awaiting(() => provider.RefundAsync(new ProviderRefundRequest(Guid.NewGuid(), "k", "r", 1m, "USD"))).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => sender.SendAsync(new NotificationRequest(Guid.NewGuid(), Guid.NewGuid(), "OrderPaid", 1))).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void WorkflowDefaults_AreBoundedAndConservative()
    {
        var options = new WorkflowOptions();

        options.BatchSize.Should().BeInRange(1, 100);
        options.MaxAttempts.Should().BeInRange(3, 20);
        options.BackoffBase.Should().BeGreaterThan(TimeSpan.Zero);
        options.MaxBackoff.Should().BeGreaterThan(options.BackoffBase);
        options.Lease.Should().BeGreaterThan(TimeSpan.FromSeconds(30));
        options.CompletionGrace.Should().BeGreaterThan(TimeSpan.Zero, "a payment completing right now is left to its own request");
    }
}

using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>What an administrator can learn about providers and unresolved work without any secret or payment detail.</summary>
public sealed class OperatorVisibilityTests
{
    private static ScriptedProvider Stripe() => new("Stripe", false, "USD");
    private static ScriptedProvider Paymob() => new("Paymob", false, "USD") { Lookup = false, Refunds = false, Idempotent = false };

    [Fact]
    public void ProvidersAreListedWithWhatTheSystemCanDoWithEach_AndNothingElse()
    {
        var unavailable = new ScriptedProvider("Disabled") { IsAvailable = false };
        var service = new PaymentOperationsService(DispatchFake.Create<IWorkflowStore>(out _), new PaymentProviderRegistry([Paymob(), Stripe(), new PulseReserve.Modules.Payments.Providers.SimulatedPaymentProvider(), unavailable]));

        var providers = service.Providers();

        providers.Select(p => p.Name).Should().Equal("Disabled", "Paymob", "Simulator", "Stripe");
        providers.Single(p => p.Name == "Paymob").Should().Be(new OperationsProvider("Paymob", false, true, false, false) { SupportsIdempotentInitiation = false });
        providers.Single(p => p.Name == "Stripe").Should().Be(new OperationsProvider("Stripe", false, true, true, true));
        providers.Single(p => p.Name == "Simulator").IsSimulated.Should().BeTrue();
        providers.Single(p => p.Name == "Disabled").IsAvailable.Should().BeFalse();
        typeof(OperationsProvider).GetProperties().Select(p => p.Name).Should().BeEquivalentTo("Name", "IsSimulated", "IsAvailable", "SupportsPaymentLookup", "SupportsRefunds", "SupportsIdempotentInitiation");
    }

    [Fact]
    public async Task TasksAboutAnAttemptCarryTheAttemptsProvider_OrderTasksDoNot()
    {
        var attemptId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        ReconciliationTask Task(Guid subject, string kind) =>
            new(Guid.NewGuid(), kind, subject, WorkState.NeedsReview, 8, "Provider still pending", null, DateTime.UtcNow, DateTime.UtcNow, null);
        var seed = DispatchFake.Create<IWorkflowStore>(out var scripted);
        scripted.Handlers["ListTasksAsync"] = _ => (IReadOnlyList<ReconciliationTask>)[
            Task(attemptId, ReconciliationKind.ResolveStaleAttempt), Task(attemptId, ReconciliationKind.FinalizeSuccessfulPayment), Task(orderId, ReconciliationKind.ExpireOrder)];
        var lookups = new List<Guid>();
        scripted.Handlers["GetAttemptAsync"] = a =>
        {
            lookups.Add((Guid)a[0]!);
            return new AttemptRecord(attemptId, orderId, "Initiated", "Paymob", "217503754", DateTime.UtcNow, TimeSpan.Zero);
        };
        var service = new PaymentOperationsService(seed, new PaymentProviderRegistry([Paymob()]));

        var tasks = await service.TasksAsync(WorkState.NeedsReview, 50);

        tasks.Select(t => t.Provider).Should().Equal("Paymob", "Paymob", null);
        lookups.Should().OnlyContain(id => id == attemptId, "an order id is never looked up as an attempt");
    }

    [Fact]
    public async Task ATaskWhoseAttemptIsGone_IsStillListed_WithoutAProvider()
    {
        var seed = DispatchFake.Create<IWorkflowStore>(out var scripted);
        scripted.Handlers["ListTasksAsync"] = _ => (IReadOnlyList<ReconciliationTask>)[
            new ReconciliationTask(Guid.NewGuid(), ReconciliationKind.ResolveStaleAttempt, Guid.NewGuid(), WorkState.Pending, 1, null, null, DateTime.UtcNow, DateTime.UtcNow, null)];
        scripted.Handlers["GetAttemptAsync"] = _ => null;

        var tasks = await new PaymentOperationsService(seed, new PaymentProviderRegistry([Stripe()])).TasksAsync(null, 50);

        tasks.Should().ContainSingle().Which.Provider.Should().BeNull();
    }
}

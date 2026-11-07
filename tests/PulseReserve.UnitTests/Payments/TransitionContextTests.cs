using System.Reflection;
using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// The CODE half of the financial transition history: which source, fact basis, reason, provider event and actor each financial
/// write is labelled with when it reaches the store. The database half (the trigger that writes the row from these labels, in the
/// same transaction) can only be exercised against PostgreSQL; those tests are compiled but have not been executed.
/// </summary>
public sealed class TransitionContextTests
{
    // ───────────── the ambient context itself ─────────────

    [Fact]
    public void AScope_RestoresTheEnclosingOne_AndLeavesNothingBehind()
    {
        FinancialTransitionContext.Current.Should().BeNull();
        using (FinancialTransitionContext.Begin(TransitionSource.Customer, FactBasis.ApplicationDecision, actorUserId: Guid.Empty))
        {
            var outer = FinancialTransitionContext.Current!;
            using (FinancialTransitionContext.Begin(TransitionSource.Worker, FactBasis.ApplicationDecision))
            {
                FinancialTransitionContext.Current!.Source.Should().Be(TransitionSource.Worker);
                FinancialTransitionContext.Current.CorrelationId.Should().Be(outer.CorrelationId, "one request or pass is one correlation id");
            }

            FinancialTransitionContext.Current.Should().Be(outer);
        }

        FinancialTransitionContext.Current.Should().BeNull();
    }

    [Fact]
    public void Refine_ChangesTheBasisAndReason_ButKeepsWhoAndWhere()
    {
        var actor = Guid.NewGuid();
        using var scope = FinancialTransitionContext.Begin(TransitionSource.ProviderWebhook, FactBasis.ProviderVerified, "Stripe", "evt_1", actor);
        using (FinancialTransitionContext.Refine(FactBasis.ApplicationDecision, TransitionReason.SaleConfirmation))
        {
            var info = FinancialTransitionContext.Current!;
            (info.Source, info.FactBasis, info.Provider, info.ProviderEventId, info.ActorUserId, info.Reason)
                .Should().Be((TransitionSource.ProviderWebhook, FactBasis.ApplicationDecision, "Stripe", "evt_1", actor, TransitionReason.SaleConfirmation));
        }

        FinancialTransitionContext.Current!.FactBasis.Should().Be(FactBasis.ProviderVerified);
    }

    [Fact]
    public async Task TheContext_FlowsIntoAwaitedCalls_ButNeverLeaksBackToTheCaller()
    {
        async Task<string?> Inner()
        {
            await Task.Yield();
            return FinancialTransitionContext.Current?.Source;
        }

        using (FinancialTransitionContext.Begin(TransitionSource.Administrator, FactBasis.AdministratorAction))
            (await Inner()).Should().Be(TransitionSource.Administrator);
        (await Inner()).Should().BeNull();
    }

    [Fact]
    public void TheContext_CarriesOnlyIdentifiersAndCodes_NeverASecretOrPayload()
    {
        var names = typeof(TransitionInfo).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray();

        names.Should().BeEquivalentTo("Source", "FactBasis", "Provider", "ProviderEventId", "ActorUserId", "Reason", "CorrelationId");
        names.Should().NotContain(n => n.Contains("Payload") || n.Contains("Signature") || n.Contains("Secret") || n.Contains("Reference") || n.Contains("Customer"));
    }

    // ───────────── what each financial write is labelled with ─────────────

    private static (PaymentWebhookService Webhooks, PaymentService Payments, InMemoryOrderStore Store, ScriptedProvider Provider) WebhookWorld()
    {
        var store = new InMemoryOrderStore("USD", 91m);
        var provider = new ScriptedProvider("Stripe", false, "USD");
        var registry = new PaymentProviderRegistry([provider]);
        var payments = new PaymentService(store, registry, new ConfigurableGateway());
        return (null!, payments, store, provider);
    }

    [Fact]
    public async Task ACustomersPaymentStart_IsLabelledAsTheCustomersOwnDecision()
    {
        var (_, payments, store, _) = WebhookWorld();

        await payments.InitiateAsync(store.CustomerId, store.Order.OrderId, "k1", "Stripe");

        var begin = store.Seen.Single(s => s.Method == "BeginPayment").Info!;
        (begin.Source, begin.FactBasis, begin.ActorUserId).Should().Be((TransitionSource.Customer, FactBasis.ApplicationDecision, store.CustomerId));
        FinancialTransitionContext.Current.Should().BeNull("the scope ended with the call");
    }

    [Fact]
    public async Task AnAmbiguousAnswerFromAProviderThatCannotDeduplicate_IsLabelledWithItsOwnReason()
    {
        var store = new InMemoryOrderStore("USD", 91m);
        var provider = new ScriptedProvider("Paymob", false, "USD") { Idempotent = false, OnInitiate = _ => throw new TimeoutException("lost") };
        var payments = new PaymentService(store, new PaymentProviderRegistry([provider]), new ConfigurableGateway());

        await payments.InitiateAsync(store.CustomerId, store.Order.OrderId, "k1", "Paymob");

        store.Seen.Single(s => s.Method == "CancelUncertain").Info!.Reason.Should().Be(TransitionReason.InitiationUncertain);
    }
}

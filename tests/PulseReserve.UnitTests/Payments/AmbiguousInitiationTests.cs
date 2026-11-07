using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Persistence;
using PulseReserve.Modules.Payments.Providers.Paymob;
using PulseReserve.Modules.Payments.Providers.Stripe;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// A provider that cannot deduplicate a repeated initiation (Paymob: no idempotency key) is called at most once per attempt.
/// After an ambiguous answer this system does not retry blindly and does not claim a repeat would have been harmless: the attempt
/// is closed with its own failure code and the customer starts again. Stripe-style providers keep their idempotent resume.
/// </summary>
public sealed class AmbiguousInitiationTests
{
    private const string Key = "start-1";

    private static (PaymentService Service, InMemoryOrderStore Store, ScriptedProvider Provider) Build(bool idempotent, Func<PaymentInitiation, ProviderSession>? onInitiate = null)
    {
        var store = new InMemoryOrderStore("USD", 91m);
        var provider = new ScriptedProvider("Paymob", false, "USD") { Idempotent = idempotent };
        if (onInitiate is not null)
            provider.OnInitiate = onInitiate;
        return (new PaymentService(store, new PaymentProviderRegistry([provider]), new ConfigurableGateway()), store, provider);
    }

    [Fact]
    public void ThePaymobAdapter_DeclaresThatItCannotDeduplicateInitiation_AndStripeDeclaresThatItCan()
    {
        var paymob = new PaymobPaymentProvider(TestProviders.PaymobOptionsFor("USD"), new HttpClient(new StubHandler()));
        var stripe = new StripePaymentProvider(TestProviders.StripeOptionsFor("USD"), new HttpClient(new StubHandler()), new FixedClock(TestProviders.Now));

        paymob.IdempotentInitiation.Should().BeFalse();
        ((IPaymentProvider)stripe).IdempotentInitiation.Should().BeTrue();
    }

    [Fact]
    public async Task AnAmbiguousAnswer_ForAProviderWithoutIdempotency_ClosesTheAttempt_AndSaysNothingWasCharged()
    {
        var (service, store, provider) = Build(idempotent: false, _ => throw new TimeoutException("answer lost"));

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");

        result.Error.Should().Be(PaymentsErrors.InitiationUncertain);
        store.Calls.Should().Contain("CancelUncertain").And.NotContain("CancelUnreferenced");
        store.Attempts.Should().ContainSingle().Which.Status.Should().Be(PaymentAttemptStatus.Cancelled);
        provider.InitiateCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnHttpTimeout_IsAmbiguousLikeAnyOtherLostAnswer_NotAnUnhandledCancellation(bool idempotent)
    {
        // HttpClient reports an elapsed timeout as TaskCanceledException although nobody cancelled anything. It used to escape as a 500
        // (found by the first PostgreSQL run); it is "the provider did not answer": the provider may have acted.
        var (service, store, provider) = Build(idempotent, _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");

        result.Error.Should().Be(idempotent ? PaymentsErrors.ProviderTemporarilyUnavailable : PaymentsErrors.InitiationUncertain);
        store.Attempts.Should().ContainSingle().Which.Status.Should().Be(idempotent ? PaymentAttemptStatus.Initiated : PaymentAttemptStatus.Cancelled);
        provider.InitiateCalls.Should().Be(1);
    }

    [Fact]
    public async Task ACancellationRequestedByTheCaller_StillStopsTheRequest_AndIsNotTreatedAsAnAmbiguousAnswer()
    {
        using var cts = new CancellationTokenSource();
        var (service, store, _) = Build(idempotent: true, _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        store.Calls.Should().NotContain("CancelUncertain").And.NotContain("CancelUnreferenced");
    }

    [Fact]
    public async Task ReplayingTheSameKey_AfterTheAmbiguousAnswer_DoesNotCallTheProviderAgain()
    {
        var (service, store, provider) = Build(idempotent: false, _ => throw new TimeoutException("answer lost"));
        await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");

        var replay = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");

        replay.Error.Should().Be(PaymentsErrors.InitiationUncertain, "the closed attempt is not resurrected");
        provider.InitiateCalls.Should().Be(1, "a repeat could create a second payment session at a provider that cannot deduplicate");
        store.Attempts.Should().ContainSingle();
    }

    [Fact]
    public async Task ANewKey_StartsAFreshAttempt_WithItsOwnAttemptId_SoAnyPaymentIsCorrelatedToTheRightAttempt()
    {
        var calls = 0;
        var (service, store, provider) = Build(idempotent: false, i => ++calls == 1 ? throw new TimeoutException("answer lost") : new ProviderSession("pm_second", "https://pay.example.test/second"));
        await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");

        var again = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, "start-2", "Paymob");

        again.IsSuccess.Should().BeTrue();
        provider.InitiateCalls.Should().Be(2);
        store.Attempts.Should().HaveCount(2);
        store.Attempts.Select(a => a.Id).Distinct().Should().HaveCount(2, "each provider session belongs to its own attempt");
        store.Attempts.Single(a => a.Status == PaymentAttemptStatus.Initiated).Reference.Should().Be("pm_second");
    }

    [Fact]
    public async Task ARepeatWhileTheFirstCallIsStillRunning_IsReportedInProgress_AndNeverReachesTheProvider()
    {
        var (service, store, provider) = Build(idempotent: false);
        // Simulate the first request having created the attempt but not yet stored the provider's answer.
        var begin = await store.BeginPaymentAsync(store.Order.OrderId, store.CustomerId, Key, "Paymob", false);
        begin.Outcome.Should().Be(BeginPaymentOutcome.Started);

        var replay = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");

        replay.Error.Should().Be(PaymentsErrors.AttemptInProgress);
        provider.InitiateCalls.Should().Be(0);
    }

    [Fact]
    public async Task AProviderThatDeduplicates_KeepsItsIdempotentResume()
    {
        var (service, store, provider) = Build(idempotent: true, _ => throw new TimeoutException("answer lost"));

        var first = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");
        provider.OnInitiate = _ => new ProviderSession("cs_resumed", "https://pay.example.test/resumed");
        var retry = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");

        first.Error.Should().Be(PaymentsErrors.ProviderTemporarilyUnavailable);
        retry.IsSuccess.Should().BeTrue();
        retry.Value.Attempt.ProviderReference.Should().Be("cs_resumed");
        store.Attempts.Should().ContainSingle();
    }

    [Fact]
    public async Task ADefinitiveRefusal_IsUnchanged_ForEveryProvider()
    {
        var (service, store, _) = Build(idempotent: false, _ => throw new ProviderRejectedException("currency_not_enabled"));

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");

        result.Error.Should().Be(PaymentsErrors.ProviderRejected);
        store.Calls.Should().Contain("CancelUnreferenced").And.NotContain("CancelUncertain");
    }
}

using FluentAssertions;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

public sealed class PaymentProviderRegistryTests
{
    private static ScriptedProvider Stripe() => new("Stripe", false, "USD", "EUR");
    private static ScriptedProvider Paymob() => new("Paymob", false, "EGP");

    [Fact]
    public void ASingleAvailableProvider_IsTheDefault_WhenNoneIsNamed()
    {
        var registry = new PaymentProviderRegistry([Stripe()]);

        registry.Select(null, "USD").Value.Name.Should().Be("Stripe");
    }

    [Fact]
    public void SeveralAvailableProviders_RequireTheCustomerToChoose_NoSilentDefault()
    {
        var registry = new PaymentProviderRegistry([Stripe(), Paymob()]);

        registry.Select(null, "USD").Error.Should().Be(PaymentsErrors.ProviderRequired);
        registry.Select("  ", "USD").Error.Should().Be(PaymentsErrors.ProviderRequired);
    }

    [Fact]
    public void ANamedProvider_MustBeEnabledAndAbleToChargeTheOrderCurrency()
    {
        var disabled = Paymob();
        disabled.IsAvailable = false;
        var registry = new PaymentProviderRegistry([Stripe(), disabled]);

        registry.Select("stripe", "USD").Value.Name.Should().Be("Stripe");
        registry.Select("Paymob", "EGP").Error.Should().Be(PaymentsErrors.ProviderNotEnabled, "a configured-but-unavailable provider is not selectable");
        registry.Select("Adyen", "USD").Error.Should().Be(PaymentsErrors.ProviderNotEnabled, "an unknown name is not selectable");
        registry.Select("Stripe", "EGP").Error.Should().Be(PaymentsErrors.CurrencyNotSupported);
    }

    [Fact]
    public void NoAvailableProvider_IsUnavailable_AndTheSimulatorCountsOnlyWhenRegistered()
    {
        new PaymentProviderRegistry([new UnavailablePaymentProvider()]).Select(null, "USD").Error.Should().Be(PaymentsErrors.ProviderUnavailable);
        new PaymentProviderRegistry([new SimulatedPaymentProvider()]).Select(null, "USD").Value.IsSimulated.Should().BeTrue();
    }

    [Fact]
    public void ExistingAttemptsKeepResolvingTheirProvider_EvenWhenItIsNoLongerAvailable()
    {
        var gone = Paymob();
        gone.IsAvailable = false;
        var registry = new PaymentProviderRegistry([Stripe(), gone]);

        registry.Available.Select(p => p.Name).Should().Equal("Stripe");
        registry.Find("Paymob").Should().BeSameAs(gone, "refunds and reconciliation must reach the provider that took the money");
        registry.Find("nope").Should().BeNull();
    }

    [Fact]
    public void ALaterRegistrationWithTheSameName_Replaces_TheEarlierOne()
    {
        var first = Stripe();
        var second = Stripe();

        new PaymentProviderRegistry([first, second]).Find("Stripe").Should().BeSameAs(second);
    }
}

public sealed class MultiProviderPaymentServiceTests
{
    private const string Key = "pay-key-1";

    private static (PaymentService Service, InMemoryOrderStore Store, ConfigurableGateway Gateway) Build(string currency, params ScriptedProvider[] providers)
    {
        var store = new InMemoryOrderStore(currency);
        var gateway = new ConfigurableGateway();
        return (new PaymentService(store, new PaymentProviderRegistry(providers), gateway), store, gateway);
    }

    [Fact]
    public async Task TheChosenProvider_IsFixedOnTheAttempt_AndItsSessionAndCheckoutUrlArePersisted()
    {
        var stripe = new ScriptedProvider("Stripe", false, "USD");
        var paymob = new ScriptedProvider("Paymob", false, "EGP");
        var (service, store, _) = Build("USD", stripe, paymob);

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe");

        result.IsSuccess.Should().BeTrue();
        result.Value.Attempt.Provider.Should().Be("Stripe");
        result.Value.Attempt.ProviderReference.Should().StartWith("stripe_ref_");
        result.Value.Attempt.CheckoutUrl.Should().StartWith("https://pay.example.test/Stripe/");
        store.Attempts.Should().ContainSingle().Which.Provider.Should().Be("Stripe");
        (stripe.InitiateCalls, paymob.InitiateCalls).Should().Be((1, 0));
    }

    [Fact]
    public async Task AmbiguousProviderChoice_IsRefused_BeforeAnyAttemptExists()
    {
        var (service, store, _) = Build("USD", new ScriptedProvider("Stripe", false, "USD"), new ScriptedProvider("Paymob", false, "USD"));

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key);

        result.Error.Should().Be(PaymentsErrors.ProviderRequired);
        store.Attempts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Adyen")]
    [InlineData("Simulator")]
    [InlineData("https://evil.example/pay")]
    public async Task ACustomerCannotNameAProviderThatIsNotEnabled(string requested)
    {
        var stripe = new ScriptedProvider("Stripe", false, "USD");
        var (service, store, _) = Build("USD", stripe);

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, requested);

        result.Error.Should().Be(PaymentsErrors.ProviderNotEnabled);
        store.Attempts.Should().BeEmpty();
        stripe.InitiateCalls.Should().Be(0);
    }

    [Fact]
    public async Task AProviderThatCannotChargeTheOrderCurrency_IsRefused()
    {
        var (service, store, _) = Build("GBP", new ScriptedProvider("Stripe", false, "USD"));

        (await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe")).Error.Should().Be(PaymentsErrors.CurrencyNotSupported);
        (await service.InitiateAsync(store.CustomerId, store.Order.OrderId, "k2")).Error.Should().Be(PaymentsErrors.CurrencyNotSupported);
        store.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task APaymentThatNamesAnotherCustomersOrder_LooksLikeAMissingOrder()
    {
        var (service, store, _) = Build("USD", new ScriptedProvider("Stripe", false, "USD"));

        var result = await service.InitiateAsync(Guid.NewGuid(), store.Order.OrderId, Key, "Stripe");

        result.Error.Should().Be(PaymentsErrors.OrderNotFound);
    }

    [Fact]
    public async Task AReplay_ReturnsTheSameAttemptAndUrl_WithoutCallingTheProviderAgain()
    {
        var stripe = new ScriptedProvider("Stripe", false, "USD");
        var (service, store, _) = Build("USD", stripe, new ScriptedProvider("Paymob", false, "USD"));
        var first = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe");

        var replay = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe");
        var replayWithoutName = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key);

        replay.Value.IsReplay.Should().BeTrue();
        replay.Value.Attempt.CheckoutUrl.Should().Be(first.Value.Attempt.CheckoutUrl);
        replayWithoutName.Value.Attempt.AttemptId.Should().Be(first.Value.Attempt.AttemptId, "a replay needs no provider name: the attempt already has one");
        stripe.InitiateCalls.Should().Be(1);
    }

    [Fact]
    public async Task AReplayNamingADifferentProvider_IsAConflict_NeverASilentSwitch()
    {
        var stripe = new ScriptedProvider("Stripe", false, "USD");
        var paymob = new ScriptedProvider("Paymob", false, "USD");
        var (service, store, _) = Build("USD", stripe, paymob);
        await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe");

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Paymob");

        result.Error.Should().Be(PaymentsErrors.ProviderMismatch);
        paymob.InitiateCalls.Should().Be(0);
        store.Attempts.Should().ContainSingle().Which.Provider.Should().Be("Stripe");
    }

    [Fact]
    public async Task WhileAnAttemptIsInFlight_AnotherProviderCannotBeStartedForTheSameOrder()
    {
        var stripe = new ScriptedProvider("Stripe", false, "USD");
        var paymob = new ScriptedProvider("Paymob", false, "USD");
        var (service, store, _) = Build("USD", stripe, paymob);
        await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe");

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, "another-key", "Paymob");

        result.Error.Should().Be(PaymentsErrors.AttemptInProgress, "one charge in flight per order, whatever the provider");
        paymob.InitiateCalls.Should().Be(0);
    }

    [Fact]
    public async Task AnAmbiguousProviderFailure_LeavesTheAttemptInPlace_AndNeverTriesAnotherProvider()
    {
        var stripe = new ScriptedProvider("Stripe", false, "USD") { OnInitiate = _ => throw new TimeoutException("answer lost") };
        var paymob = new ScriptedProvider("Paymob", false, "USD");
        var (service, store, _) = Build("USD", stripe, paymob);

        var result = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe");

        result.Error.Should().Be(PaymentsErrors.ProviderTemporarilyUnavailable);
        paymob.InitiateCalls.Should().Be(0, "no automatic failover after an uncertain result");
        store.Calls.Should().NotContain("CancelUnreferenced", "the provider may have created the payment");
        var attempt = store.Attempts.Should().ContainSingle().Subject;
        (attempt.Status, attempt.Provider, attempt.Reference).Should().Be((PaymentAttemptStatus.Initiated, "Stripe", null));

        // The idempotent retry resumes the SAME attempt on the SAME provider.
        stripe.OnInitiate = i => new ProviderSession("cs_resumed", "https://pay.example.test/resumed");
        var retry = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe");
        retry.Value.Attempt.ProviderReference.Should().Be("cs_resumed");
        retry.Value.Attempt.AttemptId.Should().Be(attempt.Id);
        store.Attempts.Should().ContainSingle();
    }

    [Fact]
    public async Task ADefinitiveRefusal_CancelsTheAttempt_SoTheCustomerMayChooseAnotherProvider()
    {
        var stripe = new ScriptedProvider("Stripe", false, "USD") { OnInitiate = _ => throw new ProviderRejectedException("invalid_credentials") };
        var paymob = new ScriptedProvider("Paymob", false, "USD");
        var (service, store, _) = Build("USD", stripe, paymob);

        var refused = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe");
        var second = await service.InitiateAsync(store.CustomerId, store.Order.OrderId, "second-key", "Paymob");

        refused.Error.Should().Be(PaymentsErrors.ProviderRejected);
        store.Attempts[0].Status.Should().Be(PaymentAttemptStatus.Cancelled);
        second.IsSuccess.Should().BeTrue("the refusal was definitive: nothing exists at the first provider");
        second.Value.Attempt.Provider.Should().Be("Paymob");
    }

    [Fact]
    public async Task AReplayWhoseProviderIsNoLongerAvailable_IsUnavailable_NotSwitched()
    {
        var stripe = new ScriptedProvider("Stripe", false, "USD");
        var paymob = new ScriptedProvider("Paymob", false, "USD");
        var store = new InMemoryOrderStore("USD");
        var gateway = new ConfigurableGateway();
        await new PaymentService(store, new PaymentProviderRegistry([stripe, paymob]), gateway)
            .InitiateAsync(store.CustomerId, store.Order.OrderId, Key, "Stripe");
        stripe.IsAvailable = false;

        var result = await new PaymentService(store, new PaymentProviderRegistry([stripe, paymob]), gateway)
            .InitiateAsync(store.CustomerId, store.Order.OrderId, Key);

        result.Error.Should().Be(PaymentsErrors.ProviderUnavailable);
        paymob.InitiateCalls.Should().Be(0);
    }

    [Fact]
    public async Task Options_ListOnlyEnabledProvidersThatCanChargeTheOrderCurrency()
    {
        var disabled = new ScriptedProvider("Disabled", false, "USD") { IsAvailable = false };
        var (service, store, _) = Build("USD", new ScriptedProvider("Stripe", false, "USD"), new ScriptedProvider("Paymob", false, "EGP"), disabled);

        var options = await service.OptionsAsync(store.CustomerId, store.Order.OrderId);

        options.Value.Currency.Should().Be("USD");
        options.Value.Providers.Select(p => p.Name).Should().Equal("Stripe");
        (await service.OptionsAsync(Guid.NewGuid(), store.Order.OrderId)).Error.Should().Be(PaymentsErrors.OrderNotFound);
    }

    [Fact]
    public async Task Options_AreEmptyWhenNothingCanChargeTheCurrency_SoTheUiCanSayWhy()
    {
        var (service, store, _) = Build("JPY", new ScriptedProvider("Stripe", false, "USD"));

        (await service.OptionsAsync(store.CustomerId, store.Order.OrderId)).Value.Providers.Should().BeEmpty();
    }
}

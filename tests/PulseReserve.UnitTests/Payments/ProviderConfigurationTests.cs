using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PulseReserve.AppHost.Composition;
using PulseReserve.AppHost.Configuration;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

public sealed class ProviderConfigurationTests
{
    private static StripeSettings GoodStripe(Func<StripeSettings, StripeSettings>? change = null)
    {
        var s = new StripeSettings(true, "Test", TestProviders.StripeSecret, TestProviders.StripeWebhookSecret, "usd, eur", "https://shop.example.test", null);
        return change?.Invoke(s) ?? s;
    }

    private static PaymobSettings GoodPaymob(Func<PaymobSettings, PaymobSettings>? change = null)
    {
        var s = new PaymobSettings(true, "Test", TestProviders.PaymobSecret, TestProviders.PaymobPublic, TestProviders.PaymobHmacSecret, "4097558, 4097559",
            "egp", "https://shop.example.test", "https://api.example.test", null, null);
        return change?.Invoke(s) ?? s;
    }

    [Fact]
    public void AValidStripeConfiguration_YieldsOptions_WithNormalizedCurrenciesAndTheDefaultApiUrl()
    {
        var config = ProviderConfigurationValidator.Stripe(GoodStripe(), production: false);

        config.IsValid.Should().BeTrue();
        config.Options!.AllowedCurrencies.Should().Equal("USD", "EUR");
        config.Options.LiveMode.Should().BeFalse();
        config.Options.ApiBaseUrl.Should().Be(new Uri("https://api.stripe.com/"));
    }

    [Fact]
    public void AValidPaymobConfiguration_YieldsOptions_WithTheWebhookUrlDerivedFromThePublicApiUrl()
    {
        var config = ProviderConfigurationValidator.Paymob(GoodPaymob(), production: false);

        config.IsValid.Should().BeTrue();
        config.Options!.IntegrationIds.Should().Equal(4097558L, 4097559L);
        config.Options.NotificationUrl.Should().Be(new Uri("https://api.example.test/api/webhooks/paymob"));
        config.Options.CheckoutBaseUrl.Should().Be(new Uri("https://eg.checkout.paymob.com/"));
    }

    public static TheoryData<string, Func<StripeSettings, StripeSettings>> BrokenStripe => new()
    {
        { "missing secret", s => s with { SecretKey = null } },
        { "missing webhook secret", s => s with { WebhookSecret = " " } },
        { "webhook secret of the wrong shape", s => s with { WebhookSecret = "not-a-signing-secret" } },
        { "no mode", s => s with { Mode = null } },
        { "unknown mode", s => s with { Mode = "Sandbox" } },
        { "live key in test mode", s => s with { SecretKey = "sk_live_not_a_real_key" } },
        { "no currencies", s => s with { AllowedCurrencies = "" } },
        { "malformed currency", s => s with { AllowedCurrencies = "usd, dollars" } },
        { "no return url", s => s with { ReturnBaseUrl = null } },
        { "relative return url", s => s with { ReturnBaseUrl = "/orders" } },
        { "plain http return url", s => s with { ReturnBaseUrl = "http://shop.example.test" } },
        { "return url with a query", s => s with { ReturnBaseUrl = "https://shop.example.test/?x=1" } },
    };

    [Theory]
    [MemberData(nameof(BrokenStripe))]
    public void AnIncompleteOrContradictoryStripeConfiguration_IsNotUsable_AndNamesNoSecretValues(string _, Func<StripeSettings, StripeSettings> change)
    {
        var settings = change(GoodStripe());

        var config = ProviderConfigurationValidator.Stripe(settings, production: false);

        config.IsValid.Should().BeFalse();
        config.Problems.Should().NotBeEmpty();
        AssertNoSecretsLeak(config.Problems, settings.SecretKey, settings.WebhookSecret);
    }

    public static TheoryData<string, Func<PaymobSettings, PaymobSettings>> BrokenPaymob => new()
    {
        { "missing secret key", s => s with { SecretKey = null } },
        { "missing public key", s => s with { PublicKey = "" } },
        { "missing hmac secret", s => s with { HmacSecret = null } },
        { "no integration ids", s => s with { IntegrationIds = "" } },
        { "bad integration id", s => s with { IntegrationIds = "abc" } },
        { "negative integration id", s => s with { IntegrationIds = "-5" } },
        { "no currencies", s => s with { AllowedCurrencies = null } },
        { "no public api url", s => s with { PublicApiBaseUrl = null } },
        { "plain http public api url", s => s with { PublicApiBaseUrl = "http://api.example.test" } },
        { "live key in test mode", s => s with { SecretKey = "egy_sk_live_not_a_real_key" } },
        { "test public key in live mode", s => s with { Mode = "Live", PublicKey = TestProviders.PaymobPublic, SecretKey = "egy_sk_live_x" } },
    };

    [Theory]
    [MemberData(nameof(BrokenPaymob))]
    public void AnIncompleteOrContradictoryPaymobConfiguration_IsNotUsable_AndNamesNoSecretValues(string _, Func<PaymobSettings, PaymobSettings> change)
    {
        var settings = change(GoodPaymob());

        var config = ProviderConfigurationValidator.Paymob(settings, production: false);

        config.IsValid.Should().BeFalse();
        AssertNoSecretsLeak(config.Problems, settings.SecretKey, settings.PublicKey, settings.HmacSecret);
    }

    [Fact]
    public void SandboxAndLive_AreKeptApart_ProductionRunsLiveOnly_EverythingElseTestOnly()
    {
        var liveStripe = GoodStripe(s => s with { Mode = "Live", SecretKey = "sk_live_not_a_real_key" });
        ProviderConfigurationValidator.Stripe(liveStripe, production: true).IsValid.Should().BeTrue();
        ProviderConfigurationValidator.Stripe(liveStripe, production: false).Problems.Should().ContainMatch("*Live*outside Production*");
        ProviderConfigurationValidator.Stripe(GoodStripe(), production: true).Problems.Should().ContainMatch("*Test*Production*");

        var livePaymob = GoodPaymob(s => s with { Mode = "Live", SecretKey = "egy_sk_live_x", PublicKey = "egy_pk_live_x" });
        ProviderConfigurationValidator.Paymob(livePaymob, production: true).IsValid.Should().BeTrue();
        ProviderConfigurationValidator.Paymob(livePaymob, production: false).IsValid.Should().BeFalse();
        ProviderConfigurationValidator.Paymob(GoodPaymob(), production: true).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ProviderApiEndpoints_CannotBeOverriddenInProduction_AndOutsideItOnlyToHttpsOrLocalhost()
    {
        var overridden = GoodStripe(s => s with { Mode = "Live", SecretKey = "sk_live_x", ApiBaseUrl = "https://mock.example.test" });
        ProviderConfigurationValidator.Stripe(overridden, production: true).Problems.Should().ContainMatch("*cannot be overridden in Production*");

        ProviderConfigurationValidator.Stripe(GoodStripe(s => s with { ApiBaseUrl = "http://localhost:9999" }), production: false).IsValid.Should().BeTrue();
        ProviderConfigurationValidator.Stripe(GoodStripe(s => s with { ApiBaseUrl = "http://stripe.example.test" }), production: false).IsValid.Should().BeFalse();
    }

    [Fact]
    public void PlainHttpReturnUrls_AreAllowedOnlyForLocalhostOutsideProduction()
    {
        var local = GoodStripe(s => s with { ReturnBaseUrl = "http://localhost:4200" });

        ProviderConfigurationValidator.Stripe(local, production: false).IsValid.Should().BeTrue();
        ProviderConfigurationValidator.Stripe(local with { Mode = "Live", SecretKey = "sk_live_x" }, production: true).IsValid.Should().BeFalse();
    }

    private static void AssertNoSecretsLeak(IReadOnlyList<string> problems, params string?[] secrets)
    {
        foreach (var secret in secrets.Where(s => !string.IsNullOrWhiteSpace(s)))
            problems.Should().NotContain(p => p.Contains(secret!), "problem messages name settings and rules, never values");
    }

    // ───────────── host composition ─────────────

    private static (IServiceCollection Services, IReadOnlyList<PaymentProviderStatus> Status) Compose(Dictionary<string, string?> settings, string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        var status = services.AddRealPaymentProviders(configuration, new FakeEnvironment(environment));
        return (services, status);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Dictionary<string, string?> StripeSettingsFor(string mode, string key) => new()
    {
        ["Payments:Stripe:Enabled"] = "true", ["Payments:Stripe:Mode"] = mode, ["Payments:Stripe:SecretKey"] = key,
        ["Payments:Stripe:WebhookSecret"] = TestProviders.StripeWebhookSecret, ["Payments:Stripe:AllowedCurrencies"] = "usd",
        ["Payments:Stripe:ReturnBaseUrl"] = "https://shop.example.test",
    };

    [Fact]
    public void ByDefault_NoRealProviderIsRegistered_AndNothingIsEnabled()
    {
        var (services, status) = Compose([], "Production");

        status.Should().OnlyContain(s => !s.Enabled && !s.Active);
        services.BuildServiceProvider().GetServices<IPaymentProvider>().Should().BeEmpty();
    }

    [Fact]
    public void AValidEnabledProvider_IsRegistered_AsARealNonSimulatedProvider()
    {
        var (services, status) = Compose(StripeSettingsFor("Test", TestProviders.StripeSecret), "Development");

        status.Single(s => s.Provider == "Stripe").Active.Should().BeTrue();
        var provider = services.BuildServiceProvider().GetServices<IPaymentProvider>().Should().ContainSingle().Subject;
        provider.Name.Should().Be("Stripe");
        provider.IsSimulated.Should().BeFalse();
    }

    [Fact]
    public void AnInvalidEnabledProvider_IsSimplyUnavailable_WithoutBreakingStartup()
    {
        var settings = StripeSettingsFor("Test", TestProviders.StripeSecret);
        settings.Remove("Payments:Stripe:WebhookSecret");

        var (services, status) = Compose(settings, "Development");

        var stripe = status.Single(s => s.Provider == "Stripe");
        (stripe.Enabled, stripe.Active).Should().Be((true, false));
        stripe.Problems.Should().NotBeEmpty();
        stripe.Problems.Should().NotContain(p => p.Contains(TestProviders.StripeSecret));
        services.BuildServiceProvider().GetServices<IPaymentProvider>().Should().BeEmpty();
    }

    [Fact]
    public void TestCredentials_InProduction_MakeTheProviderUnavailable()
    {
        var (services, status) = Compose(StripeSettingsFor("Test", TestProviders.StripeSecret), "Production");

        status.Single(s => s.Provider == "Stripe").Active.Should().BeFalse();
        services.BuildServiceProvider().GetServices<IPaymentProvider>().Should().BeEmpty();
    }

    // ───────────── the simulator stays development/test only ─────────────

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void TheSimulator_CannotBeEnabledOutsideDevelopmentAndTesting(string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [PaymentsOptions.SimulatorKey] = "true" }).Build();

        var act = () => PaymentsOptions.Resolve(configuration, new FakeEnvironment(environment));

        act.Should().Throw<InvalidOperationException>().WithMessage("*must never run in production*");
    }

    [Theory]
    [InlineData("production")]
    [InlineData("PRODUCTION")]
    [InlineData("Prod")]
    [InlineData("Prodution")]
    [InlineData("Live")]
    [InlineData("")]
    [InlineData("Test")]
    [InlineData("Development ")]
    public void TheSimulator_FailsClosed_ForEveryEnvironmentNameThatIsNotExactlyDevelopmentOrTesting(string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [PaymentsOptions.SimulatorKey] = "true" }).Build();

        var act = () => PaymentsOptions.Resolve(configuration, new FakeEnvironment(environment));

        act.Should().Throw<InvalidOperationException>().WithMessage("*must never run in production*");
    }

    [Theory]
    [InlineData("development")]
    [InlineData("TESTING")]
    public void TheEnvironmentNameComparison_IsCaseInsensitive_ForTheOnlyTwoPermissiveNames(string environment)
    {
        var on = Simulator(("Payments:Simulator:Enabled", "true"), (PaymentsOptions.SimulatorTestingOptInKey, "true"));

        PaymentsOptions.Resolve(on, new FakeEnvironment(environment)).SimulatorEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "false")]
    [InlineData("Production", "")]
    [InlineData("Development", null)]
    [InlineData("Testing", null)]
    public void TheSimulator_IsOff_WhenTheSettingIsMissingBlankOrFalse_InEveryEnvironment(string environment, string? value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [PaymentsOptions.SimulatorKey] = value }).Build();

        PaymentsOptions.Resolve(configuration, new FakeEnvironment(environment)).SimulatorEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("ture")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    public void ATypoInTheSimulatorSetting_FailsStartup_InsteadOfBeingReadAsOffOrOn(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [PaymentsOptions.SimulatorKey] = value }).Build();

        var act = () => PaymentsOptions.Resolve(configuration, new FakeEnvironment("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*must be 'true' or 'false'*");
    }

    [Theory]
    [InlineData("Development", "Payments:Stripe:Enabled")]
    [InlineData("Testing", "Payments:Paymob:Enabled")]
    public void TheSimulator_CannotRunNextToARealProvider_EvenWhereTheSimulatorIsPermitted(string environment, string providerKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PaymentsOptions.SimulatorKey] = "true", [PaymentsOptions.SimulatorTestingOptInKey] = "true", [providerKey] = "true"
        }).Build();

        var act = () => PaymentsOptions.Resolve(configuration, new FakeEnvironment(environment));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{providerKey}*Choose one*");
    }

    [Fact]
    public void ARealProviderAlone_NeverEnablesTheSimulator()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Payments:Stripe:Enabled"] = "true" }).Build();

        PaymentsOptions.Resolve(configuration, new FakeEnvironment("Production")).SimulatorEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void TheSimulator_IsAllowedOnlyInDevelopmentAndTesting_AndOffByDefault(string environment)
    {
        var on = Simulator(("Payments:Simulator:Enabled", "true"), (PaymentsOptions.SimulatorTestingOptInKey, "true"));
        var off = new ConfigurationBuilder().Build();

        PaymentsOptions.Resolve(on, new FakeEnvironment(environment)).SimulatorEnabled.Should().BeTrue();
        PaymentsOptions.Resolve(off, new FakeEnvironment(environment)).SimulatorEnabled.Should().BeFalse();
    }

    // ───────────── explicit opt-in: Testing is not permissive by name alone ─────────────

    private static IConfiguration Simulator(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value)).Build();

    [Fact]
    public void Development_NeedsOnlyTheSimulatorFlag_SoTheEverydayWorkflowIsUnchanged()
    {
        PaymentsOptions.Resolve(Simulator((PaymentsOptions.SimulatorKey, "true")), new FakeEnvironment("Development")).SimulatorEnabled.Should().BeTrue();
        PaymentsOptions.Resolve(Simulator((PaymentsOptions.SimulatorKey, "true"), (PaymentsOptions.SimulatorTestingOptInKey, "true")), new FakeEnvironment("Development"))
            .SimulatorEnabled.Should().BeTrue("the Testing opt-in is irrelevant in Development, so one shared settings file works");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    public void ADeployedHostNamedTesting_CannotStartTheSimulator_WithoutTheExplicitOptIn(string? optIn)
    {
        var configuration = Simulator((PaymentsOptions.SimulatorKey, "true"), (PaymentsOptions.SimulatorTestingOptInKey, optIn));

        var act = () => PaymentsOptions.Resolve(configuration, new FakeEnvironment("Testing"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'Testing'*{PaymentsOptions.SimulatorTestingOptInKey}=true*");
    }

    [Fact]
    public void TheTestingOptIn_IsWhatAutomatedTestsSet_AndItAloneEnablesNothing()
    {
        PaymentsOptions.Resolve(Simulator((PaymentsOptions.SimulatorKey, "true"), (PaymentsOptions.SimulatorTestingOptInKey, "true")), new FakeEnvironment("Testing"))
            .SimulatorEnabled.Should().BeTrue();

        foreach (var environment in new[] { "Development", "Testing", "Production", "Staging", "" })
        {
            PaymentsOptions.Resolve(Simulator((PaymentsOptions.SimulatorTestingOptInKey, "true")), new FakeEnvironment(environment))
                .SimulatorEnabled.Should().BeFalse($"the opt-in does not switch the simulator on ({environment})");
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("production")]
    [InlineData("Prod")]
    [InlineData("Test")]
    [InlineData("")]
    public void TheTestingOptIn_NeverRescuesAnotherEnvironment(string environment)
    {
        var configuration = Simulator((PaymentsOptions.SimulatorKey, "true"), (PaymentsOptions.SimulatorTestingOptInKey, "true"));

        var act = () => PaymentsOptions.Resolve(configuration, new FakeEnvironment(environment));

        act.Should().Throw<InvalidOperationException>().WithMessage("*must never run in production*");
    }

    [Theory]
    [InlineData("ture")]
    [InlineData("yes")]
    [InlineData("1")]
    public void ATypoInTheOptIn_FailsStartup_InEveryEnvironment_EvenWhenTheSimulatorIsOff(string value)
    {
        foreach (var environment in new[] { "Production", "Development", "Testing" })
        {
            var act = () => PaymentsOptions.Resolve(Simulator((PaymentsOptions.SimulatorTestingOptInKey, value)), new FakeEnvironment(environment));

            act.Should().Throw<InvalidOperationException>().WithMessage("*must be 'true' or 'false'*");
        }
    }

    [Fact]
    public void AnIncompleteRealProvider_IsNeverReplacedBySilentlyStartingTheSimulator()
    {
        // The provider is enabled but unusable (no keys). It is simply unavailable - and the simulator does not take its place.
        var settings = new Dictionary<string, string?> { ["Payments:Stripe:Enabled"] = "true" };
        var (services, status) = Compose(settings, "Development");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        status.Single(s => s.Provider == "Stripe").Active.Should().BeFalse();
        services.BuildServiceProvider().GetServices<IPaymentProvider>().Should().BeEmpty();
        PaymentsOptions.Resolve(configuration, new FakeEnvironment("Development")).SimulatorEnabled.Should().BeFalse("a broken real provider must not enable a fake one");

        // Asking for both is refused outright, however broken the real provider is.
        settings[PaymentsOptions.SimulatorKey] = "true";
        var both = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var act = () => PaymentsOptions.Resolve(both, new FakeEnvironment("Development"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Choose one*");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void WithNothingConfigured_NoPaymentMethodExists_AndNothingFallsBackToTheSimulator(string environment)
    {
        var options = PaymentsOptions.Resolve(new ConfigurationBuilder().Build(), new FakeEnvironment(environment));
        var (services, status) = Compose([], environment);

        options.SimulatorEnabled.Should().BeFalse();
        status.Should().OnlyContain(s => !s.Active);
        services.BuildServiceProvider().GetServices<IPaymentProvider>().Should().BeEmpty();
    }
}

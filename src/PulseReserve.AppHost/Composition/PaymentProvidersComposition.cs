using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers;
using PulseReserve.Modules.Payments.Providers.Paymob;
using PulseReserve.Modules.Payments.Providers.Stripe;

namespace PulseReserve.AppHost.Composition;

/// <summary>What the host decided about one real payment provider at startup. Contains no secret values.</summary>
public sealed record PaymentProviderStatus(string Provider, bool Enabled, bool Active, IReadOnlyList<string> Problems);

/// <summary>
/// Registers the real payment providers (Stripe, Paymob) from configuration. Both are OFF unless
/// <c>Payments:{Provider}:Enabled</c> is true. A provider that is enabled but misconfigured is not created: payments
/// through it are simply unavailable and the reasons (setting names and rules, never values) are logged at startup,
/// while the rest of the application starts normally. Configuration lives in <c>Payments:Stripe:*</c> and
/// <c>Payments:Paymob:*</c> (environment variables use <c>__</c>, for example <c>Payments__Stripe__SecretKey</c>).
/// </summary>
public static class PaymentProvidersComposition
{
    public static IReadOnlyList<PaymentProviderStatus> AddRealPaymentProviders(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var production = environment.IsProduction();
        var statuses = new List<PaymentProviderStatus>();

        if (configuration.GetValue<bool>("Payments:Stripe:Enabled"))
        {
            var s = configuration.GetSection("Payments:Stripe");
            var config = ProviderConfigurationValidator.Stripe(
                new StripeSettings(true, s["Mode"], s["SecretKey"], s["WebhookSecret"], s["AllowedCurrencies"], s["ReturnBaseUrl"], s["ApiBaseUrl"]), production);
            if (config.Options is { } options)
            {
                services.AddHttpClient(StripePaymentProvider.ProviderName, client =>
                {
                    client.BaseAddress = WithTrailingSlash(options.ApiBaseUrl);
                    client.Timeout = TimeSpan.FromSeconds(20);
                });
                services.AddSingleton<IPaymentProvider>(sp => new StripePaymentProvider(
                    options, sp.GetRequiredService<IHttpClientFactory>().CreateClient(StripePaymentProvider.ProviderName), TimeProvider.System));
            }

            statuses.Add(new PaymentProviderStatus(StripePaymentProvider.ProviderName, true, config.IsValid, config.Problems));
        }
        else
        {
            statuses.Add(new PaymentProviderStatus(StripePaymentProvider.ProviderName, false, false, []));
        }

        if (configuration.GetValue<bool>("Payments:Paymob:Enabled"))
        {
            var s = configuration.GetSection("Payments:Paymob");
            var config = ProviderConfigurationValidator.Paymob(
                new PaymobSettings(true, s["Mode"], s["SecretKey"], s["PublicKey"], s["HmacSecret"], s["IntegrationIds"], s["AllowedCurrencies"],
                    s["ReturnBaseUrl"], s["PublicApiBaseUrl"], s["ApiBaseUrl"], s["CheckoutBaseUrl"]), production);
            if (config.Options is { } options)
            {
                services.AddHttpClient(PaymobPaymentProvider.ProviderName, client =>
                {
                    client.BaseAddress = WithTrailingSlash(options.ApiBaseUrl);
                    client.Timeout = TimeSpan.FromSeconds(20);
                });
                services.AddSingleton<IPaymentProvider>(sp => new PaymobPaymentProvider(
                    options, sp.GetRequiredService<IHttpClientFactory>().CreateClient(PaymobPaymentProvider.ProviderName)));
            }

            statuses.Add(new PaymentProviderStatus(PaymobPaymentProvider.ProviderName, true, config.IsValid, config.Problems));
        }
        else
        {
            statuses.Add(new PaymentProviderStatus(PaymobPaymentProvider.ProviderName, false, false, []));
        }

        return statuses;
    }

    public static void LogStatuses(this ILogger logger, IEnumerable<PaymentProviderStatus> statuses)
    {
        foreach (var status in statuses)
        {
            if (!status.Enabled)
                logger.LogInformation("Payment provider {Provider}: disabled", status.Provider);
            else if (status.Active)
                logger.LogInformation("Payment provider {Provider}: enabled and configured", status.Provider);
            else
                logger.LogError("Payment provider {Provider} is enabled but NOT available because its configuration is invalid: {Problems}",
                    status.Provider, string.Join(" ", status.Problems));
        }
    }

    private static Uri WithTrailingSlash(Uri uri) => new(uri.ToString().TrimEnd('/') + "/");
}

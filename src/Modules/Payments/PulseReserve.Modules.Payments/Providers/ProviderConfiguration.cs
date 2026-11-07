using System.Globalization;
using PulseReserve.Modules.Payments.Providers.Paymob;
using PulseReserve.Modules.Payments.Providers.Stripe;

namespace PulseReserve.Modules.Payments.Providers;

/// <summary>Raw (unvalidated) Stripe settings as read from configuration. Never logged.</summary>
public sealed record StripeSettings(
    bool Enabled, string? Mode, string? SecretKey, string? WebhookSecret, string? AllowedCurrencies, string? ReturnBaseUrl, string? ApiBaseUrl);

/// <summary>Raw (unvalidated) Paymob settings as read from configuration. Never logged.</summary>
public sealed record PaymobSettings(
    bool Enabled, string? Mode, string? SecretKey, string? PublicKey, string? HmacSecret, string? IntegrationIds,
    string? AllowedCurrencies, string? ReturnBaseUrl, string? PublicApiBaseUrl, string? ApiBaseUrl, string? CheckoutBaseUrl);

/// <summary>
/// The outcome of validating one provider's configuration: the usable options, or the list of problems. Problems name
/// the setting and the rule, NEVER the value, so they are safe to log.
/// </summary>
public sealed record ProviderConfig<T>(T? Options, IReadOnlyList<string> Problems) where T : class
{
    public bool IsValid => Options is not null;
}

/// <summary>
/// Validates provider configuration. A provider that is enabled but incomplete or contradictory is simply NOT created
/// (so payments through it are unavailable) while the rest of the application keeps working. Sandbox and live are
/// kept apart: a Production host accepts only <c>Live</c> mode, every other environment only <c>Test</c>.
/// </summary>
public static class ProviderConfigurationValidator
{
    public const string WebhookPath = "/api/webhooks/";

    public static ProviderConfig<StripeOptions> Stripe(StripeSettings s, bool production)
    {
        var problems = new List<string>();
        var live = Mode("Payments:Stripe:Mode", s.Mode, production, problems);

        if (string.IsNullOrWhiteSpace(s.SecretKey))
            problems.Add("Payments:Stripe:SecretKey is required.");
        else if (live is { } l && !(s.SecretKey.StartsWith(l ? "sk_live_" : "sk_test_", StringComparison.Ordinal) || s.SecretKey.StartsWith(l ? "rk_live_" : "rk_test_", StringComparison.Ordinal)))
            problems.Add($"Payments:Stripe:SecretKey must be a {(l ? "live" : "test")}-mode key (sk_{(l ? "live" : "test")}_… or rk_{(l ? "live" : "test")}_…) to match Mode.");

        if (string.IsNullOrWhiteSpace(s.WebhookSecret) || !s.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal))
            problems.Add("Payments:Stripe:WebhookSecret is required and must be a whsec_… signing secret.");

        var currencies = Currencies("Payments:Stripe:AllowedCurrencies", s.AllowedCurrencies, problems);
        var returnUrl = BaseUrl("Payments:Stripe:ReturnBaseUrl", s.ReturnBaseUrl, production, required: true, problems);
        var api = ApiUrl("Payments:Stripe:ApiBaseUrl", s.ApiBaseUrl, StripeOptions.DefaultApiBaseUrl, production, problems);

        return problems.Count > 0 || live is null || returnUrl is null || api is null
            ? new ProviderConfig<StripeOptions>(null, problems)
            : new ProviderConfig<StripeOptions>(new StripeOptions(s.SecretKey!.Trim(), s.WebhookSecret!.Trim(), live.Value, currencies, returnUrl, api), problems);
    }

    public static ProviderConfig<PaymobOptions> Paymob(PaymobSettings s, bool production)
    {
        var problems = new List<string>();
        var live = Mode("Payments:Paymob:Mode", s.Mode, production, problems);

        KeyMode("Payments:Paymob:SecretKey", s.SecretKey, live, problems);
        KeyMode("Payments:Paymob:PublicKey", s.PublicKey, live, problems);
        if (string.IsNullOrWhiteSpace(s.HmacSecret))
            problems.Add("Payments:Paymob:HmacSecret is required.");

        var integrations = new List<long>();
        foreach (var part in (s.IntegrationIds ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                integrations.Add(id);
            else
                problems.Add("Payments:Paymob:IntegrationIds must be a comma-separated list of positive integration ids.");
        }

        if (integrations.Count == 0 && !problems.Any(p => p.StartsWith("Payments:Paymob:IntegrationIds", StringComparison.Ordinal)))
            problems.Add("Payments:Paymob:IntegrationIds needs at least one integration id.");

        var currencies = Currencies("Payments:Paymob:AllowedCurrencies", s.AllowedCurrencies, problems);
        var returnUrl = BaseUrl("Payments:Paymob:ReturnBaseUrl", s.ReturnBaseUrl, production, required: true, problems);
        var publicApi = BaseUrl("Payments:Paymob:PublicApiBaseUrl", s.PublicApiBaseUrl, production, required: true, problems);
        var api = ApiUrl("Payments:Paymob:ApiBaseUrl", s.ApiBaseUrl, PaymobOptions.DefaultApiBaseUrl, production, problems);
        var checkout = ApiUrl("Payments:Paymob:CheckoutBaseUrl", s.CheckoutBaseUrl, PaymobOptions.DefaultCheckoutBaseUrl, production, problems);

        if (problems.Count > 0 || live is null || returnUrl is null || publicApi is null || api is null || checkout is null)
            return new ProviderConfig<PaymobOptions>(null, problems);

        var notification = new Uri($"{publicApi.ToString().TrimEnd('/')}{WebhookPath}paymob");
        return new ProviderConfig<PaymobOptions>(
            new PaymobOptions(s.SecretKey!.Trim(), s.PublicKey!.Trim(), s.HmacSecret!.Trim(), integrations, currencies, live.Value, api, checkout, returnUrl, notification),
            problems);
    }

    /// <summary>Production hosts must run Live, everything else Test; the setting is mandatory so the choice is never implicit.</summary>
    private static bool? Mode(string key, string? value, bool production, List<string> problems)
    {
        bool? live = value?.Trim().ToLowerInvariant() switch { "live" => true, "test" => false, _ => null };
        if (live is null)
        {
            problems.Add($"{key} must be 'Test' or 'Live'.");
            return null;
        }

        if (production && !live.Value)
            problems.Add($"{key} is 'Test' in a Production environment: sandbox credentials must never run in production.");
        if (!production && live.Value)
            problems.Add($"{key} is 'Live' outside Production: live credentials are accepted only in the Production environment.");
        return live;
    }

    private static void KeyMode(string key, string? value, bool? live, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{key} is required.");
            return;
        }

        // Paymob key names carry their mode (…_test_… / …_live_…): a mismatch with Mode is a contradiction, not a guess.
        if (live is { } l && value.Contains(l ? "_test_" : "_live_", StringComparison.OrdinalIgnoreCase))
            problems.Add($"{key} looks like a {(l ? "test" : "live")}-mode key but Mode is '{(l ? "Live" : "Test")}'.");
    }

    private static IReadOnlyList<string> Currencies(string key, string? value, List<string> problems)
    {
        var list = new List<string>();
        foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 3 && part.All(char.IsAsciiLetter))
                list.Add(part.ToUpperInvariant());
            else
                problems.Add($"{key} must be a comma-separated list of 3-letter currency codes.");
        }

        if (list.Count == 0 && !problems.Any(p => p.StartsWith(key, StringComparison.Ordinal)))
            problems.Add($"{key} must list at least one currency: a provider charges nothing until it is told which currencies it may.");
        return list;
    }

    private static Uri? BaseUrl(string key, string? value, bool production, bool required, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
                problems.Add($"{key} is required.");
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            problems.Add($"{key} must be an absolute URL without query or fragment.");
            return null;
        }

        var secure = uri.Scheme == Uri.UriSchemeHttps;
        var localHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && !production;
        if (!secure && !localHttp)
        {
            problems.Add($"{key} must use https (plain http is allowed only for localhost outside Production).");
            return null;
        }

        return uri;
    }

    /// <summary>Provider API endpoints are fixed in production; an override exists only for tests and local mocks.</summary>
    private static Uri? ApiUrl(string key, string? value, string defaultUrl, bool production, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Uri(defaultUrl);
        if (production)
        {
            problems.Add($"{key} cannot be overridden in Production.");
            return null;
        }

        return BaseUrl(key, value, production, required: false, problems);
    }
}

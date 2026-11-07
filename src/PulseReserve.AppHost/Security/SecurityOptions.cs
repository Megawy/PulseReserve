namespace PulseReserve.AppHost.Security;

/// <summary>
/// Validated security settings. Everything here either has a safe default or makes startup fail; nothing secret is
/// ever defaulted.
/// <list type="bullet">
/// <item><c>Identity:SessionLifetimeMinutes</c> – absolute session lifetime (default 480, allowed 5–10080).</item>
/// <item><c>Identity:RateLimit:PermitsPerMinute</c> – register/login attempts per client IP per minute (default 10).</item>
/// <item><c>Security:FinancialWritesPerMinute</c> - state-changing financial requests (create order, start or cancel a payment,
/// every administrator payment operation) per signed-in user per minute (default 60, at least 1). Reads and webhooks are not
/// limited by the application: a webhook is protected by its signature and size cap, and any per-address limit for providers
/// belongs to the reverse proxy.</item>
/// <item><c>Security:ForwardedHeaders:*</c> - REQUIRED outside Development/Testing: <c>Mode</c> = <c>Direct</c> or <c>Proxy</c>
/// (then <c>KnownProxies</c>/<c>KnownNetworks</c>). See <see cref="ForwardedHeadersSettings"/>.</item>
/// <item><c>Identity:AdminUserIds</c> - comma/semicolon separated user IDs (GUIDs) that hold the Admin role. This is the ONLY
/// way to become an administrator (see <see cref="AdminRegistry"/>). Empty means nobody is. An unparsable entry fails startup.</item>
/// <item><c>DataProtection:KeysPath</c> – directory that persists the keys protecting the session cookie.
/// REQUIRED outside Development and Testing: without persisted keys every restart would sign everyone out, and
/// multiple instances could not read each other's cookies.</item>
/// </list>
/// </summary>
public sealed record SecurityOptions(TimeSpan SessionLifetime, int AuthPermitsPerMinute, string? DataProtectionKeysPath, bool RelaxedTransport)
{
    public const string CookieName = "pulsereserve.auth";
    public const string SecureCookieName = "__Host-pulsereserve.auth";
    public const string CsrfCookieName = "XSRF-TOKEN";
    public const string CsrfHeaderName = "X-XSRF-TOKEN";

    /// <summary>Per signed-in user and minute; see <see cref="SecurityExtensions.AddFinancialWriteRateLimit"/>.</summary>
    public int FinancialWritesPerMinute { get; init; } = 60;

    /// <summary>How the host is exposed (direct or behind a listed reverse proxy); see <see cref="ForwardedHeadersSettings"/>.</summary>
    public ForwardedHeadersSettings ForwardedHeaders { get; init; } = ForwardedHeadersSettings.Direct;

    public string AuthCookieName => RelaxedTransport ? CookieName : SecureCookieName;

    public static SecurityOptions Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var relaxed = environment.IsDevelopment() || environment.IsEnvironment("Testing");

        var lifetimeMinutes = configuration.GetValue<int?>("Identity:SessionLifetimeMinutes") ?? 480;
        if (lifetimeMinutes is < 5 or > 10080)
            throw new InvalidOperationException("Identity:SessionLifetimeMinutes must be between 5 and 10080.");

        var permits = configuration.GetValue<int?>("Identity:RateLimit:PermitsPerMinute") ?? 10;
        if (permits < 1)
            throw new InvalidOperationException("Identity:RateLimit:PermitsPerMinute must be at least 1.");

        var financialWrites = configuration.GetValue<int?>("Security:FinancialWritesPerMinute") ?? 60;
        if (financialWrites < 1)
            throw new InvalidOperationException("Security:FinancialWritesPerMinute must be at least 1.");

        var keysPath = configuration["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(keysPath))
        {
            if (!relaxed)
            {
                throw new InvalidOperationException(
                    "DataProtection:KeysPath is required outside Development and Testing so session cookies survive restarts " +
                    "and are readable by every instance. Point it at a persistent, access-restricted directory.");
            }
            keysPath = null;
        }

        AdminRegistry.ParseOrThrow(configuration[AdminRegistry.ConfigKey]); // fail fast on a misconfigured list

        var forwarded = ForwardedHeadersSettings.Resolve(configuration, relaxed);

        return new SecurityOptions(TimeSpan.FromMinutes(lifetimeMinutes), permits, keysPath, relaxed) { FinancialWritesPerMinute = financialWrites, ForwardedHeaders = forwarded };
    }
}

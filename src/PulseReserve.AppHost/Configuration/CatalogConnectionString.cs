namespace PulseReserve.AppHost.Configuration;

/// <summary>
/// Resolves the Catalog PostgreSQL connection string.
/// Standard configuration (<c>ConnectionStrings:Catalog</c>, e.g. the <c>ConnectionStrings__Catalog</c> environment
/// variable) always wins. The legacy <c>PULSERESERVE_TEST_DATABASE</c> variable is honoured only as a fallback, so it
/// can never silently redirect a host (or a test host) that has an explicitly configured database.
/// </summary>
public static class CatalogConnectionString
{
    public const string ConnectionStringName = "Catalog";
    public const string LegacyEnvironmentVariable = "PULSERESERVE_TEST_DATABASE";

    public static string Resolve(IConfiguration configuration, Func<string, string?> getEnvironmentVariable)
    {
        var configured = configuration.GetConnectionString(ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var legacy = getEnvironmentVariable(LegacyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(legacy))
            return legacy;

        throw new InvalidOperationException(
            $"A Catalog connection string is required. Set ConnectionStrings:{ConnectionStringName} " +
            $"(for example via the ConnectionStrings__{ConnectionStringName} environment variable).");
    }
}

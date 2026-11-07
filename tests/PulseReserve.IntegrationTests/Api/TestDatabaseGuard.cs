using Npgsql;

namespace PulseReserve.IntegrationTests.Api;

/// <summary>
/// Verifies that a test host resolved exactly the disposable database it was given.
/// Compares host, port, database and username; never opens a connection, and never echoes passwords.
/// </summary>
public static class TestDatabaseGuard
{
    public static void EnsureTargetsExpectedDatabase(string? resolvedConnectionString, string expectedConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConnectionString);

        if (string.IsNullOrWhiteSpace(resolvedConnectionString))
            throw new InvalidOperationException("[Test database guard] The test host did not resolve any Catalog connection string.");

        var resolved = new NpgsqlConnectionStringBuilder(resolvedConnectionString);
        var expected = new NpgsqlConnectionStringBuilder(expectedConnectionString);

        var matches =
            string.Equals(resolved.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
            resolved.Port == expected.Port &&
            string.Equals(resolved.Database, expected.Database, StringComparison.Ordinal) &&
            string.Equals(resolved.Username, expected.Username, StringComparison.Ordinal);

        if (!matches)
        {
            throw new InvalidOperationException(
                $"[Test database guard] The test host resolved '{Describe(resolved)}' instead of the disposable test " +
                $"database '{Describe(expected)}'. No HTTP client was created.");
        }
    }

    private static string Describe(NpgsqlConnectionStringBuilder builder) =>
        $"{builder.Host}:{builder.Port}/{builder.Database}";
}

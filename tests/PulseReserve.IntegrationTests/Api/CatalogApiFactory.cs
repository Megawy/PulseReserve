using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PulseReserve.AppHost.Configuration;
using PulseReserve.Infrastructure.Persistence.Catalog;
using PulseReserve.IntegrationTests.Catalog;
using PulseReserve.Modules.Identity.Persistence.Postgres;
using PulseReserve.Modules.Payments.Persistence.Postgres;
using PulseReserve.Modules.Reservation.Persistence.Postgres;

namespace PulseReserve.IntegrationTests.Api;

/// <summary>
/// Hosts the real AppHost pipeline in memory, pointed explicitly at a disposable Testcontainers database.
/// The explicit <c>ConnectionStrings:Catalog</c> setting takes precedence over the legacy
/// <c>PULSERESERVE_TEST_DATABASE</c> fallback, so a developer's environment variable cannot redirect these tests.
/// <para>
/// Safety guard: <see cref="CreateHost"/> verifies the host's effective Catalog connection string before the host is
/// handed out. <c>CreateClient()</c>, <c>Server</c> and <c>Services</c> all go through it, so no HTTP client exists and
/// no request can be sent unless the host targets exactly the expected disposable database. This relies on host
/// startup performing no database I/O before the check has run. Today that holds because (1) Program.cs migrates only in
/// the Development environment (this factory runs as Testing), (2) this factory switches the payments background worker, the
/// live change-feed listener and the hold-expiry worker off (<c>Payments:Workers:Enabled</c>, <c>Live:ChangeFeed:Enabled</c>,
/// <c>Reservation:HoldExpiry:Enabled</c> = false), and (3) even where a test switches one on, each of them waits 200-2000 ms
/// before its first database call while this check runs immediately after the host starts. If startup database work is ever
/// added, this check must move before host start (a unit test pins points 2 and 3).
/// The legacy <c>PULSERESERVE_TEST_DATABASE</c> variable is read by the application only when no connection string is
/// configured; this factory always configures one, and the guard compares the EFFECTIVE string of every module context.
/// </para>
/// </summary>
public sealed class CatalogApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _configuredConnectionString;
    private readonly IReadOnlyDictionary<string, string?> _extraSettings = new Dictionary<string, string?>();
    private readonly Action<IServiceCollection>? _configureServices;

    /// <summary>Production test path: the expected and configured database are both the fixture's own container.</summary>
    public CatalogApiFactory(PostgreSqlDatabaseFixture database)
        : this(database.ConnectionString, database.ConnectionString)
    {
    }

    /// <summary>Test host with extra configuration (e.g. a tiny rate limit) and/or service overrides (e.g. a controllable clock).</summary>
    public CatalogApiFactory(
        PostgreSqlDatabaseFixture database,
        IReadOnlyDictionary<string, string?>? extraSettings,
        Action<IServiceCollection>? configureServices = null)
        : this(database.ConnectionString, database.ConnectionString)
    {
        _extraSettings = extraSettings ?? _extraSettings;
        _configureServices = configureServices;
    }

    /// <summary>Guard tests only: lets the configured database differ from (or be absent versus) the expected one.</summary>
    internal CatalogApiFactory(string expectedConnectionString, string? configuredConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConnectionString);
        ExpectedConnectionString = expectedConnectionString;
        _configuredConnectionString = configuredConnectionString;
    }

    public string ExpectedConnectionString { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Generous by default so suites can register many users; the rate-limit test overrides it.
        builder.UseSetting("Identity:RateLimit:PermitsPerMinute", "100000");
        // The payment simulator is on for tests. The Testing environment is not permissive by name alone: the explicit opt-in below is
        // what a test harness sets and a host deployed under that name by mistake does not have (see PaymentsOptions).
        builder.UseSetting("Payments:Simulator:Enabled", "true");
        builder.UseSetting("Payments:Simulator:AllowInTestingEnvironment", "true");
        builder.UseSetting("Notifications:DevAdapter:Enabled", "true");
        // Background workers are off for determinism: tests drive PaymentsWorkflow directly (one test enables the hosted worker).
        builder.UseSetting("Payments:Workers:Enabled", "false");
        // Same for the real-time layer's two database-facing workers: the NOTIFY listener and the hold-expiry worker. A test that is
        // about them enables them explicitly through the extra settings.
        builder.UseSetting("Live:ChangeFeed:Enabled", "false");
        builder.UseSetting("Reservation:HoldExpiry:Enabled", "false");
        builder.UseSetting("Payments:Workflow:CompletionGraceSeconds", "0");
        builder.UseSetting("Payments:Workflow:BackoffBaseSeconds", "0");
        foreach (var (key, value) in _extraSettings)
            builder.UseSetting(key, value);
        if (_configureServices is not null)
            builder.ConfigureTestServices(_configureServices);
        if (_configuredConnectionString is not null)
            builder.UseSetting($"ConnectionStrings:{CatalogConnectionString.ConnectionStringName}", _configuredConnectionString);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        try
        {
            // Every module DbContext must target the disposable database before any client is handed out.
            using var scope = host.Services.CreateScope();
            TestDatabaseGuard.EnsureTargetsExpectedDatabase(
                scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.GetConnectionString(), ExpectedConnectionString);
            TestDatabaseGuard.EnsureTargetsExpectedDatabase(
                scope.ServiceProvider.GetRequiredService<ReservationDbContext>().Database.GetConnectionString(), ExpectedConnectionString);
            TestDatabaseGuard.EnsureTargetsExpectedDatabase(
                scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.GetConnectionString(), ExpectedConnectionString);
            TestDatabaseGuard.EnsureTargetsExpectedDatabase(
                scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.GetConnectionString(), ExpectedConnectionString);
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return host;
    }
}

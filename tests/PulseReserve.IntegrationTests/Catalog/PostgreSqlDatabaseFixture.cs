using Microsoft.EntityFrameworkCore;
using PulseReserve.Infrastructure.Persistence.Catalog;
using PulseReserve.Modules.Identity.Persistence.Postgres;
using PulseReserve.Modules.Payments.Persistence.Postgres;
using PulseReserve.Modules.Reservation.Persistence.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace PulseReserve.IntegrationTests.Catalog;

/// <summary>
/// Provides an isolated, disposable PostgreSQL instance (Testcontainers) for real integration testing.
/// Each test class receives its own throwaway container, migrated from scratch and destroyed afterwards.
/// The fixture deliberately has no fallback to a local or configured database, so destructive test
/// operations (migrations, TRUNCATE) can never reach an existing environment. Requires a running Docker engine.
/// </summary>
public sealed class PostgreSqlDatabaseFixture : IAsyncLifetime
{
    public const string DefaultDatabaseName = "pulsereserve_test";
    // Exact patch tag plus immutable index digest (verified against Docker Hub on 2026-10-20: PostgreSQL 17.11).
    // Update both parts deliberately when moving to a newer PostgreSQL image.
    public const string PostgreSqlImage =
        "postgres:17.11-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostgreSqlImage)
        .WithDatabase(DefaultDatabaseName)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public CatalogDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<CatalogDbContext>();
        optionsBuilder.UseNpgsql(ConnectionString, npgsqlOptions =>
        {
            npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", CatalogDbContext.Schema);
        });

        return new CatalogDbContext(optionsBuilder.Options);
    }

    public ReservationDbContext CreateReservationDbContext()
    {
        var options = new DbContextOptionsBuilder<ReservationDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ReservationDbContext.Schema))
            .Options;

        return new ReservationDbContext(options);
    }

    public IdentityDbContext CreateIdentityDbContext()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema))
            .Options;

        return new IdentityDbContext(options);
    }

    public PaymentsDbContext CreatePaymentsDbContext()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", PaymentsDbContext.Schema))
            .Options;

        return new PaymentsDbContext(options);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"[Docker Prerequisite] Unable to start the disposable PostgreSQL container '{PostgreSqlImage}'. " +
                $"Integration tests require a running Docker engine. Error: {ex.Message}", ex);
        }

        using var context = CreateDbContext();
        await context.Database.MigrateAsync();

        await using var reservationContext = CreateReservationDbContext();
        await reservationContext.Database.MigrateAsync();

        await using var identityContext = CreateIdentityDbContext();
        await identityContext.Database.MigrateAsync();

        await using var paymentsContext = CreatePaymentsDbContext();
        await paymentsContext.Database.MigrateAsync();
    }

    public async Task CleanupAsync()
    {
        using var context = CreateDbContext();
        await context.Database.ExecuteSqlRawAsync(@"
            TRUNCATE TABLE
                catalog.event_prices,
                catalog.seats,
                catalog.rows,
                catalog.sections,
                catalog.price_tiers,
                catalog.events,
                catalog.seating_layouts,
                catalog.venues
            CASCADE;");
    }

    /// <summary>
    /// Empties every table of the four module schemas in this fixture's own container (never the migration history), so that a test
    /// that asserts global row counts starts from nothing. Test classes share one container across their tests, so those classes call
    /// this before each test. It acts only on the container's connection string, like every other operation here.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var context = CreatePaymentsDbContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DO $reset$
            DECLARE t text;
            BEGIN
                FOR t IN
                    SELECT format('%I.%I', schemaname, tablename)
                    FROM pg_tables
                    WHERE schemaname IN ('catalog', 'reservation', 'identity', 'payments') AND tablename <> '__EFMigrationsHistory'
                LOOP
                    EXECUTE 'TRUNCATE TABLE ' || t || ' RESTART IDENTITY CASCADE';
                END LOOP;
            END
            $reset$;
            """);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

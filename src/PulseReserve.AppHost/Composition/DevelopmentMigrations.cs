using Microsoft.EntityFrameworkCore;
using PulseReserve.Infrastructure.Persistence.Catalog;
using PulseReserve.Modules.Identity.Persistence.Postgres;
using PulseReserve.Modules.Payments.Persistence.Postgres;
using PulseReserve.Modules.Reservation.Persistence.Postgres;

namespace PulseReserve.AppHost.Composition;

/// <summary>
/// Opt-in schema migration on startup for the local Docker workflow. The host never migrates on its own: it requires
/// <c>Database:MigrateOnStartup=true</c> AND the Development environment, so a production host cannot be pointed at it
/// by accident. Migrations are additive (EF applies only pending ones) and never drop data.
/// </summary>
public static class DevelopmentMigrations
{
    public const string ConfigKey = "Database:MigrateOnStartup";

    public static async Task ApplyIfRequestedAsync(WebApplication app)
    {
        if (!app.Configuration.GetValue(ConfigKey, false))
            return;

        if (!app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is only honoured in the Development environment. Apply migrations explicitly elsewhere.");
        }

        await using var scope = app.Services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DevelopmentMigrations");

        // Retry briefly: the database container can be "started" before it accepts connections.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await MigrateAsync<CatalogDbContext>(scope.ServiceProvider, logger);
                await MigrateAsync<IdentityDbContext>(scope.ServiceProvider, logger);
                await MigrateAsync<ReservationDbContext>(scope.ServiceProvider, logger);
                await MigrateAsync<PaymentsDbContext>(scope.ServiceProvider, logger);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}/10): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static async Task MigrateAsync<TContext>(IServiceProvider services, ILogger logger) where TContext : DbContext
    {
        var context = services.GetRequiredService<TContext>();
        var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("{Context}: schema up to date", typeof(TContext).Name);
            return;
        }

        await context.Database.MigrateAsync();
        logger.LogInformation("{Context}: applied {Count} migration(s): {Names}", typeof(TContext).Name, pending.Count, string.Join(", ", pending));
    }
}

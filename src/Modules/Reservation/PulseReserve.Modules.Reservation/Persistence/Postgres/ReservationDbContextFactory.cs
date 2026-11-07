using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PulseReserve.Modules.Reservation.Persistence.Postgres;

/// <summary>
/// Design-time factory for EF Core tooling (migration generation only). It never connects to a database; the
/// placeholder connection string only selects the provider. Apply migrations explicitly to an intended database.
/// </summary>
public sealed class ReservationDbContextFactory : IDesignTimeDbContextFactory<ReservationDbContext>
{
    public ReservationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ReservationDbContext>()
            .UseNpgsql("Host=localhost;Database=pulsereserve;Username=postgres",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ReservationDbContext.Schema))
            .Options;

        return new ReservationDbContext(options);
    }
}

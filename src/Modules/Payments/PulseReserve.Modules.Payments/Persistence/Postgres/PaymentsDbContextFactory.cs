using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PulseReserve.Modules.Payments.Persistence.Postgres;

/// <summary>
/// Design-time factory for EF Core tooling (migration generation only). It never connects to a database; the
/// placeholder connection string only selects the provider. Apply migrations explicitly to an intended database.
/// </summary>
public sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql("Host=localhost;Database=pulsereserve;Username=postgres",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", PaymentsDbContext.Schema))
            .Options;

        return new PaymentsDbContext(options);
    }
}

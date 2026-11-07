using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PulseReserve.Modules.Identity.Persistence.Postgres;

/// <summary>
/// Design-time factory for EF Core tooling (migration generation only). It never connects to a database; the
/// placeholder connection string only selects the provider. Apply migrations explicitly to an intended database.
/// </summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=pulsereserve;Username=postgres",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema))
            .Options;

        return new IdentityDbContext(options);
    }
}

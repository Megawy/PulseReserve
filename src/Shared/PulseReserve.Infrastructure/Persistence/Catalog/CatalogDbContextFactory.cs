using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PulseReserve.Infrastructure.Persistence.Catalog;

/// <summary>
/// Design-time factory for CatalogDbContext to support EF Core tooling (migrations, model inspection).
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<CatalogDbContext>();

        // Design-time connection string targeting the isolated "catalog" schema
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=pulsereserve;Username=postgres",
            npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", CatalogDbContext.Schema);
            });

        return new CatalogDbContext(optionsBuilder.Options);
    }
}

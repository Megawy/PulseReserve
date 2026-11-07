using Microsoft.EntityFrameworkCore;
using PulseReserve.Infrastructure.Persistence.Catalog.Converters;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.Venues;

namespace PulseReserve.Infrastructure.Persistence.Catalog;

/// <summary>
/// Authoritative Entity Framework Core DbContext for the Catalog bounded context.
/// Targets the isolated "catalog" schema within PostgreSQL.
/// </summary>
public sealed class CatalogDbContext : DbContext
{
    public const string Schema = "catalog";

    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Aggregate Root: Venues
    /// </summary>
    public DbSet<Venue> Venues => Set<Venue>();

    /// <summary>
    /// Aggregate Root: SeatingLayouts (owns Sections, Rows, Seats, and PriceTiers)
    /// </summary>
    public DbSet<SeatingLayout> SeatingLayouts => Set<SeatingLayout>();

    /// <summary>
    /// Aggregate Root: Events (owns EventPrices)
    /// </summary>
    public DbSet<Event> Events => Set<Event>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.AddInterceptors(new Interceptors.CatalogOptimisticConcurrencyInterceptor());
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Centrally register strongly typed ID conversions to/from PostgreSQL UUIDs
        configurationBuilder.Properties<VenueId>().HaveConversion<VenueIdConverter>();
        configurationBuilder.Properties<SeatingLayoutId>().HaveConversion<SeatingLayoutIdConverter>();
        configurationBuilder.Properties<SectionId>().HaveConversion<SectionIdConverter>();
        configurationBuilder.Properties<RowId>().HaveConversion<RowIdConverter>();
        configurationBuilder.Properties<SeatId>().HaveConversion<SeatIdConverter>();
        configurationBuilder.Properties<PriceTierId>().HaveConversion<PriceTierIdConverter>();
        configurationBuilder.Properties<EventId>().HaveConversion<EventIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Enforce Catalog schema boundary in PostgreSQL
        modelBuilder.HasDefaultSchema(Schema);

        // Apply explicit entity type configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
    }
}

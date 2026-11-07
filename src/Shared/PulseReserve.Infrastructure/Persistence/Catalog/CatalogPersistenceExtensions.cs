using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PulseReserve.Infrastructure.Persistence.Catalog;

/// <summary>
/// Dependency injection extensions for Catalog persistence infrastructure.
/// </summary>
public static class CatalogPersistenceExtensions
{
    public const string DefaultConnectionStringName = "Catalog";

    /// <summary>
    /// Configures and registers CatalogDbContext against the PostgreSQL provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The PostgreSQL connection string (configured via ConnectionStrings:Catalog).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCatalogPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<CatalogDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", CatalogDbContext.Schema);
            });
        });

        services.AddScoped<PulseReserve.Modules.Catalog.Persistence.IVenueRepository, Repositories.VenueRepository>();
        services.AddScoped<PulseReserve.Modules.Catalog.Persistence.ISeatingLayoutRepository, Repositories.SeatingLayoutRepository>();
        services.AddScoped<PulseReserve.Modules.Catalog.Persistence.IEventRepository, Repositories.EventRepository>();
        services.AddScoped<PulseReserve.Modules.Catalog.Persistence.ICatalogUnitOfWork, Repositories.CatalogUnitOfWork>();
        services.AddSingleton<PulseReserve.SharedKernel.IDateTimeProvider, SystemDateTimeProvider>();

        // Register Catalog Feature Handlers
        services.AddScoped<PulseReserve.Modules.Catalog.Features.Venues.CreateVenue.CreateVenueHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.Venues.GetVenueById.GetVenueByIdHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.SeatingLayouts.CreateSeatingLayout.CreateSeatingLayoutHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSection.AddSectionHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddRow.AddRowHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSeat.AddSeatHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddPriceTier.AddPriceTierHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.SeatingLayouts.LockSeatingLayout.LockSeatingLayoutHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.SeatingLayouts.GetSeatingLayoutById.GetSeatingLayoutByIdHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.Events.CreateEvent.CreateEventHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.Events.SetEventPrice.SetEventPriceHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.Events.PublishEvent.PublishEventHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.Events.StartSale.StartSaleHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.Events.GetEventById.GetEventByIdHandler>();
        services.AddScoped<PulseReserve.Modules.Catalog.Features.Events.ListEvents.ListEventsHandler>();

        return services;
    }
}

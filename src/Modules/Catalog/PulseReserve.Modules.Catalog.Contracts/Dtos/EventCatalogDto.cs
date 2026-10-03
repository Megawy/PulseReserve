namespace PulseReserve.Modules.Catalog.Contracts;

public sealed record EventCatalogDto(
    EventId Id,
    string Name,
    string Description,
    VenueId VenueId,
    SeatingLayoutId SeatingLayoutId,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    string Status);

namespace PulseReserve.Modules.Catalog.Contracts;

public sealed record EventPublishedIntegrationEvent(
    EventId EventId,
    VenueId VenueId,
    SeatingLayoutId SeatingLayoutId,
    DateTime ScheduledStartUtc,
    DateTime PublishedAtUtc);

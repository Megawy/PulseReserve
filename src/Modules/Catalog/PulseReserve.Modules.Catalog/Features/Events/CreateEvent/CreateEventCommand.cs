using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.Events.CreateEvent;

public sealed record CreateEventCommand(
    EventId Id,
    string Name,
    string Description,
    VenueId VenueId,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    SeatingLayoutId? SeatingLayoutId = null);

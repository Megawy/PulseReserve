using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.Events.GetEventById;

public sealed record EventResponse(
    EventId Id,
    string Name,
    string Description,
    VenueId VenueId,
    SeatingLayoutId? SeatingLayoutId,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    string Status,
    DateTime? PublishedAtUtc,
    DateTime? OnSaleAtUtc,
    DateTime? CancelledAtUtc,
    string? CancellationReason,
    IReadOnlyCollection<EventPriceResponse> Prices);

public sealed record EventPriceResponse(
    PriceTierId PriceTierId,
    decimal Amount,
    string Currency);

using PulseReserve.Modules.Catalog.Features.Events.GetEventById;
using PulseReserve.Modules.Catalog.Persistence;

namespace PulseReserve.Modules.Catalog.Features.Events.ListEvents;

/// <summary>Lists customer-visible events (published, on sale or closed) using the same shape as GetEventById.</summary>
public sealed class ListEventsHandler(IEventRepository eventRepository)
{
    public async Task<IReadOnlyList<EventResponse>> HandleAsync(CancellationToken cancellationToken = default)
    {
        var events = await eventRepository.ListPublicAsync(cancellationToken);

        return events.Select(e => new EventResponse(
            e.Id,
            e.Name,
            e.Description,
            e.VenueId,
            e.SeatingLayoutId,
            e.ScheduledStartUtc,
            e.ScheduledEndUtc,
            e.Status.ToString(),
            e.PublishedAtUtc,
            e.OnSaleAtUtc,
            e.CancelledAtUtc,
            e.CancellationReason,
            e.Prices.Select(p => new EventPriceResponse(p.PriceTierId, p.Price.Amount, p.Price.Currency)).ToList())).ToList();
    }
}

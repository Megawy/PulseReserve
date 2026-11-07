using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.Events.GetEventById;

public sealed class GetEventByIdHandler
{
    private readonly IEventRepository _eventRepository;

    public GetEventByIdHandler(IEventRepository eventRepository)
    {
        _eventRepository = eventRepository;
    }

    public async Task<Result<EventResponse>> HandleAsync(
        GetEventByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var @event = await _eventRepository.GetByIdAsync(
            query.EventId,
            cancellationToken: cancellationToken);

        if (@event is null)
            return Result.Failure<EventResponse>(Error.NotFound("Event.NotFound", $"Event with ID '{query.EventId}' was not found."));

        return Result.Success(new EventResponse(
            @event.Id,
            @event.Name,
            @event.Description,
            @event.VenueId,
            @event.SeatingLayoutId,
            @event.ScheduledStartUtc,
            @event.ScheduledEndUtc,
            @event.Status.ToString(),
            @event.PublishedAtUtc,
            @event.OnSaleAtUtc,
            @event.CancelledAtUtc,
            @event.CancellationReason,
            @event.Prices.Select(p => new EventPriceResponse(p.PriceTierId, p.Price.Amount, p.Price.Currency)).ToList()));
    }
}

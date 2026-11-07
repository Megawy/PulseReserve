using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.Events.CreateEvent;

public sealed class CreateEventHandler
{
    private readonly IVenueRepository _venueRepository;
    private readonly ISeatingLayoutRepository _seatingLayoutRepository;
    private readonly IEventRepository _eventRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;

    public CreateEventHandler(
        IVenueRepository venueRepository,
        ISeatingLayoutRepository seatingLayoutRepository,
        IEventRepository eventRepository,
        ICatalogUnitOfWork unitOfWork)
    {
        _venueRepository = venueRepository;
        _seatingLayoutRepository = seatingLayoutRepository;
        _eventRepository = eventRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<EventId>> HandleAsync(
        CreateEventCommand command,
        CancellationToken cancellationToken = default)
    {
        var venueExists = await _venueRepository.ExistsAsync(command.VenueId, cancellationToken);
        if (!venueExists)
            return Result.Failure<EventId>(Error.NotFound("Venue.NotFound", $"Venue with ID '{command.VenueId}' was not found."));

        if (command.SeatingLayoutId.HasValue && command.SeatingLayoutId.Value != SeatingLayoutId.Empty)
        {
            var layoutExists = await _seatingLayoutRepository.ExistsAsync(command.SeatingLayoutId.Value, cancellationToken);
            if (!layoutExists)
                return Result.Failure<EventId>(Error.NotFound("SeatingLayout.NotFound", $"Seating layout with ID '{command.SeatingLayoutId.Value}' was not found."));
        }

        var eventResult = Event.CreateDraft(
            command.Id,
            command.Name,
            command.Description,
            command.VenueId,
            command.ScheduledStartUtc,
            command.ScheduledEndUtc);

        if (eventResult.IsFailure)
            return Result.Failure<EventId>(eventResult.Error);

        var @event = eventResult.Value;
        if (command.SeatingLayoutId.HasValue && command.SeatingLayoutId.Value != SeatingLayoutId.Empty)
        {
            var assignResult = @event.AssignSeatingLayout(command.SeatingLayoutId.Value);
            if (assignResult.IsFailure)
                return Result.Failure<EventId>(assignResult.Error);
        }

        await _eventRepository.AddAsync(@event, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(@event.Id);
    }
}

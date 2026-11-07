using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.Events.PublishEvent;

public sealed class PublishEventHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly ISeatingLayoutRepository _seatingLayoutRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _dateTimeProvider;

    public PublishEventHandler(
        IEventRepository eventRepository,
        ISeatingLayoutRepository seatingLayoutRepository,
        ICatalogUnitOfWork unitOfWork,
        IDateTimeProvider dateTimeProvider)
    {
        _eventRepository = eventRepository;
        _seatingLayoutRepository = seatingLayoutRepository;
        _unitOfWork = unitOfWork;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result> HandleAsync(
        PublishEventCommand command,
        CancellationToken cancellationToken = default)
    {
        var @event = await _eventRepository.GetByIdAsync(
            command.EventId,
            command.ExpectedVersion,
            cancellationToken);

        if (@event is null)
            return Result.Failure(Error.NotFound("Event.NotFound", $"Event with ID '{command.EventId}' was not found."));

        if (!@event.SeatingLayoutId.HasValue || @event.SeatingLayoutId.Value == SeatingLayoutId.Empty)
            return Result.Failure(new Error("Event.NoSeatingLayout", "Event cannot be published without a linked seating layout."));

        var layout = await _seatingLayoutRepository.GetByIdAsync(
            @event.SeatingLayoutId.Value,
            cancellationToken: cancellationToken);

        if (layout is null)
            return Result.Failure(Error.NotFound("SeatingLayout.NotFound", $"Seating layout with ID '{@event.SeatingLayoutId.Value}' was not found."));

        var structureResult = layout.ValidateStructure();
        if (structureResult.IsFailure)
            return structureResult;

        var missingPriceTiers = layout.PriceTiers
            .Where(pt => !@event.Prices.Any(p => p.PriceTierId == pt.Id))
            .ToList();

        if (missingPriceTiers.Count > 0)
        {
            return Result.Failure(new Error(
                "Event.IncompletePricing",
                $"Event is missing prices for price tiers: {string.Join(", ", missingPriceTiers.Select(pt => pt.Name))}"));
        }

        var publishResult = @event.Publish(_dateTimeProvider.UtcNow);
        if (publishResult.IsFailure)
            return publishResult;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

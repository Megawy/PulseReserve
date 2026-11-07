using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.Events.SetEventPrice;

public sealed class SetEventPriceHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;

    public SetEventPriceHandler(
        IEventRepository eventRepository,
        ICatalogUnitOfWork unitOfWork)
    {
        _eventRepository = eventRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> HandleAsync(
        SetEventPriceCommand command,
        CancellationToken cancellationToken = default)
    {
        var moneyResult = Money.Create(command.PriceAmount, command.Currency);
        if (moneyResult.IsFailure)
            return moneyResult;

        var @event = await _eventRepository.GetByIdAsync(
            command.EventId,
            command.ExpectedVersion,
            cancellationToken);

        if (@event is null)
            return Result.Failure(Error.NotFound("Event.NotFound", $"Event with ID '{command.EventId}' was not found."));

        var setPriceResult = @event.SetPrice(command.PriceTierId, moneyResult.Value);
        if (setPriceResult.IsFailure)
            return setPriceResult;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

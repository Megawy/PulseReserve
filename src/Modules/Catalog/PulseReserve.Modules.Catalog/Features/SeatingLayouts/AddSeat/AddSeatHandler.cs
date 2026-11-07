using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSeat;

public sealed class AddSeatHandler
{
    private readonly ISeatingLayoutRepository _seatingLayoutRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;

    public AddSeatHandler(
        ISeatingLayoutRepository seatingLayoutRepository,
        ICatalogUnitOfWork unitOfWork)
    {
        _seatingLayoutRepository = seatingLayoutRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> HandleAsync(
        AddSeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var layout = await _seatingLayoutRepository.GetByIdAsync(
            command.LayoutId,
            command.ExpectedVersion,
            cancellationToken);

        if (layout is null)
            return Result.Failure(Error.NotFound("SeatingLayout.NotFound", $"Seating layout with ID '{command.LayoutId}' was not found."));

        var coordResult = SeatCoordinate.Create(command.CoordinateX, command.CoordinateY);
        if (coordResult.IsFailure)
            return coordResult;

        var seatResult = Seat.Create(
            command.SeatId,
            command.RowId,
            command.SectionId,
            command.Label,
            coordResult.Value,
            command.PriceTierId);

        if (seatResult.IsFailure)
            return seatResult;

        var addResult = layout.AddSeat(seatResult.Value);
        if (addResult.IsFailure)
            return addResult;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

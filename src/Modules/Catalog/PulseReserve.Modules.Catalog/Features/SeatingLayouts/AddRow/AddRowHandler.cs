using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddRow;

public sealed class AddRowHandler
{
    private readonly ISeatingLayoutRepository _seatingLayoutRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;

    public AddRowHandler(
        ISeatingLayoutRepository seatingLayoutRepository,
        ICatalogUnitOfWork unitOfWork)
    {
        _seatingLayoutRepository = seatingLayoutRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> HandleAsync(
        AddRowCommand command,
        CancellationToken cancellationToken = default)
    {
        var layout = await _seatingLayoutRepository.GetByIdAsync(
            command.LayoutId,
            command.ExpectedVersion,
            cancellationToken);

        if (layout is null)
            return Result.Failure(Error.NotFound("SeatingLayout.NotFound", $"Seating layout with ID '{command.LayoutId}' was not found."));

        var rowResult = Row.Create(command.RowId, command.SectionId, command.Label, command.DisplayOrder);
        if (rowResult.IsFailure)
            return rowResult;

        var addResult = layout.AddRow(rowResult.Value);
        if (addResult.IsFailure)
            return addResult;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddPriceTier;

public sealed class AddPriceTierHandler
{
    private readonly ISeatingLayoutRepository _seatingLayoutRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;

    public AddPriceTierHandler(
        ISeatingLayoutRepository seatingLayoutRepository,
        ICatalogUnitOfWork unitOfWork)
    {
        _seatingLayoutRepository = seatingLayoutRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> HandleAsync(
        AddPriceTierCommand command,
        CancellationToken cancellationToken = default)
    {
        var layout = await _seatingLayoutRepository.GetByIdAsync(
            command.LayoutId,
            command.ExpectedVersion,
            cancellationToken);

        if (layout is null)
            return Result.Failure(Error.NotFound("SeatingLayout.NotFound", $"Seating layout with ID '{command.LayoutId}' was not found."));

        var tierResult = PriceTier.Create(command.PriceTierId, command.Name);
        if (tierResult.IsFailure)
            return tierResult;

        var addResult = layout.AddPriceTier(tierResult.Value);
        if (addResult.IsFailure)
            return addResult;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

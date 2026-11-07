using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.LockSeatingLayout;

public sealed class LockSeatingLayoutHandler
{
    private readonly ISeatingLayoutRepository _seatingLayoutRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;

    public LockSeatingLayoutHandler(
        ISeatingLayoutRepository seatingLayoutRepository,
        ICatalogUnitOfWork unitOfWork)
    {
        _seatingLayoutRepository = seatingLayoutRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> HandleAsync(
        LockSeatingLayoutCommand command,
        CancellationToken cancellationToken = default)
    {
        var layout = await _seatingLayoutRepository.GetByIdAsync(
            command.LayoutId,
            command.ExpectedVersion,
            cancellationToken);

        if (layout is null)
            return Result.Failure(Error.NotFound("SeatingLayout.NotFound", $"Seating layout with ID '{command.LayoutId}' was not found."));

        var lockResult = layout.LockLayout();
        if (lockResult.IsFailure)
            return lockResult;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

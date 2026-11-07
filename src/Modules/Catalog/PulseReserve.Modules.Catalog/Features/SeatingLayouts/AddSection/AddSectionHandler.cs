using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSection;

public sealed class AddSectionHandler
{
    private readonly ISeatingLayoutRepository _seatingLayoutRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;

    public AddSectionHandler(
        ISeatingLayoutRepository seatingLayoutRepository,
        ICatalogUnitOfWork unitOfWork)
    {
        _seatingLayoutRepository = seatingLayoutRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> HandleAsync(
        AddSectionCommand command,
        CancellationToken cancellationToken = default)
    {
        var layout = await _seatingLayoutRepository.GetByIdAsync(
            command.LayoutId,
            command.ExpectedVersion,
            cancellationToken);

        if (layout is null)
            return Result.Failure(Error.NotFound("SeatingLayout.NotFound", $"Seating layout with ID '{command.LayoutId}' was not found."));

        var sectionResult = Section.Create(command.SectionId, command.Name, command.DisplayOrder);
        if (sectionResult.IsFailure)
            return sectionResult;

        var addResult = layout.AddSection(sectionResult.Value);
        if (addResult.IsFailure)
            return addResult;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.CreateSeatingLayout;

public sealed class CreateSeatingLayoutHandler
{
    private readonly IVenueRepository _venueRepository;
    private readonly ISeatingLayoutRepository _seatingLayoutRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;

    public CreateSeatingLayoutHandler(
        IVenueRepository venueRepository,
        ISeatingLayoutRepository seatingLayoutRepository,
        ICatalogUnitOfWork unitOfWork)
    {
        _venueRepository = venueRepository;
        _seatingLayoutRepository = seatingLayoutRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<SeatingLayoutId>> HandleAsync(
        CreateSeatingLayoutCommand command,
        CancellationToken cancellationToken = default)
    {
        var venueExists = await _venueRepository.ExistsAsync(command.VenueId, cancellationToken);
        if (!venueExists)
            return Result.Failure<SeatingLayoutId>(Error.NotFound("Venue.NotFound", $"Venue with ID '{command.VenueId}' was not found."));

        var layoutResult = SeatingLayout.Create(command.Id, command.VenueId, command.Name);
        if (layoutResult.IsFailure)
            return Result.Failure<SeatingLayoutId>(layoutResult.Error);

        await _seatingLayoutRepository.AddAsync(layoutResult.Value, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(layoutResult.Value.Id);
    }
}

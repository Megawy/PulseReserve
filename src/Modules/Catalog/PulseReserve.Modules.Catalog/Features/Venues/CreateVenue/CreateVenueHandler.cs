using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.Modules.Catalog.Domain.Venues;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.Venues.CreateVenue;

public sealed class CreateVenueHandler
{
    private readonly IVenueRepository _venueRepository;
    private readonly ICatalogUnitOfWork _unitOfWork;

    public CreateVenueHandler(
        IVenueRepository venueRepository,
        ICatalogUnitOfWork unitOfWork)
    {
        _venueRepository = venueRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<VenueId>> HandleAsync(
        CreateVenueCommand command,
        CancellationToken cancellationToken = default)
    {
        var addressResult = Address.Create(command.Street, command.City, command.Country, command.PostalCode);
        if (addressResult.IsFailure)
            return Result.Failure<VenueId>(addressResult.Error);

        var venueResult = Venue.Create(command.Id, command.Name, addressResult.Value);
        if (venueResult.IsFailure)
            return Result.Failure<VenueId>(venueResult.Error);

        await _venueRepository.AddAsync(venueResult.Value, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(venueResult.Value.Id);
    }
}

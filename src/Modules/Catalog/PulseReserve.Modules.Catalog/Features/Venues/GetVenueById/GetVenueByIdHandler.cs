using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Features.Venues.GetVenueById;

public sealed class GetVenueByIdHandler
{
    private readonly IVenueRepository _venueRepository;

    public GetVenueByIdHandler(IVenueRepository venueRepository)
    {
        _venueRepository = venueRepository;
    }

    public async Task<Result<VenueResponse>> HandleAsync(
        GetVenueByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var venue = await _venueRepository.GetByIdAsync(query.Id, cancellationToken);
        if (venue is null)
            return Result.Failure<VenueResponse>(Error.NotFound("Venue.NotFound", $"Venue with ID '{query.Id}' was not found."));

        return Result.Success(new VenueResponse(
            venue.Id,
            venue.Name,
            venue.Address.Street,
            venue.Address.City,
            venue.Address.Country,
            venue.Address.PostalCode));
    }
}

using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.Venues.GetVenueById;

public sealed record VenueResponse(
    VenueId Id,
    string Name,
    string Street,
    string City,
    string Country,
    string PostalCode);

using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.Venues.CreateVenue;

public sealed record CreateVenueCommand(
    VenueId Id,
    string Name,
    string Street,
    string City,
    string Country,
    string PostalCode);

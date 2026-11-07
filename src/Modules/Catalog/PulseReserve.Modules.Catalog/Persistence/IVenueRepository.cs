using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Venues;

namespace PulseReserve.Modules.Catalog.Persistence;

public interface IVenueRepository
{
    Task AddAsync(Venue venue, CancellationToken cancellationToken = default);
    Task<Venue?> GetByIdAsync(VenueId id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(VenueId id, CancellationToken cancellationToken = default);
}

using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Venues;
using PulseReserve.Modules.Catalog.Persistence;

namespace PulseReserve.UnitTests.Catalog.Fakes;

public sealed class FakeVenueRepository : IVenueRepository
{
    private readonly Dictionary<VenueId, Venue> _venues = [];

    public Task AddAsync(Venue venue, CancellationToken cancellationToken = default)
    {
        _venues[venue.Id] = venue;
        return Task.CompletedTask;
    }

    public Task<Venue?> GetByIdAsync(VenueId id, CancellationToken cancellationToken = default)
    {
        _venues.TryGetValue(id, out var venue);
        return Task.FromResult(venue);
    }

    public Task<bool> ExistsAsync(VenueId id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_venues.ContainsKey(id));
    }
}

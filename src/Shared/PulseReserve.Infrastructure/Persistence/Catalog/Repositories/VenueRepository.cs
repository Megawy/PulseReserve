using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Venues;
using PulseReserve.Modules.Catalog.Persistence;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Repositories;

public sealed class VenueRepository : IVenueRepository
{
    private readonly CatalogDbContext _context;

    public VenueRepository(CatalogDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Venue venue, CancellationToken cancellationToken = default)
    {
        await _context.Venues.AddAsync(venue, cancellationToken);
    }

    public async Task<Venue?> GetByIdAsync(VenueId id, CancellationToken cancellationToken = default)
    {
        return await _context.Venues.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
    }

    public async Task<bool> ExistsAsync(VenueId id, CancellationToken cancellationToken = default)
    {
        return await _context.Venues.AnyAsync(v => v.Id == id, cancellationToken);
    }
}

using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Persistence;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Repositories;

public sealed class SeatingLayoutRepository : ISeatingLayoutRepository
{
    private readonly CatalogDbContext _context;

    public SeatingLayoutRepository(CatalogDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(SeatingLayout seatingLayout, CancellationToken cancellationToken = default)
    {
        await _context.SeatingLayouts.AddAsync(seatingLayout, cancellationToken);
    }

    public async Task<SeatingLayout?> GetByIdAsync(
        SeatingLayoutId id,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var layout = await _context.SeatingLayouts
            .Include(l => l.Sections)
            .Include(l => l.Rows)
            .Include(l => l.Seats)
            .Include(l => l.PriceTiers)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

        if (layout is not null && expectedVersion.HasValue)
        {
            _context.Entry(layout).Property<int>("Version").OriginalValue = expectedVersion.Value;
        }

        return layout;
    }

    public async Task<bool> ExistsAsync(SeatingLayoutId id, CancellationToken cancellationToken = default)
    {
        return await _context.SeatingLayouts.AnyAsync(l => l.Id == id, cancellationToken);
    }
}

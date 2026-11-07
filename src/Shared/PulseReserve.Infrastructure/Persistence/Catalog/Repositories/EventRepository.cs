using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Persistence;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Repositories;

public sealed class EventRepository : IEventRepository
{
    private readonly CatalogDbContext _context;

    public EventRepository(CatalogDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Event @event, CancellationToken cancellationToken = default)
    {
        await _context.Events.AddAsync(@event, cancellationToken);
    }

    public async Task<Event?> GetByIdAsync(
        EventId id,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var @event = await _context.Events
            .Include(e => e.Prices)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (@event is not null && expectedVersion.HasValue)
        {
            _context.Entry(@event).Property<int>("Version").OriginalValue = expectedVersion.Value;
        }

        return @event;
    }

    public async Task<bool> ExistsAsync(EventId id, CancellationToken cancellationToken = default)
    {
        return await _context.Events.AnyAsync(e => e.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Event>> ListPublicAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Events
            .AsNoTracking()
            .Include(e => e.Prices)
            .Where(e => e.Status == EventStatus.Published || e.Status == EventStatus.OnSale || e.Status == EventStatus.Closed)
            .OrderBy(e => e.ScheduledStartUtc)
            .ThenBy(e => e.Name)
            .ToListAsync(cancellationToken);
    }
}

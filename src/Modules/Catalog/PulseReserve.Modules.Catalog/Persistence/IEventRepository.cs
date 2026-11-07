using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;

namespace PulseReserve.Modules.Catalog.Persistence;

public interface IEventRepository
{
    Task AddAsync(Event @event, CancellationToken cancellationToken = default);
    Task<Event?> GetByIdAsync(EventId id, int? expectedVersion = null, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(EventId id, CancellationToken cancellationToken = default);

    /// <summary>Events visible to customers (published, on sale or closed), soonest first. Drafts and cancelled events are excluded.</summary>
    Task<IReadOnlyList<Event>> ListPublicAsync(CancellationToken cancellationToken = default);
}

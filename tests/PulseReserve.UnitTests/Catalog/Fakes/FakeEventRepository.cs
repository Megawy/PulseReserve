using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Persistence;

namespace PulseReserve.UnitTests.Catalog.Fakes;

public sealed class FakeEventRepository : IEventRepository
{
    private readonly Dictionary<EventId, Event> _events = [];
    public int? LastRequestedExpectedVersion { get; private set; }

    public Task AddAsync(Event @event, CancellationToken cancellationToken = default)
    {
        _events[@event.Id] = @event;
        return Task.CompletedTask;
    }

    public Task<Event?> GetByIdAsync(
        EventId id,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        LastRequestedExpectedVersion = expectedVersion;
        _events.TryGetValue(id, out var @event);
        return Task.FromResult(@event);
    }

    public Task<bool> ExistsAsync(EventId id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_events.ContainsKey(id));
    }

    public Task<IReadOnlyList<Event>> ListPublicAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Event> result = _events.Values
            .Where(e => e.Status is EventStatus.Published or EventStatus.OnSale or EventStatus.Closed)
            .OrderBy(e => e.ScheduledStartUtc)
            .ToList();
        return Task.FromResult(result);
    }
}

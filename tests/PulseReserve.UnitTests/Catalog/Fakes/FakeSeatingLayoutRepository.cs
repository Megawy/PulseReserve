using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Persistence;

namespace PulseReserve.UnitTests.Catalog.Fakes;

public sealed class FakeSeatingLayoutRepository : ISeatingLayoutRepository
{
    private readonly Dictionary<SeatingLayoutId, SeatingLayout> _layouts = [];
    public int? LastRequestedExpectedVersion { get; private set; }

    public Task AddAsync(SeatingLayout seatingLayout, CancellationToken cancellationToken = default)
    {
        _layouts[seatingLayout.Id] = seatingLayout;
        return Task.CompletedTask;
    }

    public Task<SeatingLayout?> GetByIdAsync(
        SeatingLayoutId id,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        LastRequestedExpectedVersion = expectedVersion;
        _layouts.TryGetValue(id, out var layout);
        return Task.FromResult(layout);
    }

    public Task<bool> ExistsAsync(SeatingLayoutId id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_layouts.ContainsKey(id));
    }
}

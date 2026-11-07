using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

namespace PulseReserve.Modules.Catalog.Persistence;

public interface ISeatingLayoutRepository
{
    Task AddAsync(SeatingLayout seatingLayout, CancellationToken cancellationToken = default);
    Task<SeatingLayout?> GetByIdAsync(SeatingLayoutId id, int? expectedVersion = null, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(SeatingLayoutId id, CancellationToken cancellationToken = default);
}

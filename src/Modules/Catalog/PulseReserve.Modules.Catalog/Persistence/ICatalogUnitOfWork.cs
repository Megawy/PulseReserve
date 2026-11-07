namespace PulseReserve.Modules.Catalog.Persistence;

public interface ICatalogUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

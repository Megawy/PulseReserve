using PulseReserve.Modules.Catalog.Persistence;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Repositories;

public sealed class CatalogUnitOfWork : ICatalogUnitOfWork
{
    private readonly CatalogDbContext _context;

    public CatalogUnitOfWork(CatalogDbContext context)
    {
        _context = context;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }
}

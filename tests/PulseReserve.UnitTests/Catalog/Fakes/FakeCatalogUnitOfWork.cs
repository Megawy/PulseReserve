using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Catalog.Persistence;

namespace PulseReserve.UnitTests.Catalog.Fakes;

public sealed class FakeCatalogUnitOfWork : ICatalogUnitOfWork
{
    public int SaveCount { get; private set; }
    public Exception? ExceptionToThrow { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        SaveCount++;
        return Task.FromResult(1);
    }
}

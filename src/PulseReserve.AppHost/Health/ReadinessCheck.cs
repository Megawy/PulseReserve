using Microsoft.Extensions.Diagnostics.HealthChecks;
using PulseReserve.Infrastructure.Persistence.Catalog;

namespace PulseReserve.AppHost.Health;

/// <summary>Readiness: the shared PostgreSQL database accepts connections. Liveness never touches dependencies.</summary>
public sealed class DatabaseReadinessCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Database is not reachable."); // no exception detail in the response
        }
    }
}

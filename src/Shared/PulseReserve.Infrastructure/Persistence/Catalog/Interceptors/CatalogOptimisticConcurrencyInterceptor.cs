using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.Modules.Catalog.Domain.SeatingLayouts;
using PulseReserve.Modules.Catalog.Domain.Venues;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Interceptors;

/// <summary>
/// Infrastructure-level SaveChangesInterceptor that enforces aggregate-level optimistic concurrency
/// across the Catalog bounded context.
/// 
/// Intercepts before SaveChanges to detect modifications to child entities (Section, Row, Seat, PriceTier, EventPrice)
/// or direct aggregate root mutations, advancing the owning Aggregate Root's integer Version property exactly once
/// while preserving the original version for optimistic concurrency verification.
/// </summary>
public sealed class CatalogOptimisticConcurrencyInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
        {
            AdvanceAggregateVersions(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            AdvanceAggregateVersions(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public static void AdvanceAggregateVersions(DbContext context)
    {
        var rootsToAdvance = new HashSet<EntityEntry>();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            // 1. Inspect child entities of SeatingLayout
            if (entry.Entity is Section or Row or Seat or PriceTier)
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    var layoutIdProp = entry.Property("SeatingLayoutId");
                    var rawValue = entry.State == EntityState.Deleted
                        ? layoutIdProp.OriginalValue
                        : layoutIdProp.CurrentValue;

                    if (rawValue is not SeatingLayoutId layoutId)
                    {
                        continue;
                    }

                    var rootEntry = context.ChangeTracker
                        .Entries<SeatingLayout>()
                        .FirstOrDefault(e => e.Entity.Id == layoutId);

                    if (rootEntry is null)
                    {
                        throw new InvalidOperationException(
                            $"The aggregate root SeatingLayout with ID '{layoutId}' is not tracked in the current DbContext. " +
                            $"Modifying child entity '{entry.Entity.GetType().Name}' without tracking the aggregate root violates aggregate consistency.");
                    }

                    rootsToAdvance.Add(rootEntry);
                }

                continue;
            }

            // 2. Inspect child entities of Event
            if (entry.Entity is EventPrice)
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    var eventIdProp = entry.Property("EventId");
                    var rawValue = entry.State == EntityState.Deleted
                        ? eventIdProp.OriginalValue
                        : eventIdProp.CurrentValue;

                    if (rawValue is not EventId eventId)
                    {
                        continue;
                    }

                    var rootEntry = context.ChangeTracker
                        .Entries<Event>()
                        .FirstOrDefault(e => e.Entity.Id == eventId);

                    if (rootEntry is null)
                    {
                        throw new InvalidOperationException(
                            $"The aggregate root Event with ID '{eventId}' is not tracked in the current DbContext. " +
                            $"Modifying child entity 'EventPrice' without tracking the aggregate root violates aggregate consistency.");
                    }

                    rootsToAdvance.Add(rootEntry);
                }

                continue;
            }

            // 3. Inspect direct modifications to Aggregate Roots
            if (entry.Entity is Venue or SeatingLayout or Event)
            {
                if (entry.State == EntityState.Modified)
                {
                    rootsToAdvance.Add(entry);
                }
            }
        }

        // Advance version for each identified aggregate root exactly once
        foreach (var rootEntry in rootsToAdvance)
        {
            // Skip roots that are newly added (INSERT uses default version 1) or deleted
            if (rootEntry.State is EntityState.Added or EntityState.Deleted)
            {
                continue;
            }

            var versionProp = rootEntry.Property("Version");
            int originalVersion = versionProp.OriginalValue is int orig ? orig : 1;

            // Idempotency check: advance version only if it has not already been advanced
            if (versionProp.CurrentValue is int currentVersion && currentVersion == originalVersion)
            {
                versionProp.CurrentValue = originalVersion + 1;
                versionProp.IsModified = true;
            }

            // Keep updated_at_utc aligned with the modification
            var updatedAtProp = rootEntry.Metadata.FindProperty("UpdatedAtUtc");
            if (updatedAtProp is not null)
            {
                rootEntry.Property("UpdatedAtUtc").CurrentValue = DateTime.UtcNow;
                rootEntry.Property("UpdatedAtUtc").IsModified = true;
            }
        }
    }
}

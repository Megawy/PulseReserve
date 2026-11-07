using Microsoft.EntityFrameworkCore;
using PulseReserve.Infrastructure.Persistence.Catalog;
using PulseReserve.Modules.Catalog.Domain.Events;
using EventId = PulseReserve.Modules.Catalog.Contracts.EventId;
using PulseReserve.Modules.Reservation.Features.Inventory;

namespace PulseReserve.AppHost.Composition;

/// <summary>
/// Composition-root adapter that answers Reservation's <see cref="IEventSalesCatalog"/> port from Catalog persistence
/// with read-only, untracked queries. Keeping it here means neither module references the other.
/// </summary>
public sealed class CatalogEventSalesAdapter(CatalogDbContext catalog) : IEventSalesCatalog
{
    public async Task<EventSalesStatus?> GetSalesStatusAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var id = new EventId(eventId);
        var status = await catalog.Events.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => (EventStatus?)e.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status is null ? null : new EventSalesStatus(eventId, status.Value.ToString(), status == EventStatus.OnSale);
    }

    public async Task<IReadOnlyList<SellableSeat>> GetSellableSeatsAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var id = new EventId(eventId);
        var @event = await catalog.Events.AsNoTracking()
            .Include(e => e.Prices)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{eventId}' does not exist.");

        if (@event.Status != EventStatus.OnSale || @event.SeatingLayoutId is not { } layoutId)
            throw new InvalidOperationException($"Event '{eventId}' is not on sale; its seats are not a stable snapshot.");

        var layout = await catalog.SeatingLayouts.AsNoTracking()
            .Include(l => l.Seats)
            .FirstAsync(l => l.Id == layoutId, cancellationToken);

        var prices = @event.Prices.ToDictionary(p => p.PriceTierId, p => p.Price);
        return layout.Seats
            .Select(seat => prices.TryGetValue(seat.PriceTierId, out var price)
                ? new SellableSeat(seat.Id.Value, price.Amount, price.Currency)
                : throw new InvalidOperationException(
                    $"Event '{eventId}' is on sale but has no price for tier '{seat.PriceTierId}'; this violates the OnSale pricing invariant."))
            .ToList();
    }
}

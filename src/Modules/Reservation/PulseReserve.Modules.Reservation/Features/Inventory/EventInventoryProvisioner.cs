using PulseReserve.Modules.Reservation.Domain;
using PulseReserve.Modules.Reservation.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Reservation.Features.Inventory;

/// <summary>
/// Ensures an event is on sale and that its seat inventory exists. Inventory is seeded lazily from the immutable
/// OnSale snapshot on first use (no messaging infrastructure exists yet); seeding is idempotent and race-safe.
/// </summary>
public sealed class EventInventoryProvisioner(IEventSalesCatalog catalog, IReservationStore store)
{
    public async Task<Result> EnsureSellableAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var status = await catalog.GetSalesStatusAsync(eventId, cancellationToken);
        if (status is null)
            return Result.Failure(ReservationErrors.EventNotFound(eventId));

        if (!status.IsOnSale)
            return Result.Failure(ReservationErrors.SalesNotOpen);

        if (!await store.InventoryExistsAsync(eventId, cancellationToken))
        {
            var seats = await catalog.GetSellableSeatsAsync(eventId, cancellationToken);
            await store.SeedInventoryAsync(eventId, seats, cancellationToken);
        }

        return Result.Success();
    }
}

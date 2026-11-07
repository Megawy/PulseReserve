using PulseReserve.Modules.Reservation.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Reservation.Features.Inventory;

public sealed record EventAvailabilityResponse(Guid EventId, IReadOnlyList<SeatAvailability> Seats);

/// <summary>
/// Returns each seat's effective availability. A hold whose expiry has passed is reported as Available because it
/// is reclaimable; this read is a point-in-time view and never a reservation guarantee.
/// </summary>
public sealed class GetEventAvailabilityHandler(EventInventoryProvisioner provisioner, IReservationStore store)
{
    public async Task<Result<EventAvailabilityResponse>> HandleAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var sellable = await provisioner.EnsureSellableAsync(eventId, cancellationToken);
        if (sellable.IsFailure)
            return Result.Failure<EventAvailabilityResponse>(sellable.Error);

        var seats = await store.GetAvailabilityAsync(eventId, cancellationToken);
        return Result.Success(new EventAvailabilityResponse(eventId, seats));
    }
}

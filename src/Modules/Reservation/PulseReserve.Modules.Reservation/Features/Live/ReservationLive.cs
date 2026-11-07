using PulseReserve.Modules.Reservation.Persistence;

namespace PulseReserve.Modules.Reservation.Features.Live;

/// <summary>One seat of a reservation with its EFFECTIVE status right now (<c>Available</c>, <c>Held</c> or <c>Sold</c>).</summary>
public sealed record SeatLiveState(Guid SeatId, string Status);

/// <summary>
/// The authoritative current state of a reservation as the real-time layer needs it. Routing and content both come from
/// here (never from a database notification), so a client only ever receives what the database says at dispatch time.
/// </summary>
public sealed record ReservationLiveSnapshot(
    Guid ReservationId,
    Guid EventId,
    Guid CustomerId,
    string Status,
    DateTime ExpiresAtUtc,
    IReadOnlyList<SeatLiveState> Seats);

/// <summary>Public read surface of the module for the composition root's real-time dispatcher.</summary>
public sealed class ReservationLiveReader(IReservationLiveStore store)
{
    public Task<ReservationLiveSnapshot?> ReadAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
        store.ReadLiveSnapshotAsync(reservationId, cancellationToken);
}

/// <summary>
/// Proactively closes holds that ran out. Correctness never depends on it (an expired hold is already reported as expired and
/// its seats are already reclaimable); it exists so that "the hold ended" is a committed fact that clients can be told about.
/// </summary>
public sealed class HoldExpiryService(IReservationLiveStore store)
{
    public Task<int> ExpireDueAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "The batch size must be between 1 and 1000.");

        return store.ExpireDueHeldReservationsAsync(batchSize, cancellationToken);
    }
}

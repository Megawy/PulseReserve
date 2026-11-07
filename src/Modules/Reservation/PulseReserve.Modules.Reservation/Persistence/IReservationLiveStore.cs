using PulseReserve.Modules.Reservation.Features.Live;

namespace PulseReserve.Modules.Reservation.Persistence;

/// <summary>
/// Persistence operations behind real-time updates and proactive hold expiry. They are kept apart from
/// <see cref="IReservationStore"/> because nothing in the booking path depends on them: lazy expiry already keeps
/// inventory correct, and these only make the outcome visible (and proactive) for connected clients.
/// </summary>
public interface IReservationLiveStore
{
    /// <summary>
    /// The CURRENT state of a reservation and of each of its seats, read from the database at call time with the database
    /// clock (a hold past its expiry reads as Expired and its seats as Available). Never derived from a notification payload,
    /// so a stale or reordered signal can only cause a harmless extra read. <c>null</c> when the reservation does not exist.
    /// </summary>
    Task<ReservationLiveSnapshot?> ReadLiveSnapshotAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes up to <paramref name="limit"/> reservations that are still <c>Held</c> and past their expiry (database clock) and
    /// returns their seats to the pool, in one statement under <c>FOR UPDATE SKIP LOCKED</c>. This is exactly what lazy expiry
    /// already reports; doing it proactively lets the status change commit (and therefore be announced) when the hold actually
    /// ends. <c>Checkout</c> reservations are left to the payment workflow, which owns their order. Returns the number closed.
    /// </summary>
    Task<int> ExpireDueHeldReservationsAsync(int limit, CancellationToken cancellationToken = default);
}

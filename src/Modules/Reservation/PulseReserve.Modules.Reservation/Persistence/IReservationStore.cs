using PulseReserve.Modules.Reservation.Features.Holds;
using PulseReserve.Modules.Reservation.Features.Inventory;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Reservation.Persistence;

/// <summary>
/// Persistence boundary for seat inventory and holds. Every state change is a single PostgreSQL transaction, and
/// seat ownership is decided by the database (row locks + conditional updates), never by in-memory checks.
/// </summary>
public interface IReservationStore
{
    Task<bool> InventoryExistsAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>Seeds the event's seat inventory. Idempotent and safe under concurrent callers.</summary>
    Task SeedInventoryAsync(Guid eventId, IReadOnlyList<SellableSeat> seats, CancellationToken cancellationToken = default);

    Task<ReservationResponse?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<HoldAttempt> HoldAsync(HoldRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the reservation with its effective status, or <c>null</c> if absent or owned by another customer.</summary>
    Task<ReservationResponse?> GetAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The customer's live reservation for the event (<c>Held</c> or <c>Checkout</c>, not past its expiry on the database clock), or
    /// <c>null</c>. Lets a customer find a hold made in another browser or after clearing local state; there is at most one per event.
    /// </summary>
    Task<ReservationResponse?> FindActiveAsync(Guid customerId, Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>Releases a held reservation. Idempotent; returns <c>null</c> if absent or owned by another customer.</summary>
    Task<ReservationResponse?> ReleaseAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeatAvailability>> GetAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically moves the customer's reservation from Held to Checkout. The expiry check and the transition happen
    /// under a row lock with the database clock, so a hold that expires first can never become payable.
    /// </summary>
    Task<BeginCheckoutResult> BeginCheckoutAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>Atomically converts a Checkout reservation into a Confirmed sale (seats Sold) unless it expired first.</summary>
    Task<ConfirmSaleResult> ConfirmSaleAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>Returns a Checkout reservation's seats to the pool (order cancelled). Idempotent; null if absent or not owned.</summary>
    Task<ReservationResponse?> ReleaseCheckoutAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default);
}

public enum BeginCheckoutOutcome
{
    /// <summary>Held -> Checkout happened now.</summary>
    Started,

    /// <summary>Already in Checkout and still live: resuming an interrupted or repeated request.</summary>
    AlreadyInCheckout,

    /// <summary>Absent or owned by someone else.</summary>
    NotFound,

    /// <summary>Released, Expired or Confirmed: cannot be checked out.</summary>
    NotActive
}

public sealed record BeginCheckoutResult(BeginCheckoutOutcome Outcome, ReservationResponse? Reservation, string? Status);

public enum ConfirmSaleOutcome
{
    Confirmed,
    AlreadyConfirmed,

    /// <summary>The database clock passed the hold expiry before confirmation: nothing was sold.</summary>
    Expired,

    /// <summary>Not in Checkout (released/never started) or its seats are no longer exclusively ours: nothing was sold.</summary>
    NotConfirmable
}

public sealed record ConfirmSaleResult(ConfirmSaleOutcome Outcome);

public sealed record HoldRequest(
    Guid ReservationId,
    Guid EventId,
    Guid CustomerId,
    IReadOnlyList<Guid> SeatIds,
    string IdempotencyKey,
    TimeSpan HoldDuration);

public sealed record SeatAvailability(Guid SeatId, string Status, decimal PriceAmount, string Currency);

/// <summary>Outcome of a hold attempt. Exactly one of <see cref="Reservation"/> or <see cref="Error"/> is set.</summary>
public sealed record HoldAttempt(
    ReservationResponse? Reservation,
    bool IsReplay,
    Error? Error,
    IReadOnlyList<Guid> UnavailableSeatIds)
{
    public static HoldAttempt Created(ReservationResponse reservation) => new(reservation, false, null, []);

    public static HoldAttempt Replayed(ReservationResponse reservation) => new(reservation, true, null, []);

    public static HoldAttempt Failed(Error error) => new(null, false, error, []);

    public static HoldAttempt Unavailable(IReadOnlyList<Guid> seatIds) =>
        new(null, false, Domain.ReservationErrors.SeatsUnavailable, seatIds);
}

using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Reservation.Domain;

public static class ReservationErrors
{
    public static readonly Error EmptyCustomerId =
        new("Reservation.EmptyCustomerId", "A customer identifier is required.");

    public static readonly Error MissingIdempotencyKey =
        new("Reservation.MissingIdempotencyKey", "An Idempotency-Key header is required to hold seats.");

    public static readonly Error IdempotencyKeyTooLong =
        new("Reservation.IdempotencyKeyTooLong", $"The Idempotency-Key must be at most {ReservationPolicy.MaxIdempotencyKeyLength} characters.");

    public static readonly Error NoSeats =
        new("Reservation.NoSeats", "At least one seat must be requested.");

    public static readonly Error TooManySeats =
        new("Reservation.TooManySeats", $"At most {ReservationPolicy.MaxSeatsPerHold} seats can be held at once.");

    public static readonly Error InvalidSeatIds =
        new("Reservation.InvalidSeatIds", "Seat identifiers must be non-empty and distinct.");

    public static readonly Error UnknownSeats =
        new("Reservation.UnknownSeats", "One or more requested seats do not exist for this event.");

    public static readonly Error MixedCurrency =
        new("Reservation.MixedCurrency", "The requested seats are priced in different currencies and cannot be held together.");

    public static readonly Error SeatsUnavailable =
        Error.Conflict("Reservation.SeatsUnavailable", "One or more requested seats are no longer available.");

    public static readonly Error ActiveHoldExists =
        Error.Conflict("Reservation.ActiveHoldExists", "The customer already holds seats for this event. Release that hold first.");

    public static readonly Error IdempotencyKeyReused =
        Error.Conflict("Reservation.IdempotencyKeyReused", "The Idempotency-Key was already used for a different request.");

    public static readonly Error SalesNotOpen =
        Error.Conflict("Reservation.SalesNotOpen", "Seats can only be held while the event is on sale.");

    public static Error EventNotFound(Guid eventId) =>
        Error.NotFound("Event.NotFound", $"Event with ID '{eventId}' was not found.");

    public static Error NotFound(Guid reservationId) =>
        Error.NotFound("Reservation.NotFound", $"Reservation with ID '{reservationId}' was not found.");

    /// <summary>The signed-in customer has no live hold (or checkout) for the event. Not an error condition for the caller.</summary>
    public static readonly Error NoActiveHold =
        Error.NotFound("Reservation.NoActiveHold", "The customer has no active hold for this event.");
}

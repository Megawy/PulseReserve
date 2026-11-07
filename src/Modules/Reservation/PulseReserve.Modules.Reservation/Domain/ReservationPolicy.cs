namespace PulseReserve.Modules.Reservation.Domain;

/// <summary>
/// Reservation lifecycle states persisted in <c>reservation.reservations.status</c>.
/// A <see cref="Held"/> or <see cref="Checkout"/> reservation whose expiry has passed is reported as
/// <see cref="Expired"/> even before a write marks it so (lazy expiry); its seats are reclaimable by other customers
/// from that moment on. Lifecycle: Held -> Checkout (an order exists, same expiry, not renewed) -> Confirmed (paid,
/// seats Sold); Held/Checkout -> Released (customer) or Expired (database clock).
/// </summary>
public static class ReservationStatus
{
    public const string Held = "Held";
    public const string Released = "Released";
    public const string Expired = "Expired";

    /// <summary>An order is pending payment. The seats stay Held until the ORIGINAL expiry; the hold is not extended.</summary>
    public const string Checkout = "Checkout";

    /// <summary>Paid. Terminal: the seats are Sold.</summary>
    public const string Confirmed = "Confirmed";
}

/// <summary>Seat states persisted in <c>reservation.event_seats.status</c>.</summary>
public static class SeatStatus
{
    public const string Available = "Available";
    public const string Held = "Held";

    /// <summary>Permanently sold through a confirmed order. Never reclaimed by expiry.</summary>
    public const string Sold = "Sold";
}

/// <summary>Business limits for seat holds.</summary>
public static class ReservationPolicy
{
    public const int MaxSeatsPerHold = 8;
    public const int MaxIdempotencyKeyLength = 100;

    /// <summary>Fixed, non-renewable hold duration (architecture contract 01: 600 seconds).</summary>
    public static readonly TimeSpan DefaultHoldDuration = TimeSpan.FromSeconds(600);
}

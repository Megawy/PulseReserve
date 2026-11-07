using PulseReserve.Modules.Reservation.Domain;

namespace PulseReserve.Modules.Reservation;

public sealed class ReservationOptions
{
    /// <summary>How long a hold reserves its seats. Fixed per hold; holds are never renewed.</summary>
    public TimeSpan HoldDuration { get; set; } = ReservationPolicy.DefaultHoldDuration;
}

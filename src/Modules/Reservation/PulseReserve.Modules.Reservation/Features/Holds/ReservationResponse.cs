namespace PulseReserve.Modules.Reservation.Features.Holds;

public sealed record ReservationResponse(
    Guid ReservationId,
    Guid EventId,
    Guid CustomerId,
    string Status,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    decimal TotalAmount,
    string Currency,
    IReadOnlyList<ReservedSeatResponse> Seats);

public sealed record ReservedSeatResponse(Guid SeatId, decimal PriceAmount, string Currency);

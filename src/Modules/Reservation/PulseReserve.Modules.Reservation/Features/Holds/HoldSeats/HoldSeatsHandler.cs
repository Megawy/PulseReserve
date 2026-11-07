using PulseReserve.Modules.Reservation.Domain;
using PulseReserve.Modules.Reservation.Features.Inventory;
using PulseReserve.Modules.Reservation.Persistence;

namespace PulseReserve.Modules.Reservation.Features.Holds.HoldSeats;

public sealed record HoldSeatsCommand(
    Guid EventId,
    Guid CustomerId,
    IReadOnlyList<Guid> SeatIds,
    string? IdempotencyKey);

public sealed class HoldSeatsHandler(
    EventInventoryProvisioner provisioner,
    IReservationStore store,
    ReservationOptions options)
{
    public async Task<HoldAttempt> HandleAsync(HoldSeatsCommand command, CancellationToken cancellationToken = default)
    {
        var validation = Validate(command);
        if (validation is not null)
            return HoldAttempt.Failed(validation);

        var key = command.IdempotencyKey!.Trim();

        // A retry of an already-completed request returns the original reservation, even if sales have since closed.
        var existing = await store.FindByIdempotencyKeyAsync(command.CustomerId, key, cancellationToken);
        if (existing is not null)
            return SameRequest(existing, command) ? HoldAttempt.Replayed(existing) : HoldAttempt.Failed(ReservationErrors.IdempotencyKeyReused);

        var sellable = await provisioner.EnsureSellableAsync(command.EventId, cancellationToken);
        if (sellable.IsFailure)
            return HoldAttempt.Failed(sellable.Error);

        return await store.HoldAsync(
            new HoldRequest(Guid.NewGuid(), command.EventId, command.CustomerId, command.SeatIds, key, options.HoldDuration),
            cancellationToken);
    }

    internal static bool SameRequest(ReservationResponse reservation, HoldSeatsCommand command) =>
        reservation.EventId == command.EventId &&
        reservation.Seats.Select(s => s.SeatId).ToHashSet().SetEquals(command.SeatIds);

    private static SharedKernel.Error? Validate(HoldSeatsCommand command)
    {
        if (command.CustomerId == Guid.Empty)
            return ReservationErrors.EmptyCustomerId;

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return ReservationErrors.MissingIdempotencyKey;

        if (command.IdempotencyKey.Trim().Length > ReservationPolicy.MaxIdempotencyKeyLength)
            return ReservationErrors.IdempotencyKeyTooLong;

        if (command.SeatIds is null || command.SeatIds.Count == 0)
            return ReservationErrors.NoSeats;

        if (command.SeatIds.Count > ReservationPolicy.MaxSeatsPerHold)
            return ReservationErrors.TooManySeats;

        if (command.SeatIds.Any(id => id == Guid.Empty) || command.SeatIds.Distinct().Count() != command.SeatIds.Count)
            return ReservationErrors.InvalidSeatIds;

        return null;
    }
}

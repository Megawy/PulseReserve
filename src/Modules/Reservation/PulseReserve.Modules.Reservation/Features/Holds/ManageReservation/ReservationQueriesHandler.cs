using PulseReserve.Modules.Reservation.Domain;
using PulseReserve.Modules.Reservation.Persistence;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Reservation.Features.Holds.ManageReservation;

public sealed record GetReservationQuery(Guid ReservationId, Guid CustomerId);

public sealed record GetActiveReservationQuery(Guid EventId, Guid CustomerId);

public sealed record ReleaseReservationCommand(Guid ReservationId, Guid CustomerId);

/// <summary>
/// Reads and releases reservations. A reservation owned by another customer is reported as not found, so its
/// existence is not disclosed. Release is idempotent: releasing an already released or expired hold succeeds
/// and returns its current state.
/// </summary>
public sealed class ReservationQueriesHandler(IReservationStore store)
{
    public async Task<Result<ReservationResponse>> GetAsync(GetReservationQuery query, CancellationToken cancellationToken = default)
    {
        if (query.CustomerId == Guid.Empty)
            return Result.Failure<ReservationResponse>(ReservationErrors.EmptyCustomerId);

        var reservation = await store.GetAsync(query.ReservationId, query.CustomerId, cancellationToken);
        return reservation is null
            ? Result.Failure<ReservationResponse>(ReservationErrors.NotFound(query.ReservationId))
            : Result.Success(reservation);
    }

    /// <summary>The customer's own live hold for an event. Another customer's hold is never visible: the query is scoped to the caller.</summary>
    public async Task<Result<ReservationResponse>> GetActiveAsync(GetActiveReservationQuery query, CancellationToken cancellationToken = default)
    {
        if (query.CustomerId == Guid.Empty)
            return Result.Failure<ReservationResponse>(ReservationErrors.EmptyCustomerId);
        if (query.EventId == Guid.Empty)
            return Result.Failure<ReservationResponse>(ReservationErrors.NoActiveHold);

        var reservation = await store.FindActiveAsync(query.CustomerId, query.EventId, cancellationToken);
        return reservation is null
            ? Result.Failure<ReservationResponse>(ReservationErrors.NoActiveHold)
            : Result.Success(reservation);
    }

    public async Task<Result<ReservationResponse>> ReleaseAsync(ReleaseReservationCommand command, CancellationToken cancellationToken = default)
    {
        if (command.CustomerId == Guid.Empty)
            return Result.Failure<ReservationResponse>(ReservationErrors.EmptyCustomerId);

        var reservation = await store.ReleaseAsync(command.ReservationId, command.CustomerId, cancellationToken);
        return reservation is null
            ? Result.Failure<ReservationResponse>(ReservationErrors.NotFound(command.ReservationId))
            : Result.Success(reservation);
    }
}

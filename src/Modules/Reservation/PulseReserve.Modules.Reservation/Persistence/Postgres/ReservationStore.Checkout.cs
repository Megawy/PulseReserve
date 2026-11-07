using PulseReserve.Modules.Reservation.Domain;

namespace PulseReserve.Modules.Reservation.Persistence.Postgres;

/// <summary>
/// Checkout transitions of a reservation. Every one runs in a single transaction that first takes the reservation's
/// row lock (<c>FOR UPDATE</c>), the same lock release, expiry closing and checkout take, and decides expiry with the
/// database clock (<c>clock_timestamp()</c>, the real time of the statement, not the transaction start). So whichever
/// of "checkout", "payment confirmation" and "expiry" gets the lock first wins, and the others see the result.
/// </summary>
internal sealed partial class ReservationStore
{
    public async Task<BeginCheckoutResult> BeginCheckoutAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default)
    {
        BeginCheckoutOutcome outcome;
        string status;

        await using (var tx = await BeginAsync(cancellationToken))
        {
            string stored;
            bool live;
            await using (var cmd = await CommandAsync(
                """
                SELECT status, expires_at_utc > clock_timestamp()
                FROM reservation.reservations
                WHERE id = @id AND customer_id = @customer_id
                FOR UPDATE
                """, cancellationToken))
            {
                cmd.Parameters.AddWithValue("id", reservationId);
                cmd.Parameters.AddWithValue("customer_id", customerId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return new BeginCheckoutResult(BeginCheckoutOutcome.NotFound, null, null);
                stored = reader.GetString(0);
                live = reader.GetBoolean(1);
            }

            if ((stored == ReservationStatus.Held || stored == ReservationStatus.Checkout) && !live)
            {
                // Lost the race against expiry: close it (same effect as lazy expiry) so nothing payable is created.
                await CloseExpiredAsync(reservationId, cancellationToken);
                outcome = BeginCheckoutOutcome.NotActive;
                status = ReservationStatus.Expired;
            }
            else if (stored == ReservationStatus.Held)
            {
                await using var move = await CommandAsync(
                    "UPDATE reservation.reservations SET status = 'Checkout' WHERE id = @id AND status = 'Held'", cancellationToken);
                move.Parameters.AddWithValue("id", reservationId);
                await move.ExecuteNonQueryAsync(cancellationToken);
                outcome = BeginCheckoutOutcome.Started;
                status = ReservationStatus.Checkout;
            }
            else if (stored == ReservationStatus.Checkout)
            {
                outcome = BeginCheckoutOutcome.AlreadyInCheckout;
                status = stored;
            }
            else
            {
                outcome = BeginCheckoutOutcome.NotActive;
                status = stored;
            }

            await tx.CommitAsync(cancellationToken);
        }

        if (outcome == BeginCheckoutOutcome.NotActive)
            return new BeginCheckoutResult(outcome, null, status);

        return new BeginCheckoutResult(outcome, await ReadAsync(reservationId, customerId, cancellationToken), status);
    }

    public async Task<ConfirmSaleResult> ConfirmSaleAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        string stored;
        bool live;
        await using (var cmd = await CommandAsync(
            """
            SELECT status, expires_at_utc > clock_timestamp()
            FROM reservation.reservations
            WHERE id = @id
            FOR UPDATE
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", reservationId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new ConfirmSaleResult(ConfirmSaleOutcome.NotConfirmable);
            stored = reader.GetString(0);
            live = reader.GetBoolean(1);
        }

        if (stored == ReservationStatus.Confirmed)
            return new ConfirmSaleResult(ConfirmSaleOutcome.AlreadyConfirmed);

        if (stored != ReservationStatus.Checkout)
            return new ConfirmSaleResult(stored == ReservationStatus.Expired ? ConfirmSaleOutcome.Expired : ConfirmSaleOutcome.NotConfirmable);

        if (!live)
        {
            // The hold ran out before the payment completed: never sell. Close it and free the seats.
            await CloseExpiredAsync(reservationId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new ConfirmSaleResult(ConfirmSaleOutcome.Expired);
        }

        // Live and locked: sell exactly this reservation's seats, and only if every one is still exclusively ours.
        int sold, expected;
        await using (var cmd = await CommandAsync(
            """
            WITH locked AS (
                SELECT seat_id FROM reservation.event_seats
                WHERE reservation_id = @id AND status = 'Held' AND hold_expires_at_utc > clock_timestamp()
                ORDER BY seat_id
                FOR UPDATE),
            sold AS (
                UPDATE reservation.event_seats s
                SET status = 'Sold', hold_expires_at_utc = NULL
                FROM locked l
                WHERE s.reservation_id = @id AND s.seat_id = l.seat_id
                RETURNING s.seat_id)
            SELECT (SELECT count(*) FROM sold), (SELECT count(*) FROM reservation.reservation_items WHERE reservation_id = @id)
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", reservationId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            sold = (int)reader.GetInt64(0);
            expected = (int)reader.GetInt64(1);
        }

        if (sold != expected)
        {
            await tx.RollbackAsync(cancellationToken);
            return new ConfirmSaleResult(ConfirmSaleOutcome.NotConfirmable);
        }

        await using (var cmd = await CommandAsync(
            "UPDATE reservation.reservations SET status = 'Confirmed', closed_at_utc = now() WHERE id = @id", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", reservationId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new ConfirmSaleResult(ConfirmSaleOutcome.Confirmed);
    }

    public async Task<Features.Holds.ReservationResponse?> ReleaseCheckoutAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default)
    {
        await using (var tx = await BeginAsync(cancellationToken))
        {
            await using (var lockCmd = await CommandAsync(
                "SELECT 1 FROM reservation.reservations WHERE id = @id AND customer_id = @customer_id FOR UPDATE", cancellationToken))
            {
                lockCmd.Parameters.AddWithValue("id", reservationId);
                lockCmd.Parameters.AddWithValue("customer_id", customerId);
                if (await lockCmd.ExecuteScalarAsync(cancellationToken) is null)
                    return null;
            }

            await using (var cmd = await CommandAsync(
                """
                WITH closed AS (
                    UPDATE reservation.reservations
                    SET status = CASE WHEN expires_at_utc <= now() THEN 'Expired' ELSE 'Released' END, closed_at_utc = now()
                    WHERE id = @id AND status = 'Checkout'
                    RETURNING id)
                UPDATE reservation.event_seats s
                SET status = 'Available', reservation_id = NULL, hold_expires_at_utc = NULL
                WHERE s.reservation_id IN (SELECT id FROM closed) AND s.status = 'Held'
                """, cancellationToken))
            {
                cmd.Parameters.AddWithValue("id", reservationId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        return await ReadAsync(reservationId, customerId, cancellationToken);
    }

    private async Task CloseExpiredAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        await using var close = await CommandAsync(
            """
            WITH expired AS (
                UPDATE reservation.reservations SET status = 'Expired', closed_at_utc = now()
                WHERE id = @id AND status IN ('Held', 'Checkout') RETURNING id)
            UPDATE reservation.event_seats s
            SET status = 'Available', reservation_id = NULL, hold_expires_at_utc = NULL
            WHERE s.reservation_id IN (SELECT id FROM expired) AND s.status = 'Held'
            """, cancellationToken);
        close.Parameters.AddWithValue("id", reservationId);
        await close.ExecuteNonQueryAsync(cancellationToken);
    }
}

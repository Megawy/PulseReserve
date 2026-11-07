using PulseReserve.Modules.Reservation.Features.Live;

namespace PulseReserve.Modules.Reservation.Persistence.Postgres;

/// <summary>Reads and proactive expiry that back the real-time layer (see <see cref="IReservationLiveStore"/>).</summary>
internal sealed partial class ReservationStore : IReservationLiveStore
{
    public async Task<ReservationLiveSnapshot?> ReadLiveSnapshotAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        Guid eventId, customerId;
        string status;
        DateTime expiresAt;

        await using (var cmd = await CommandAsync(
            """
            SELECT event_id, customer_id,
                   CASE WHEN status IN ('Held', 'Checkout') AND expires_at_utc <= now() THEN 'Expired' ELSE status END,
                   expires_at_utc
            FROM reservation.reservations
            WHERE id = @id
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", reservationId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            eventId = reader.GetGuid(0);
            customerId = reader.GetGuid(1);
            status = reader.GetString(2);
            expiresAt = reader.GetFieldValue<DateTime>(3);
        }

        var seats = new List<SeatLiveState>();
        await using (var cmd = await CommandAsync(
            """
            SELECT s.seat_id,
                   CASE WHEN s.status = 'Held' AND s.hold_expires_at_utc <= now() THEN 'Available' ELSE s.status END
            FROM reservation.reservation_items i
            JOIN reservation.event_seats s ON s.event_id = i.event_id AND s.seat_id = i.seat_id
            WHERE i.reservation_id = @id
            ORDER BY s.seat_id
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", reservationId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                seats.Add(new SeatLiveState(reader.GetGuid(0), reader.GetString(1)));
        }

        return new ReservationLiveSnapshot(reservationId, eventId, customerId, status, expiresAt, seats);
    }

    public async Task<int> ExpireDueHeldReservationsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        int closed;
        await using (var cmd = await CommandAsync(
            """
            WITH due AS (
                SELECT id FROM reservation.reservations
                WHERE status = 'Held' AND expires_at_utc <= now()
                ORDER BY expires_at_utc
                LIMIT @limit
                FOR UPDATE SKIP LOCKED),
            expired AS (
                UPDATE reservation.reservations r
                SET status = 'Expired', closed_at_utc = now()
                FROM due
                WHERE r.id = due.id AND r.status = 'Held'
                RETURNING r.id),
            freed AS (
                UPDATE reservation.event_seats s
                SET status = 'Available', reservation_id = NULL, hold_expires_at_utc = NULL
                WHERE s.reservation_id IN (SELECT id FROM expired) AND s.status = 'Held'
                RETURNING s.seat_id)
            SELECT (SELECT count(*) FROM expired)
            """, cancellationToken))
        {
            // Seats that another reservation already reclaimed no longer reference the expired one and are left untouched.
            cmd.Parameters.AddWithValue("limit", limit);
            closed = (int)(long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        await tx.CommitAsync(cancellationToken);
        return closed;
    }
}

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using PulseReserve.Modules.Reservation.Domain;
using PulseReserve.Modules.Reservation.Features.Holds;
using PulseReserve.Modules.Reservation.Features.Inventory;

namespace PulseReserve.Modules.Reservation.Persistence.Postgres;

/// <summary>
/// PostgreSQL implementation of seat inventory and holds (ADR-009).
/// <list type="bullet">
/// <item>Each <c>event_seats</c> row is the single authoritative state cell for one seat of one event.</item>
/// <item>Holds lock the requested rows in seat-id order with <c>FOR UPDATE SKIP LOCKED</c> and update them in the same
/// statement, so a seat can be acquired by at most one transaction, concurrent holds never deadlock, and contended
/// requests fail fast instead of queueing. A hold is all-or-nothing.</item>
/// <item>Expiry is evaluated with the database clock (<c>now()</c>), never the application clock. Expired holds are
/// reclaimable immediately (lazy expiry); no background sweeper is required for correctness.</item>
/// </list>
/// </summary>
internal sealed partial class ReservationStore(ReservationDbContext db) : IReservationStore
{
    private const string UniqueViolation = "23505";
    private const string IdempotencyConstraint = "uq_reservations_customer_idempotency_key";
    private const string ActiveHoldConstraint = "uq_reservations_active_hold_per_customer_event";

    public async Task<bool> InventoryExistsAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync("SELECT EXISTS (SELECT 1 FROM reservation.event_inventories WHERE event_id = @event_id)", cancellationToken);
        cmd.Parameters.AddWithValue("event_id", eventId);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task SeedInventoryAsync(Guid eventId, IReadOnlyList<SellableSeat> seats, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        await using (var cmd = await CommandAsync(
            "INSERT INTO reservation.event_inventories (event_id, seat_count) VALUES (@event_id, @seat_count) ON CONFLICT (event_id) DO NOTHING",
            cancellationToken))
        {
            cmd.Parameters.AddWithValue("event_id", eventId);
            cmd.Parameters.AddWithValue("seat_count", seats.Count);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = await CommandAsync(
            """
            INSERT INTO reservation.event_seats (event_id, seat_id, status, price_amount, price_currency)
            SELECT @event_id, s.seat_id, 'Available', s.price_amount, s.price_currency
            FROM unnest(@seat_ids, @amounts, @currencies) AS s(seat_id, price_amount, price_currency)
            ON CONFLICT (event_id, seat_id) DO NOTHING
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("event_id", eventId);
            cmd.Parameters.Add(new NpgsqlParameter("seat_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seats.Select(s => s.SeatId).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("amounts", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = seats.Select(s => s.PriceAmount).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("currencies", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = seats.Select(s => s.Currency).ToArray() });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<ReservationResponse?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(
            "SELECT id FROM reservation.reservations WHERE customer_id = @customer_id AND idempotency_key = @key", cancellationToken);
        cmd.Parameters.AddWithValue("customer_id", customerId);
        cmd.Parameters.AddWithValue("key", idempotencyKey);
        var id = await cmd.ExecuteScalarAsync(cancellationToken);
        return id is Guid reservationId ? await ReadAsync(reservationId, customerId, cancellationToken) : null;
    }

    public async Task<HoldAttempt> HoldAsync(HoldRequest request, CancellationToken cancellationToken = default)
    {
        var seatIds = request.SeatIds.ToArray();

        await using (var tx = await BeginAsync(cancellationToken))
        {
            // 1. Prices are an immutable OnSale snapshot, so they can be read without locks.
            var prices = await ReadSeatPricesAsync(request.EventId, seatIds, cancellationToken);
            if (prices.Count != seatIds.Length)
                return HoldAttempt.Failed(ReservationErrors.UnknownSeats);

            var currencies = prices.Values.Select(p => p.Currency).Distinct().ToList();
            if (currencies.Count != 1)
                return HoldAttempt.Failed(ReservationErrors.MixedCurrency);

            // 2. Close this customer's own expired holds for the event so the one-active-hold rule does not block them.
            await ExpireCustomerHoldsAsync(request.EventId, request.CustomerId, cancellationToken);

            // 3. Claim the idempotency key and the customer's active-hold slot before touching any seat.
            try
            {
                await InsertReservationAsync(request, prices.Values.Sum(p => p.Amount), currencies[0], cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
            {
                await tx.RollbackAsync(cancellationToken);
                return await ResolveUniqueViolationAsync(request, ex.ConstraintName, cancellationToken);
            }

            // 4. Atomically acquire every requested seat or none.
            var acquired = await AcquireSeatsAsync(request.EventId, request.ReservationId, seatIds, cancellationToken);
            if (acquired.Count != seatIds.Length)
            {
                await tx.RollbackAsync(cancellationToken);
                return HoldAttempt.Unavailable(seatIds.Where(id => !acquired.Contains(id)).Order().ToList());
            }

            // 5. Record the reserved seats with their snapshot prices.
            await InsertItemsAsync(request.ReservationId, request.EventId, seatIds, prices, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        var created = await ReadAsync(request.ReservationId, request.CustomerId, cancellationToken)
            ?? throw new InvalidOperationException($"Reservation '{request.ReservationId}' was committed but could not be read back.");
        return HoldAttempt.Created(created);
    }

    public Task<ReservationResponse?> GetAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
        ReadAsync(reservationId, customerId, cancellationToken);

    public async Task<ReservationResponse?> FindActiveAsync(Guid customerId, Guid eventId, CancellationToken cancellationToken = default)
    {
        object? found;
        await using (var cmd = await CommandAsync(
            """
            SELECT id
            FROM reservation.reservations
            WHERE customer_id = @customer_id AND event_id = @event_id
              AND status IN ('Held', 'Checkout') AND expires_at_utc > now()
            ORDER BY created_at_utc DESC
            LIMIT 1
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("customer_id", customerId);
            cmd.Parameters.AddWithValue("event_id", eventId);
            found = await cmd.ExecuteScalarAsync(cancellationToken);
        }

        return found is Guid reservationId ? await ReadAsync(reservationId, customerId, cancellationToken) : null;
    }

    public async Task<ReservationResponse?> ReleaseAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default)
    {
        await using (var tx = await BeginAsync(cancellationToken))
        {
            string? status;
            await using (var cmd = await CommandAsync(
                """
                SELECT CASE WHEN status IN ('Held', 'Checkout') AND expires_at_utc <= now() THEN 'Expired' ELSE status END
                FROM reservation.reservations
                WHERE id = @id AND customer_id = @customer_id
                FOR UPDATE
                """, cancellationToken))
            {
                cmd.Parameters.AddWithValue("id", reservationId);
                cmd.Parameters.AddWithValue("customer_id", customerId);
                status = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
            }

            if (status is null)
                return null;

            // Only a reservation still recorded as Held changes state (an expired Checkout is closed as Expired); a live
            // Checkout can only be ended by cancelling its order, and Released/Expired/Confirmed are terminal.
            await using (var cmd = await CommandAsync(
                """
                WITH closed AS (
                    UPDATE reservation.reservations
                    SET status = CASE WHEN expires_at_utc <= now() THEN 'Expired' ELSE 'Released' END,
                        closed_at_utc = now()
                    WHERE id = @id AND (status = 'Held' OR (status = 'Checkout' AND expires_at_utc <= now()))
                    RETURNING id)
                UPDATE reservation.event_seats s
                SET status = 'Available', reservation_id = NULL, hold_expires_at_utc = NULL
                WHERE s.reservation_id IN (SELECT id FROM closed) AND s.status = 'Held'
                """, cancellationToken))
            {
                // Seats lazily reclaimed by another reservation no longer reference this one and are left untouched.
                cmd.Parameters.AddWithValue("id", reservationId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        return await ReadAsync(reservationId, customerId, cancellationToken);
    }

    public async Task<IReadOnlyList<SeatAvailability>> GetAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(
            """
            SELECT seat_id,
                   CASE WHEN status = 'Held' AND hold_expires_at_utc <= now() THEN 'Available' ELSE status END,
                   price_amount, price_currency
            FROM reservation.event_seats
            WHERE event_id = @event_id
            ORDER BY seat_id
            """, cancellationToken);
        cmd.Parameters.AddWithValue("event_id", eventId);

        var seats = new List<SeatAvailability>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            seats.Add(new SeatAvailability(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3)));
        return seats;
    }

    private async Task<Dictionary<Guid, (decimal Amount, string Currency)>> ReadSeatPricesAsync(
        Guid eventId, Guid[] seatIds, CancellationToken cancellationToken)
    {
        await using var cmd = await CommandAsync(
            "SELECT seat_id, price_amount, price_currency FROM reservation.event_seats WHERE event_id = @event_id AND seat_id = ANY(@seat_ids)",
            cancellationToken);
        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.Add(new NpgsqlParameter("seat_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seatIds });

        var prices = new Dictionary<Guid, (decimal, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            prices[reader.GetGuid(0)] = (reader.GetDecimal(1), reader.GetString(2));
        return prices;
    }

    private async Task ExpireCustomerHoldsAsync(Guid eventId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var cmd = await CommandAsync(
            """
            WITH expired AS (
                UPDATE reservation.reservations
                SET status = 'Expired', closed_at_utc = now()
                WHERE event_id = @event_id AND customer_id = @customer_id AND status IN ('Held', 'Checkout') AND expires_at_utc <= now()
                RETURNING id)
            UPDATE reservation.event_seats s
            SET status = 'Available', reservation_id = NULL, hold_expires_at_utc = NULL
            WHERE s.reservation_id IN (SELECT id FROM expired) AND s.status = 'Held'
            """, cancellationToken);
        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.AddWithValue("customer_id", customerId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertReservationAsync(HoldRequest request, decimal total, string currency, CancellationToken cancellationToken)
    {
        await using var cmd = await CommandAsync(
            """
            INSERT INTO reservation.reservations
                (id, event_id, customer_id, status, idempotency_key, total_amount, currency, created_at_utc, expires_at_utc)
            VALUES (@id, @event_id, @customer_id, 'Held', @key, @total, @currency, now(), now() + @hold_duration)
            """, cancellationToken);
        cmd.Parameters.AddWithValue("id", request.ReservationId);
        cmd.Parameters.AddWithValue("event_id", request.EventId);
        cmd.Parameters.AddWithValue("customer_id", request.CustomerId);
        cmd.Parameters.AddWithValue("key", request.IdempotencyKey);
        cmd.Parameters.AddWithValue("total", total);
        cmd.Parameters.AddWithValue("currency", currency);
        cmd.Parameters.Add(new NpgsqlParameter("hold_duration", NpgsqlDbType.Interval) { Value = request.HoldDuration });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<HashSet<Guid>> AcquireSeatsAsync(Guid eventId, Guid reservationId, Guid[] seatIds, CancellationToken cancellationToken)
    {
        await using var cmd = await CommandAsync(
            """
            WITH candidate AS (
                SELECT seat_id
                FROM reservation.event_seats
                WHERE event_id = @event_id
                  AND seat_id = ANY(@seat_ids)
                  AND (status = 'Available' OR (status = 'Held' AND hold_expires_at_utc <= now()))
                ORDER BY seat_id
                FOR UPDATE SKIP LOCKED)
            UPDATE reservation.event_seats s
            SET status = 'Held',
                reservation_id = @reservation_id,
                hold_expires_at_utc = (SELECT expires_at_utc FROM reservation.reservations WHERE id = @reservation_id)
            FROM candidate c
            WHERE s.event_id = @event_id AND s.seat_id = c.seat_id
            RETURNING s.seat_id
            """, cancellationToken);
        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.AddWithValue("reservation_id", reservationId);
        cmd.Parameters.Add(new NpgsqlParameter("seat_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seatIds });

        var acquired = new HashSet<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            acquired.Add(reader.GetGuid(0));
        return acquired;
    }

    private async Task InsertItemsAsync(
        Guid reservationId, Guid eventId, Guid[] seatIds, Dictionary<Guid, (decimal Amount, string Currency)> prices, CancellationToken cancellationToken)
    {
        await using var cmd = await CommandAsync(
            """
            INSERT INTO reservation.reservation_items (reservation_id, event_id, seat_id, price_amount, price_currency)
            SELECT @reservation_id, @event_id, s.seat_id, s.price_amount, s.price_currency
            FROM unnest(@seat_ids, @amounts, @currencies) AS s(seat_id, price_amount, price_currency)
            """, cancellationToken);
        cmd.Parameters.AddWithValue("reservation_id", reservationId);
        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.Add(new NpgsqlParameter("seat_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seatIds });
        cmd.Parameters.Add(new NpgsqlParameter("amounts", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = seatIds.Select(id => prices[id].Amount).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("currencies", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = seatIds.Select(id => prices[id].Currency).ToArray() });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<HoldAttempt> ResolveUniqueViolationAsync(HoldRequest request, string? constraint, CancellationToken cancellationToken)
    {
        // A concurrent request with the same key committed first: answer exactly like a sequential retry would.
        var existing = await FindByIdempotencyKeyAsync(request.CustomerId, request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var sameRequest = existing.EventId == request.EventId &&
                              existing.Seats.Select(s => s.SeatId).ToHashSet().SetEquals(request.SeatIds);
            return sameRequest ? HoldAttempt.Replayed(existing) : HoldAttempt.Failed(ReservationErrors.IdempotencyKeyReused);
        }

        if (constraint == ActiveHoldConstraint)
            return HoldAttempt.Failed(ReservationErrors.ActiveHoldExists);

        throw new InvalidOperationException($"Unexpected unique constraint violation on '{constraint}'.");
    }

    private async Task<ReservationResponse?> ReadAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken)
    {
        Guid eventId;
        string status, currency;
        decimal total;
        DateTime createdAt, expiresAt;

        await using (var cmd = await CommandAsync(
            """
            SELECT event_id,
                   CASE WHEN status IN ('Held', 'Checkout') AND expires_at_utc <= now() THEN 'Expired' ELSE status END,
                   total_amount, currency, created_at_utc, expires_at_utc
            FROM reservation.reservations
            WHERE id = @id AND customer_id = @customer_id
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", reservationId);
            cmd.Parameters.AddWithValue("customer_id", customerId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            eventId = reader.GetGuid(0);
            status = reader.GetString(1);
            total = reader.GetDecimal(2);
            currency = reader.GetString(3);
            createdAt = reader.GetFieldValue<DateTime>(4);
            expiresAt = reader.GetFieldValue<DateTime>(5);
        }

        var seats = new List<ReservedSeatResponse>();
        await using (var cmd = await CommandAsync(
            "SELECT seat_id, price_amount, price_currency FROM reservation.reservation_items WHERE reservation_id = @id ORDER BY seat_id",
            cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", reservationId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                seats.Add(new ReservedSeatResponse(reader.GetGuid(0), reader.GetDecimal(1), reader.GetString(2)));
        }

        return new ReservationResponse(reservationId, eventId, customerId, status, createdAt, expiresAt, total, currency, seats);
    }

    private async Task<IDbContextTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Bound any wait (e.g. the unique-index wait between concurrent holds of one customer) instead of hanging.
        await using var cmd = await CommandAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return tx;
    }

    private async Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        return cmd;
    }
}

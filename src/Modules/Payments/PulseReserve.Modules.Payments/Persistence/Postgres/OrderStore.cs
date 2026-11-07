using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.Modules.Payments.Persistence.Postgres;

/// <summary>
/// PostgreSQL implementation of orders and payment attempts. Mutating operations run in one transaction and take the
/// order's row lock first (<c>FOR UPDATE</c>), so cancel, payment initiation, payment completion and finalisation of
/// one order are serialised, while different orders never block each other. No transaction is ever held open while
/// waiting for a payment provider. Time comparisons (deadline) use the database clock.
/// </summary>
internal sealed class OrderStore(PaymentsDbContext db) : IOrderStore
{
    private const string UniqueViolation = "23505";
    private const string ReservationConstraint = "uq_orders_reservation";

    public Task<Order?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        ReadAsync("o.customer_id = @customer_id AND o.idempotency_key = @key",
            cmd =>
            {
                cmd.Parameters.AddWithValue("customer_id", customerId);
                cmd.Parameters.AddWithValue("key", idempotencyKey);
            }, cancellationToken);

    public Task<Order?> FindByReservationAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default) =>
        ReadAsync("o.reservation_id = @reservation_id AND o.customer_id = @customer_id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("reservation_id", reservationId);
                cmd.Parameters.AddWithValue("customer_id", customerId);
            }, cancellationToken);

    public async Task<IReadOnlyList<Order>> ListForCustomerAsync(Guid customerId, int limit, CancellationToken cancellationToken = default)
    {
        var ids = new List<Guid>();
        await using (var cmd = await CommandAsync(
            "SELECT id FROM payments.orders WHERE customer_id = @customer_id ORDER BY created_at_utc DESC LIMIT @limit", cancellationToken))
        {
            cmd.Parameters.AddWithValue("customer_id", customerId);
            cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetGuid(0));
        }

        var orders = new List<Order>();
        foreach (var id in ids)
            if (await GetAsync(id, customerId, cancellationToken) is { } order)
                orders.Add(order);
        return orders;
    }

    public Task<Order?> GetAsync(Guid orderId, Guid? customerId, CancellationToken cancellationToken = default) =>
        ReadAsync(customerId is null ? "o.id = @id" : "o.id = @id AND o.customer_id = @customer_id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", orderId);
                if (customerId is { } owner)
                    cmd.Parameters.AddWithValue("customer_id", owner);
            }, cancellationToken);

    public async Task<CreateOrderOutcome> TryCreateAsync(NewOrder order, CancellationToken cancellationToken = default)
    {
        var snapshot = order.Snapshot;
        var seatIds = snapshot.Lines.Select(l => l.SeatId).ToArray();
        var prices = snapshot.Lines.Select(l => l.UnitPrice).ToArray();
        var total = prices.Sum(); // decimal arithmetic only
        var deadline = DateTime.SpecifyKind(snapshot.ExpiresAtUtc, DateTimeKind.Utc);

        await using var tx = await BeginAsync(cancellationToken);
        try
        {
            await using (var cmd = await CommandAsync(
                """
                INSERT INTO payments.orders
                    (id, customer_id, reservation_id, event_id, idempotency_key, status, currency, total_amount,
                     payment_deadline_utc, created_at_utc, updated_at_utc)
                VALUES (@id, @customer_id, @reservation_id, @event_id, @key, 'PendingPayment', @currency, @total,
                        @deadline, now(), now())
                """, cancellationToken))
            {
                cmd.Parameters.AddWithValue("id", order.OrderId);
                cmd.Parameters.AddWithValue("customer_id", order.CustomerId);
                cmd.Parameters.AddWithValue("reservation_id", snapshot.ReservationId);
                cmd.Parameters.AddWithValue("event_id", snapshot.EventId);
                cmd.Parameters.AddWithValue("key", order.IdempotencyKey);
                cmd.Parameters.AddWithValue("currency", snapshot.Currency);
                cmd.Parameters.Add(new NpgsqlParameter("total", NpgsqlDbType.Numeric) { Value = total });
                cmd.Parameters.Add(new NpgsqlParameter("deadline", NpgsqlDbType.TimestampTz) { Value = deadline });
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var cmd = await CommandAsync(
                """
                INSERT INTO payments.order_items (order_id, seat_id, quantity, unit_price_amount, line_total_amount, currency)
                SELECT @order_id, s.seat_id, 1, s.price, s.price, @currency
                FROM unnest(@seat_ids, @prices) AS s(seat_id, price)
                """, cancellationToken))
            {
                cmd.Parameters.AddWithValue("order_id", order.OrderId);
                cmd.Parameters.AddWithValue("currency", snapshot.Currency);
                cmd.Parameters.Add(new NpgsqlParameter("seat_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seatIds });
                cmd.Parameters.Add(new NpgsqlParameter("prices", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = prices });
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return CreateOrderOutcome.Created;
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            await tx.RollbackAsync(cancellationToken);
            return ex.ConstraintName == ReservationConstraint
                ? CreateOrderOutcome.ReservationAlreadyHasOrder
                : CreateOrderOutcome.KeyAlreadyUsed;
        }
    }

    public async Task<CancelOrderResult> CancelAsync(Guid orderId, Guid customerId, CancellationToken cancellationToken = default)
    {
        await using (var tx = await BeginAsync(cancellationToken))
        {
            string status;
            bool overdue;
            await using (var cmd = await CommandAsync(
                "SELECT status, payment_deadline_utc <= now() FROM payments.orders WHERE id = @id AND customer_id = @customer_id FOR UPDATE",
                cancellationToken))
            {
                cmd.Parameters.AddWithValue("id", orderId);
                cmd.Parameters.AddWithValue("customer_id", customerId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return new CancelOrderResult(CancelOrderOutcome.NotFound, null);
                status = reader.GetString(0);
                overdue = reader.GetBoolean(1);
            }

            if (status is OrderStatus.Paid or OrderStatus.RefundRequired)
                return new CancelOrderResult(CancelOrderOutcome.NotCancellable, null);

            if (status == OrderStatus.PendingPayment)
            {
                // An overdue order simply closes as Expired; otherwise the customer's cancel wins.
                await using (var cmd = await CommandAsync(
                    "UPDATE payments.orders SET status = @status, updated_at_utc = now() WHERE id = @id AND status = 'PendingPayment'",
                    cancellationToken))
                {
                    cmd.Parameters.AddWithValue("id", orderId);
                    cmd.Parameters.AddWithValue("status", overdue ? OrderStatus.Expired : OrderStatus.Cancelled);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // An in-flight attempt is abandoned; a late provider success is still recorded and ends as RefundRequired.
                await using (var cmd = await CommandAsync(
                    "UPDATE payments.payment_attempts SET status = 'Cancelled', completed_at_utc = now(), checkout_url = NULL WHERE order_id = @id AND status = 'Initiated'",
                    cancellationToken))
                {
                    cmd.Parameters.AddWithValue("id", orderId);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await tx.CommitAsync(cancellationToken);
        }

        return new CancelOrderResult(CancelOrderOutcome.Cancelled, await GetAsync(orderId, customerId, cancellationToken));
    }

    public async Task<BeginPaymentResult> BeginPaymentAsync(
        Guid orderId, Guid customerId, string idempotencyKey, string provider, bool isSimulated, CancellationToken cancellationToken = default)
    {
        Guid attemptId;
        bool replay = false;

        await using (var tx = await BeginAsync(cancellationToken))
        {
            string status;
            bool live;
            await using (var cmd = await CommandAsync(
                "SELECT status, payment_deadline_utc > now() FROM payments.orders WHERE id = @id AND customer_id = @customer_id FOR UPDATE",
                cancellationToken))
            {
                cmd.Parameters.AddWithValue("id", orderId);
                cmd.Parameters.AddWithValue("customer_id", customerId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return new BeginPaymentResult(BeginPaymentOutcome.OrderNotFound, null, null);
                status = reader.GetString(0);
                live = reader.GetBoolean(1);
            }

            // The same key always answers with the same attempt, even if the order has moved on since.
            await using (var cmd = await CommandAsync(
                "SELECT id FROM payments.payment_attempts WHERE order_id = @order_id AND idempotency_key = @key", cancellationToken))
            {
                cmd.Parameters.AddWithValue("order_id", orderId);
                cmd.Parameters.AddWithValue("key", idempotencyKey);
                if (await cmd.ExecuteScalarAsync(cancellationToken) is Guid existing)
                {
                    await tx.CommitAsync(cancellationToken);
                    var order = await GetAsync(orderId, customerId, cancellationToken);
                    return new BeginPaymentResult(BeginPaymentOutcome.Replay, order, await ReadAttemptAsync(existing, cancellationToken));
                }
            }

            if (status != OrderStatus.PendingPayment || !live)
                return new BeginPaymentResult(BeginPaymentOutcome.NotPayable, null, null);

            // An order that ever had a payment exception (a verified success for one of its failed attempts) is never paid
            // again, whether the exception is still open or an administrator has since recorded the money as returned:
            // another attempt could be charged while a withdrawn one is still payable at the provider, and two applied
            // payments for one order is exactly what must not happen. The customer starts a new order instead. Checked under the
            // order lock, like the exception insert, so the two can never interleave.
            await using (var cmd = await CommandAsync(
                "SELECT EXISTS (SELECT 1 FROM payments.payment_exceptions WHERE order_id = @order_id)", cancellationToken))
            {
                cmd.Parameters.AddWithValue("order_id", orderId);
                if ((bool)(await cmd.ExecuteScalarAsync(cancellationToken))!)
                    return new BeginPaymentResult(BeginPaymentOutcome.HeldForReview, null, null);
            }

            bool inFlight;
            await using (var cmd = await CommandAsync(
                "SELECT EXISTS (SELECT 1 FROM payments.payment_attempts WHERE order_id = @order_id AND status = 'Initiated')", cancellationToken))
            {
                cmd.Parameters.AddWithValue("order_id", orderId);
                inFlight = (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            }

            if (inFlight)
                return new BeginPaymentResult(BeginPaymentOutcome.InProgress, null, null);

            attemptId = Guid.NewGuid();
            await using (var cmd = await CommandAsync(
                """
                INSERT INTO payments.payment_attempts
                    (id, order_id, attempt_number, idempotency_key, status, provider, is_simulated, created_at_utc)
                VALUES (@id, @order_id,
                        (SELECT COALESCE(MAX(attempt_number), 0) + 1 FROM payments.payment_attempts WHERE order_id = @order_id),
                        @key, 'Initiated', @provider, @simulated, now())
                """, cancellationToken))
            {
                cmd.Parameters.AddWithValue("id", attemptId);
                cmd.Parameters.AddWithValue("order_id", orderId);
                cmd.Parameters.AddWithValue("key", idempotencyKey);
                cmd.Parameters.AddWithValue("provider", provider);
                cmd.Parameters.AddWithValue("simulated", isSimulated);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        var created = await GetAsync(orderId, customerId, cancellationToken);
        return new BeginPaymentResult(replay ? BeginPaymentOutcome.Replay : BeginPaymentOutcome.Started, created, await ReadAttemptAsync(attemptId, cancellationToken));
    }

    public async Task SetProviderReferenceAsync(Guid attemptId, string providerReference, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(
            "UPDATE payments.payment_attempts SET provider_reference = @reference WHERE id = @id AND provider_reference IS NULL", cancellationToken);
        cmd.Parameters.AddWithValue("id", attemptId);
        cmd.Parameters.AddWithValue("reference", providerReference);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetProviderSessionAsync(Guid attemptId, string providerReference, string? checkoutUrl, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(
            // The reference is always stored (a later verified verdict must be able to find this attempt), but the hosted-page
            // link only while the attempt is still in flight: if it was cancelled or failed while the provider call was running
            // (customer cancel, expiry, a payment exception withdrawing it), the link - which can embed a client secret - is
            // never written. The status is evaluated on the latest committed row, so the two cannot interleave.
            """
            UPDATE payments.payment_attempts
            SET provider_reference = @reference, checkout_url = CASE WHEN status = 'Initiated' THEN @url ELSE NULL END
            WHERE id = @id AND provider_reference IS NULL
            """, cancellationToken);
        cmd.Parameters.AddWithValue("id", attemptId);
        cmd.Parameters.AddWithValue("reference", providerReference);
        cmd.Parameters.Add(new NpgsqlParameter("url", NpgsqlDbType.Varchar) { Value = (object?)checkoutUrl ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PaymentAttempt?> FindAttemptByKeyAsync(Guid orderId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(AttemptSelect + " WHERE order_id = @order_id AND idempotency_key = @key", cancellationToken);
        cmd.Parameters.AddWithValue("order_id", orderId);
        cmd.Parameters.AddWithValue("key", idempotencyKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAttempt(reader) : null;
    }

    public Task CancelUnreferencedAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        CloseUnreferencedAsync(attemptId, "provider_refused_initiation", cancellationToken);

    public async Task<bool> HasPaymentExceptionAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync("SELECT EXISTS (SELECT 1 FROM payments.payment_exceptions WHERE order_id = @id)", cancellationToken);
        cmd.Parameters.AddWithValue("id", orderId);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public Task CancelUncertainAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        CloseUnreferencedAsync(attemptId, "initiation_uncertain", cancellationToken);

    private async Task CloseUnreferencedAsync(Guid attemptId, string failureCode, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(cancellationToken); // so the transition context reaches the database
        await using var cmd = await CommandAsync(
            """
            UPDATE payments.payment_attempts
            SET status = 'Cancelled', failure_code = @code, completed_at_utc = now(), checkout_url = NULL
            WHERE id = @id AND status = 'Initiated' AND provider_reference IS NULL
            """, cancellationToken);
        cmd.Parameters.AddWithValue("id", attemptId);
        cmd.Parameters.AddWithValue("code", failureCode);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<RecordAttemptOutcome> RecordAttemptOutcomeAsync(PaymentCallback callback, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        await using (var cmd = await CommandAsync("SELECT 1 FROM payments.orders WHERE id = @id FOR UPDATE", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", callback.OrderId);
            if (await cmd.ExecuteScalarAsync(cancellationToken) is null)
                return RecordAttemptOutcome.NotFound;
        }

        string current;
        string? storedReference;
        await using (var cmd = await CommandAsync(
            "SELECT status, provider_reference FROM payments.payment_attempts WHERE id = @id AND order_id = @order_id FOR UPDATE", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", callback.AttemptId);
            cmd.Parameters.AddWithValue("order_id", callback.OrderId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return RecordAttemptOutcome.NotFound;
            current = reader.GetString(0);
            storedReference = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        // The verdict must name the exact attempt AND carry its unguessable provider reference.
        if (storedReference is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(storedReference), Encoding.UTF8.GetBytes(callback.ProviderReference ?? string.Empty)))
            return RecordAttemptOutcome.NotFound;

        string? next = (current, callback.Succeeded) switch
        {
            (PaymentAttemptStatus.Initiated, true) => PaymentAttemptStatus.Succeeded,
            (PaymentAttemptStatus.Initiated, false) => PaymentAttemptStatus.Failed,
            (PaymentAttemptStatus.Cancelled, true) => PaymentAttemptStatus.Succeeded, // late success: the money did move
            _ => null
        };

        if (next is null && current == PaymentAttemptStatus.Failed && callback.Succeeded)
        {
            // A verified success contradicts a Failed verdict. The verdict is evidence and stays exactly as recorded; the
            // contradiction is stored as an exception (facts copied from the attempt and the order under the locks held
            // here) in the same transaction, once per attempt. Nothing about the order, its reservation, tickets or refunds
            // changes: a person decides.
            int created;
            await SetReasonAsync(TransitionReason.LateSuccessRecorded, cancellationToken);
            await using (var cmd = await CommandAsync(
                """
                INSERT INTO payments.payment_exceptions
                    (id, kind, attempt_id, order_id, provider, is_simulated, provider_event_id, amount, currency,
                     original_attempt_status, original_failure_code, original_completed_at_utc, order_status_at_detection,
                     detected_at_utc, state)
                SELECT gen_random_uuid(), 'LateSuccessAfterFailedAttempt', a.id, o.id, a.provider, a.is_simulated, @event_id,
                       o.total_amount, o.currency, a.status, a.failure_code, a.completed_at_utc, o.status, now(), 'Open'
                FROM payments.payment_attempts a JOIN payments.orders o ON o.id = a.order_id
                WHERE a.id = @attempt_id AND a.order_id = @order_id AND a.status = 'Failed'
                ON CONFLICT (attempt_id, kind) DO NOTHING
                """, cancellationToken))
            {
                cmd.Parameters.AddWithValue("attempt_id", callback.AttemptId);
                cmd.Parameters.AddWithValue("order_id", callback.OrderId);
                cmd.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Varchar) { Value = (object?)Truncate(callback.ProviderEventId, 200) ?? DBNull.Value });
                created = await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (created == 1)
            {
                // Another attempt may already be in flight for this order (the customer started again after the failure).
                // Withdraw it so its payment link disappears: with this exception open the customer must not be charged a
                // second time. A verified success for such an attempt is still accepted later (as for any cancelled attempt),
                // and this exception then shows that order as paid by a different payment.
                await SetReasonAsync(TransitionReason.AttemptWithdrawnForReview, cancellationToken);
                await using var withdraw = await CommandAsync(
                    """
                    UPDATE payments.payment_attempts
                    SET status = 'Cancelled', failure_code = 'order_held_for_review', completed_at_utc = now(), checkout_url = NULL
                    WHERE order_id = @order_id AND id <> @attempt_id AND status = 'Initiated'
                    """, cancellationToken);
                withdraw.Parameters.AddWithValue("order_id", callback.OrderId);
                withdraw.Parameters.AddWithValue("attempt_id", callback.AttemptId);
                await withdraw.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return created == 1 ? RecordAttemptOutcome.LateSuccessRecorded : RecordAttemptOutcome.LateSuccessAlreadyRecorded;
        }

        if (next is null)
        {
            var same = (current == PaymentAttemptStatus.Succeeded && callback.Succeeded) ||
                       (current is PaymentAttemptStatus.Failed or PaymentAttemptStatus.Cancelled && !callback.Succeeded);
            if (same)
                return RecordAttemptOutcome.AlreadyRecordedSame;

            // A verified failure for an attempt that already holds a different verdict: nothing changes, but the rejected event is
            // kept in the transition history (once per provider event) for incident investigation.
            await using (var rejected = await CommandAsync(TransitionSql.InsertRejected, cancellationToken))
            {
                TransitionSql.Bind(rejected);
                rejected.Parameters.AddWithValue("id", callback.AttemptId);
                rejected.Parameters.Add(new NpgsqlParameter("attempted", NpgsqlDbType.Varchar) { Value = callback.Succeeded ? PaymentAttemptStatus.Succeeded : PaymentAttemptStatus.Failed });
                await rejected.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return RecordAttemptOutcome.ConflictingOutcome;
        }

        await using (var cmd = await CommandAsync(
            """
            UPDATE payments.payment_attempts
            SET status = @status, failure_code = @failure, completed_at_utc = now(), checkout_url = NULL
            WHERE id = @id
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", callback.AttemptId);
            cmd.Parameters.AddWithValue("status", next);
            cmd.Parameters.Add(new NpgsqlParameter("failure", NpgsqlDbType.Varchar) { Value = (object?)Truncate(callback.FailureCode) ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return RecordAttemptOutcome.Recorded;
    }

    public async Task FinalizeAfterSaleAsync(Guid orderId, bool sold, CancellationToken cancellationToken = default)
    {
        // One transaction: the order's final state and the durable follow-up work appear together or not at all.
        // Paid + its outbox events (tickets, notification), or RefundRequired + its refund record. A crash can
        // therefore never leave a paid order without recoverable post-payment work, nor an owed refund without a record.
        await using var tx = await BeginAsync(cancellationToken);

        bool changed;
        await using (var cmd = await CommandAsync(
            """
            UPDATE payments.orders
            SET status = @status, updated_at_utc = now()
            WHERE id = @id AND status IN ('PendingPayment', 'Cancelled', 'Expired')
            RETURNING id
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            cmd.Parameters.AddWithValue("status", sold ? OrderStatus.Paid : OrderStatus.RefundRequired);
            changed = await cmd.ExecuteScalarAsync(cancellationToken) is not null;
        }

        if (changed && sold)
        {
            await using var cmd = await CommandAsync(
                """
                INSERT INTO payments.outbox_events (id, type, aggregate_id, state, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc)
                VALUES (gen_random_uuid(), 'IssueTickets', @id, 'Pending', 0, now(), now(), now()),
                       (gen_random_uuid(), 'NotifyOrderPaid', @id, 'Pending', 0, now(), now(), now())
                ON CONFLICT (type, aggregate_id) DO NOTHING
                """, cancellationToken);
            cmd.Parameters.AddWithValue("id", orderId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (changed)
        {
            await using var cmd = await CommandAsync(RefundSql.InsertForOrder, cancellationToken);
            cmd.Parameters.AddWithValue("order_id", orderId);
            cmd.Parameters.AddWithValue("reason", RefundReason.PaymentAfterSaleFailed);
            cmd.Parameters.Add(new NpgsqlParameter("requested_by", NpgsqlDbType.Uuid) { Value = DBNull.Value });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    // ───────────── reads ─────────────

    private async Task<Order?> ReadAsync(string where, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        Guid orderId, reservationId, eventId, customerId;
        string status, currency;
        decimal total;
        DateTime createdAt, deadline;

        await using (var cmd = await CommandAsync(
            $"""
             SELECT o.id, o.reservation_id, o.event_id, o.customer_id,
                    CASE WHEN o.status = 'PendingPayment' AND o.payment_deadline_utc <= now() THEN 'Expired' ELSE o.status END,
                    o.currency, o.total_amount, o.created_at_utc, o.payment_deadline_utc
             FROM payments.orders o
             WHERE {where}
             """, cancellationToken))
        {
            bind(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            orderId = reader.GetGuid(0);
            reservationId = reader.GetGuid(1);
            eventId = reader.GetGuid(2);
            customerId = reader.GetGuid(3);
            status = reader.GetString(4);
            currency = reader.GetString(5);
            total = reader.GetDecimal(6);
            createdAt = reader.GetFieldValue<DateTime>(7);
            deadline = reader.GetFieldValue<DateTime>(8);
        }

        var lines = new List<OrderLine>();
        await using (var cmd = await CommandAsync(
            "SELECT seat_id, quantity, unit_price_amount, currency, line_total_amount FROM payments.order_items WHERE order_id = @id ORDER BY seat_id",
            cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                lines.Add(new OrderLine(reader.GetGuid(0), reader.GetInt32(1), reader.GetDecimal(2), reader.GetString(3), reader.GetDecimal(4)));
        }

        PaymentAttempt? latest = null;
        await using (var cmd = await CommandAsync(
            AttemptSelect + " WHERE order_id = @id ORDER BY attempt_number DESC LIMIT 1", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                latest = MapAttempt(reader);
        }

        return new Order(orderId, reservationId, eventId, customerId, status, currency, total, createdAt, deadline, lines, latest);
    }

    private const string AttemptSelect =
        "SELECT id, attempt_number, status, provider, is_simulated, provider_reference, created_at_utc, completed_at_utc, checkout_url FROM payments.payment_attempts";

    private async Task<PaymentAttempt?> ReadAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        await using var cmd = await CommandAsync(AttemptSelect + " WHERE id = @id", cancellationToken);
        cmd.Parameters.AddWithValue("id", attemptId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAttempt(reader) : null;
    }

    private static PaymentAttempt MapAttempt(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<DateTime>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTime>(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    /// <summary>Changes the transaction-local reason code for the statements that follow (the history trigger reads it).</summary>
    private async Task SetReasonAsync(string reason, CancellationToken cancellationToken)
    {
        await using var cmd = await CommandAsync("SELECT set_config('pulsereserve.reason', @reason, true)", cancellationToken);
        cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = reason });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Truncate(string? value) => value is { Length: > 100 } ? value[..100] : value;

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private async Task<IDbContextTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var cmd = await CommandAsync("SET LOCAL lock_timeout = '5s'", cancellationToken))
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Tell the database why the financial state is about to change; the history trigger reads it in this transaction.
        if (TransitionSql.HasContext)
        {
            await using var context = await CommandAsync(TransitionSql.ApplyContext, cancellationToken);
            TransitionSql.Bind(context);
            await context.ExecuteNonQueryAsync(cancellationToken);
        }

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

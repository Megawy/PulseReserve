using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.Modules.Payments.Persistence.Postgres;

/// <summary>
/// SQL behind reconciliation, refunds, the outbox, tickets and the operations audit trail. Every method is one short
/// transaction (or a single statement). Work items are claimed with <c>FOR UPDATE SKIP LOCKED</c> and a lease, so
/// concurrent workers never process the same item and a crashed worker's items come back when the lease ends.
/// Time always comes from the database clock.
/// </summary>
internal sealed class WorkflowStore(PaymentsDbContext db) : IWorkflowStore
{
    // ───────────── reconciliation ─────────────

    public async Task<int> DiscoverReconciliationTasksAsync(WorkflowOptions options, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);
        var inserted = 0;

        // 1. A successful payment whose order was never finalized (crash between recording the verdict and selling).
        inserted += await ExecAsync(
            """
            INSERT INTO payments.reconciliation_tasks (id, kind, subject_id, state, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), 'FinalizeSuccessfulPayment', a.id, 'Pending', 0, now(), now(), now()
            FROM payments.payment_attempts a JOIN payments.orders o ON o.id = a.order_id
            WHERE a.status = 'Succeeded' AND o.status IN ('PendingPayment', 'Cancelled', 'Expired')
              AND a.completed_at_utc <= now() - make_interval(secs => @grace)
            ORDER BY a.completed_at_utc LIMIT @batch
            ON CONFLICT (kind, subject_id) DO NOTHING
            """, cmd => { cmd.Parameters.AddWithValue("grace", options.CompletionGrace.TotalSeconds); cmd.Parameters.AddWithValue("batch", options.BatchSize * 5); }, cancellationToken);

        // 2. Attempts that stayed Initiated: the provider must be asked; silence is never read as success or failure.
        inserted += await ExecAsync(
            """
            INSERT INTO payments.reconciliation_tasks (id, kind, subject_id, state, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), 'ResolveStaleAttempt', a.id, 'Pending', 0, now(), now(), now()
            FROM payments.payment_attempts a
            WHERE a.status = 'Initiated' AND a.created_at_utc <= now() - make_interval(secs => @stale)
            ORDER BY a.created_at_utc LIMIT @batch
            ON CONFLICT (kind, subject_id) DO NOTHING
            """, cmd => { cmd.Parameters.AddWithValue("stale", options.StaleAttemptAfter.TotalSeconds); cmd.Parameters.AddWithValue("batch", options.BatchSize * 5); }, cancellationToken);

        // 3. Orders past their payment deadline that still say PendingPayment.
        inserted += await ExecAsync(
            """
            INSERT INTO payments.reconciliation_tasks (id, kind, subject_id, state, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), 'ExpireOrder', o.id, 'Pending', 0, now(), now(), now()
            FROM payments.orders o
            WHERE o.status = 'PendingPayment' AND o.payment_deadline_utc <= now() - make_interval(secs => @grace)
            ORDER BY o.payment_deadline_utc LIMIT @batch
            ON CONFLICT (kind, subject_id) DO NOTHING
            """, cmd => { cmd.Parameters.AddWithValue("grace", options.CompletionGrace.TotalSeconds); cmd.Parameters.AddWithValue("batch", options.BatchSize * 5); }, cancellationToken);

        // 4. A Paid order must always have its post-payment events (covers rows from before the outbox existed or lost by a bug).
        inserted += await ExecAsync(
            """
            INSERT INTO payments.outbox_events (id, type, aggregate_id, state, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), t.type, o.id, 'Pending', 0, now(), now(), now()
            FROM payments.orders o CROSS JOIN (VALUES ('IssueTickets'), ('NotifyOrderPaid')) AS t(type)
            WHERE o.status = 'Paid'
              AND NOT EXISTS (SELECT 1 FROM payments.outbox_events e WHERE e.aggregate_id = o.id AND e.type = t.type)
            ORDER BY o.created_at_utc LIMIT @batch
            ON CONFLICT (type, aggregate_id) DO NOTHING
            """, cmd => cmd.Parameters.AddWithValue("batch", options.BatchSize * 5), cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public Task<int> EnsureRefundsForRefundRequiredOrdersAsync(CancellationToken cancellationToken = default) =>
        ExecAsync(
            RefundSql.InsertForOrder.Replace("WHERE o.id = @order_id AND o.status = 'RefundRequired'",
                "WHERE o.status = 'RefundRequired' AND NOT EXISTS (SELECT 1 FROM payments.refunds r WHERE r.order_id = o.id)"),
            cmd =>
            {
                cmd.Parameters.AddWithValue("reason", Domain.RefundReason.PaymentAfterSaleFailed);
                cmd.Parameters.Add(new NpgsqlParameter("requested_by", NpgsqlDbType.Uuid) { Value = DBNull.Value });
            }, cancellationToken);

    public async Task<IReadOnlyList<ReconciliationTask>> ClaimTasksAsync(WorkflowOptions options, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        // A task whose worker keeps dying (lease expired again and again) is poison: stop and ask a human.
        await ExecAsync(
            """
            UPDATE payments.reconciliation_tasks SET state = 'NeedsReview', locked_until_utc = NULL, updated_at_utc = now(),
                last_error = 'Worker lease expired repeatedly'
            WHERE state = 'Processing' AND locked_until_utc <= now() AND attempts >= @max
            """, cmd => cmd.Parameters.AddWithValue("max", options.MaxAttempts), cancellationToken);

        var tasks = new List<ReconciliationTask>();
        await using (var cmd = await CommandAsync(
            """
            WITH due AS (
                SELECT id FROM payments.reconciliation_tasks
                WHERE (state = 'Pending' AND next_attempt_at_utc <= now())
                   OR (state = 'Processing' AND locked_until_utc <= now())
                ORDER BY next_attempt_at_utc
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            UPDATE payments.reconciliation_tasks t
            SET state = 'Processing', attempts = t.attempts + 1, locked_until_utc = now() + make_interval(secs => @lease), updated_at_utc = now()
            FROM due WHERE t.id = due.id
            RETURNING t.id, t.kind, t.subject_id, t.state, t.attempts, t.last_outcome, t.last_error, t.next_attempt_at_utc, t.created_at_utc, t.completed_at_utc
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("batch", options.BatchSize);
            cmd.Parameters.AddWithValue("lease", options.Lease.TotalSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tasks.Add(MapTask(reader));
        }

        await tx.CommitAsync(cancellationToken);
        return tasks;
    }

    public Task CompleteTaskAsync(Guid taskId, string outcome, CancellationToken cancellationToken = default) =>
        ExecAsync(
            """
            UPDATE payments.reconciliation_tasks
            SET state = 'Done', locked_until_utc = NULL, last_outcome = @outcome, last_error = NULL, completed_at_utc = now(), updated_at_utc = now()
            WHERE id = @id AND state = 'Processing'
            """, cmd => { cmd.Parameters.AddWithValue("id", taskId); cmd.Parameters.AddWithValue("outcome", Truncate(outcome, 100)!); }, cancellationToken);

    public Task RetryTaskLaterAsync(Guid taskId, string outcome, string? error, WorkflowOptions options, CancellationToken cancellationToken = default) =>
        ExecAsync(
            """
            UPDATE payments.reconciliation_tasks
            SET state = CASE WHEN attempts >= @max THEN 'NeedsReview' ELSE 'Pending' END,
                next_attempt_at_utc = now() + make_interval(secs => LEAST(@max_backoff, @base * power(2, GREATEST(attempts - 1, 0)))),
                locked_until_utc = NULL, last_outcome = @outcome, last_error = @error, updated_at_utc = now()
            WHERE id = @id AND state = 'Processing'
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", taskId);
                cmd.Parameters.AddWithValue("outcome", Truncate(outcome, 100)!);
                cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = (object?)Truncate(error, 300) ?? DBNull.Value });
                Backoff(cmd, options);
            }, cancellationToken);

    public async Task<AttemptRecord?> GetAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(
            "SELECT id, order_id, status, provider, provider_reference, created_at_utc, EXTRACT(EPOCH FROM (now() - created_at_utc))::float8 FROM payments.payment_attempts WHERE id = @id", cancellationToken);
        cmd.Parameters.AddWithValue("id", attemptId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AttemptRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTime>(5), TimeSpan.FromSeconds(reader.GetDouble(6)))
            : null;
    }

    public Task AbandonAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        ExecAsync("UPDATE payments.payment_attempts SET status = 'Cancelled', completed_at_utc = now(), checkout_url = NULL WHERE id = @id AND status = 'Initiated'",
            cmd => cmd.Parameters.AddWithValue("id", attemptId), cancellationToken);

    public Task CancelAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        ExecAsync(
            "UPDATE payments.payment_attempts SET status = 'Cancelled', completed_at_utc = now(), checkout_url = NULL WHERE id = @id AND status = 'Initiated' AND provider_reference IS NULL",
            cmd => cmd.Parameters.AddWithValue("id", attemptId), cancellationToken);

    public async Task<(Guid ReservationId, Guid CustomerId)?> ExpireOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);
        Guid reservationId, customerId;
        string status;
        bool overdue;
        await using (var cmd = await CommandAsync(
            "SELECT status, payment_deadline_utc <= now(), reservation_id, customer_id FROM payments.orders WHERE id = @id FOR UPDATE", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            status = reader.GetString(0);
            overdue = reader.GetBoolean(1);
            reservationId = reader.GetGuid(2);
            customerId = reader.GetGuid(3);
        }

        if (status == Domain.OrderStatus.PendingPayment && overdue)
        {
            await ExecAsync("UPDATE payments.orders SET status = 'Expired', updated_at_utc = now() WHERE id = @id AND status = 'PendingPayment'",
                cmd => cmd.Parameters.AddWithValue("id", orderId), cancellationToken);
            await ExecAsync(
                "UPDATE payments.payment_attempts SET status = 'Cancelled', completed_at_utc = now(), checkout_url = NULL WHERE order_id = @id AND status = 'Initiated' AND provider_reference IS NULL",
                cmd => cmd.Parameters.AddWithValue("id", orderId), cancellationToken);
            status = Domain.OrderStatus.Expired;
        }

        await tx.CommitAsync(cancellationToken);
        return status is Domain.OrderStatus.Expired or Domain.OrderStatus.Cancelled ? (reservationId, customerId) : null;
    }

    // ───────────── refunds ─────────────

    private const string RefundColumns =
        "r.id, r.order_id, r.payment_attempt_id, r.amount, r.currency, r.reason, r.state, r.provider, r.is_simulated, r.submission_number, r.provider_refund_id, r.attempts, r.last_error, r.requested_by, r.created_at_utc, r.completed_at_utc, r.next_attempt_at_utc, r.updated_at_utc, r.manual_reference, r.manual_note, r.manual_recorded_by, r.manual_recorded_at_utc";

    public async Task<IReadOnlyList<Refund>> ClaimRefundsAsync(WorkflowOptions options, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        await ExecAsync(
            """
            UPDATE payments.refunds SET state = 'NeedsReview', locked_until_utc = NULL, updated_at_utc = now(), last_error = 'Attempt limit reached'
            WHERE state IN ('Requested', 'Unknown', 'Pending') AND attempts >= @max AND (locked_until_utc IS NULL OR locked_until_utc <= now())
            """, cmd => cmd.Parameters.AddWithValue("max", options.MaxAttempts), cancellationToken);

        var refunds = new List<Refund>();
        await using (var cmd = await CommandAsync(
            $"""
             WITH due AS (
                 SELECT id FROM payments.refunds
                 WHERE state IN ('Requested', 'Unknown', 'Pending') AND next_attempt_at_utc <= now()
                   AND (locked_until_utc IS NULL OR locked_until_utc <= now())
                 ORDER BY next_attempt_at_utc
                 LIMIT @batch
                 FOR UPDATE SKIP LOCKED),
             claimed AS (
                 UPDATE payments.refunds t
                 SET attempts = t.attempts + 1, locked_until_utc = now() + make_interval(secs => @lease), updated_at_utc = now()
                 FROM due WHERE t.id = due.id
                 RETURNING t.*)
             SELECT {RefundColumns} FROM claimed r
             """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("batch", options.BatchSize);
            cmd.Parameters.AddWithValue("lease", options.Lease.TotalSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                refunds.Add(MapRefund(reader));
        }

        await tx.CommitAsync(cancellationToken);
        return refunds;
    }

    public async Task<bool> MarkRefundUnknownAsync(Guid refundId, CancellationToken cancellationToken = default) =>
        await ExecAsync("UPDATE payments.refunds SET state = 'Unknown', updated_at_utc = now() WHERE id = @id AND state = 'Requested'",
            cmd => cmd.Parameters.AddWithValue("id", refundId), cancellationToken) == 1;

    public Task MarkRefundPendingAsync(Guid refundId, string? providerRefundId, WorkflowOptions options, CancellationToken cancellationToken = default) =>
        ExecAsync(
            """
            UPDATE payments.refunds
            SET state = 'Pending', provider_refund_id = COALESCE(@provider_id, provider_refund_id), last_error = NULL,
                next_attempt_at_utc = now() + make_interval(secs => LEAST(@max_backoff, @base * power(2, GREATEST(attempts - 1, 0)))),
                locked_until_utc = NULL, updated_at_utc = now()
            WHERE id = @id AND state IN ('Requested', 'Unknown', 'Pending')
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", refundId);
                cmd.Parameters.Add(new NpgsqlParameter("provider_id", NpgsqlDbType.Varchar) { Value = (object?)providerRefundId ?? DBNull.Value });
                Backoff(cmd, options);
            }, cancellationToken);

    public async Task MarkRefundSucceededAsync(Guid refundId, string? providerRefundId, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);
        Guid? orderId = null;
        await using (var cmd = await CommandAsync(
            """
            UPDATE payments.refunds
            SET state = 'Succeeded', provider_refund_id = COALESCE(@provider_id, provider_refund_id), last_error = NULL,
                locked_until_utc = NULL, completed_at_utc = now(), updated_at_utc = now()
            WHERE id = @id AND state NOT IN ('Succeeded', 'ManuallyRecorded')
            RETURNING order_id
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", refundId);
            cmd.Parameters.Add(new NpgsqlParameter("provider_id", NpgsqlDbType.Varchar) { Value = (object?)providerRefundId ?? DBNull.Value });
            orderId = await cmd.ExecuteScalarAsync(cancellationToken) as Guid?;
        }

        if (orderId is { } id)
        {
            // Only RefundRequired becomes Refunded; a paid order is never touched.
            await ExecAsync("UPDATE payments.orders SET status = 'Refunded', updated_at_utc = now() WHERE id = @id AND status = 'RefundRequired'",
                cmd => cmd.Parameters.AddWithValue("id", id), cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public Task MarkRefundRejectedAsync(Guid refundId, string? code, bool retryable, WorkflowOptions options, CancellationToken cancellationToken = default) =>
        ExecAsync(
            """
            UPDATE payments.refunds
            SET state = CASE WHEN NOT @retryable THEN 'Failed' WHEN attempts >= @max THEN 'NeedsReview' ELSE 'Requested' END,
                submission_number = CASE WHEN @retryable THEN submission_number + 1 ELSE submission_number END,
                last_error = @code, locked_until_utc = NULL,
                next_attempt_at_utc = now() + make_interval(secs => LEAST(@max_backoff, @base * power(2, GREATEST(attempts - 1, 0)))),
                completed_at_utc = CASE WHEN @retryable THEN NULL ELSE now() END, updated_at_utc = now()
            WHERE id = @id AND state IN ('Requested', 'Unknown', 'Pending')
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", refundId);
                cmd.Parameters.AddWithValue("retryable", retryable);
                cmd.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = (object?)Truncate(code, 300) ?? DBNull.Value });
                Backoff(cmd, options);
            }, cancellationToken);

    public Task DeferRefundAsync(Guid refundId, string? error, WorkflowOptions options, CancellationToken cancellationToken = default) =>
        ExecAsync(
            """
            UPDATE payments.refunds
            SET state = CASE WHEN attempts >= @max THEN 'NeedsReview' ELSE state END,
                last_error = @error, locked_until_utc = NULL,
                next_attempt_at_utc = now() + make_interval(secs => LEAST(@max_backoff, @base * power(2, GREATEST(attempts - 1, 0)))),
                updated_at_utc = now()
            WHERE id = @id AND state IN ('Requested', 'Unknown', 'Pending')
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", refundId);
                cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = (object?)Truncate(error, 300) ?? DBNull.Value });
                Backoff(cmd, options);
            }, cancellationToken);

    public Task MarkRefundNeedsReviewAsync(Guid refundId, string error, CancellationToken cancellationToken = default) =>
        ExecAsync(
            """
            UPDATE payments.refunds
            SET state = 'NeedsReview', last_error = @error, locked_until_utc = NULL, updated_at_utc = now()
            WHERE id = @id AND state IN ('Requested', 'Unknown', 'Pending')
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", refundId);
                cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = (object?)Truncate(error, 300) ?? DBNull.Value });
            }, cancellationToken);

    public async Task<IReadOnlyList<Refund>> ListRefundsAsync(string? state, int limit, CancellationToken cancellationToken = default)
    {
        var refunds = new List<Refund>();
        await using var cmd = await CommandAsync(
            $"SELECT {RefundColumns} FROM payments.refunds r WHERE (@state::text IS NULL OR r.state = @state) ORDER BY r.created_at_utc DESC LIMIT @limit",
            cancellationToken);
        cmd.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Varchar) { Value = (object?)state ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            refunds.Add(MapRefund(reader));
        return refunds;
    }

    public async Task<Refund?> GetRefundAsync(Guid refundId, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync($"SELECT {RefundColumns} FROM payments.refunds r WHERE r.id = @id", cancellationToken);
        cmd.Parameters.AddWithValue("id", refundId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRefund(reader) : null;
    }

    public async Task<ManualRefundResult> RecordManualRefundAsync(Guid refundId, Guid adminUserId, ManualRefundRecord record, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        // The row lock serializes concurrent submissions: the second one sees the first one's committed state.
        Refund? current;
        await using (var cmd = await CommandAsync($"SELECT {RefundColumns} FROM payments.refunds r WHERE r.id = @id FOR UPDATE", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", refundId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            current = await reader.ReadAsync(cancellationToken) ? MapRefund(reader) : null;
        }

        if (current is null)
            return new ManualRefundResult(ManualRefundOutcome.NotFound, null);

        var reference = Truncate(record.ExternalReference?.Trim() is { Length: > 0 } r ? r : null, 200);
        var note = Truncate(record.Note?.Trim() is { Length: > 0 } n ? n : null, 300);

        if (current.State == Domain.RefundState.ManuallyRecorded)
        {
            // The same record again (a double click, a retry after a lost answer) is answered with the existing one and
            // changes nothing, audits nothing. A different record is a conflict: it never overwrites the first.
            return string.Equals(current.ManualReference, reference, StringComparison.Ordinal) && string.Equals(current.ManualNote, note, StringComparison.Ordinal)
                ? new ManualRefundResult(ManualRefundOutcome.Replay, current)
                : new ManualRefundResult(ManualRefundOutcome.Conflict, current);
        }

        if (current.State != Domain.RefundState.NeedsReview)
            return new ManualRefundResult(ManualRefundOutcome.NotAllowed, current);

        // Amount and currency come from the stored refund; the submitted values only have to match them (full refunds only).
        if (record.Amount != current.Amount || !string.Equals(record.Currency, current.Currency, StringComparison.Ordinal))
            return new ManualRefundResult(ManualRefundOutcome.AmountMismatch, current);

        await ExecAsync(
            """
            UPDATE payments.refunds
            SET state = 'ManuallyRecorded', manual_reference = @reference, manual_note = @note, manual_recorded_by = @admin,
                manual_recorded_at_utc = now(), completed_at_utc = now(), locked_until_utc = NULL, updated_at_utc = now()
            WHERE id = @id AND state = 'NeedsReview'
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", refundId);
                cmd.Parameters.AddWithValue("admin", adminUserId);
                cmd.Parameters.Add(new NpgsqlParameter("reference", NpgsqlDbType.Varchar) { Value = (object?)reference ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
            }, cancellationToken);

        // Same rule as the provider-confirmed path: only RefundRequired becomes Refunded; a paid order is never touched.
        await ExecAsync("UPDATE payments.orders SET status = 'Refunded', updated_at_utc = now() WHERE id = @id AND status = 'RefundRequired'",
            cmd => cmd.Parameters.AddWithValue("id", current.OrderId), cancellationToken);

        // The audit detail names ids and the kind only: the operator note is free text and is not copied into the log.
        await AuditAsync(adminUserId, "RecordManualRefund", "Refund", refundId,
            $"NeedsReview -> ManuallyRecorded (performed externally, NOT provider-confirmed); order {current.OrderId}; provider {current.Provider}; reference {(reference is null ? "none" : "given")}",
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return new ManualRefundResult(ManualRefundOutcome.Recorded, await GetRefundAsync(refundId, cancellationToken));
    }

    public async Task<OperationResult> RetryRefundAsync(Guid refundId, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);
        string? state;
        await using (var cmd = await CommandAsync("SELECT state FROM payments.refunds WHERE id = @id FOR UPDATE", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", refundId);
            state = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (state is null)
            return new OperationResult(OperationOutcome.NotFound);

        string? next = state switch
        {
            // The provider definitively refused: nothing moved, so a fresh submission (new idempotency key) is safe.
            Domain.RefundState.Failed => Domain.RefundState.Requested,
            // Outcome unknown or unresolved: ask the provider first; only a "no such refund" answer leads to a resubmission.
            Domain.RefundState.NeedsReview or Domain.RefundState.Unknown or Domain.RefundState.Pending => Domain.RefundState.Unknown,
            _ => null
        };
        if (next is null)
            return new OperationResult(OperationOutcome.InvalidState);

        await ExecAsync(
            """
            UPDATE payments.refunds
            SET state = @next, attempts = 0, last_error = NULL, locked_until_utc = NULL, next_attempt_at_utc = now(), completed_at_utc = NULL,
                submission_number = CASE WHEN state = 'Failed' THEN submission_number + 1 ELSE submission_number END, updated_at_utc = now()
            WHERE id = @id
            """, cmd => { cmd.Parameters.AddWithValue("id", refundId); cmd.Parameters.AddWithValue("next", next); }, cancellationToken);
        await AuditAsync(adminUserId, "RetryRefund", "Refund", refundId, $"{state} -> {next}", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new OperationResult(OperationOutcome.Done);
    }

    // ───────────── payment anomalies ─────────────

    public async Task<bool> RecordAsync(PaymentAnomalyRecord record, CancellationToken cancellationToken = default)
    {
        // One short transaction: the row and its history entry (written by the trigger from the transaction-local context)
        // commit together, or neither does and the webhook is not acknowledged.
        await using var tx = await BeginAsync(cancellationToken);
        var correlated = record.AttemptId is not null && record.OrderId is not null;
        var rows = await ExecAsync(
            correlated
                ? """
                  INSERT INTO payments.payment_anomalies
                      (id, kind, reason_code, state, dedupe_key, provider, provider_event_id, attempt_id, order_id, claimed_attempt_id, claimed_order_id,
                       received_amount_minor, received_currency, expected_amount_minor, expected_currency, order_status_at_detection,
                       attempt_status_at_detection, detected_at_utc)
                  SELECT gen_random_uuid(), @kind, @reason, 'Open', @dedupe, a.provider, @event, a.id, o.id, @claimed_attempt, @claimed_order,
                         @received_amount, @received_currency, @expected_amount, @expected_currency, o.status, a.status, now()
                  FROM payments.payment_attempts a JOIN payments.orders o ON o.id = a.order_id
                  WHERE a.id = @attempt AND o.id = @order AND a.provider = @provider
                  ON CONFLICT (kind, dedupe_key) DO NOTHING
                  """
                : """
                  INSERT INTO payments.payment_anomalies
                      (id, kind, reason_code, state, dedupe_key, provider, provider_event_id, claimed_attempt_id, claimed_order_id,
                       received_amount_minor, received_currency, detected_at_utc)
                  VALUES (gen_random_uuid(), @kind, @reason, 'Open', @dedupe, @provider, @event, @claimed_attempt, @claimed_order,
                          @received_amount, @received_currency, now())
                  ON CONFLICT (kind, dedupe_key) DO NOTHING
                  """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("kind", record.Kind);
                cmd.Parameters.AddWithValue("reason", record.ReasonCode);
                cmd.Parameters.AddWithValue("dedupe", Truncate(record.DedupeKey, 300)!);
                cmd.Parameters.AddWithValue("provider", Truncate(record.Provider, 50)!);
                cmd.Parameters.Add(new NpgsqlParameter("event", NpgsqlDbType.Varchar) { Value = (object?)Truncate(record.ProviderEventId, 200) ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("claimed_attempt", NpgsqlDbType.Uuid) { Value = (object?)record.ClaimedAttemptId ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("claimed_order", NpgsqlDbType.Uuid) { Value = (object?)record.ClaimedOrderId ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("received_amount", NpgsqlDbType.Bigint) { Value = (object?)record.ReceivedAmountMinor ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("received_currency", NpgsqlDbType.Varchar) { Value = (object?)record.ReceivedCurrency ?? DBNull.Value });
                if (correlated)
                {
                    cmd.Parameters.AddWithValue("attempt", record.AttemptId!.Value);
                    cmd.Parameters.AddWithValue("order", record.OrderId!.Value);
                    cmd.Parameters.Add(new NpgsqlParameter("expected_amount", NpgsqlDbType.Bigint) { Value = (object?)record.ExpectedAmountMinor ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("expected_currency", NpgsqlDbType.Varchar) { Value = (object?)record.ExpectedCurrency ?? DBNull.Value });
                }
            }, cancellationToken);

        if (rows == 0)
        {
            // Nothing was inserted. That is a duplicate only if the SAME provider event is really on record; an insert that matched no
            // row at all (for example the attempt it names vanished) would look identical. Confirm inside the transaction: a webhook
            // may be acknowledged because its evidence exists, never because the insert merely returned zero rows.
            bool present;
            await using (var check = await CommandAsync(
                "SELECT EXISTS (SELECT 1 FROM payments.payment_anomalies WHERE kind = @kind AND dedupe_key = @dedupe)", cancellationToken))
            {
                check.Parameters.AddWithValue("kind", record.Kind);
                check.Parameters.AddWithValue("dedupe", Truncate(record.DedupeKey, 300)!);
                present = (bool)(await check.ExecuteScalarAsync(cancellationToken))!;
            }

            if (!present)
                throw new InvalidOperationException("The payment anomaly was neither stored nor already on record; the event is not acknowledged.");
        }

        await tx.CommitAsync(cancellationToken);
        return rows == 1;
    }

    private const string AnomalySelect =
        """
        SELECT x.id, x.kind, x.reason_code, x.state, x.provider, x.provider_event_id, x.attempt_id, x.order_id, x.claimed_attempt_id, x.claimed_order_id,
               x.received_amount_minor, x.received_currency, x.expected_amount_minor, x.expected_currency, x.order_status_at_detection,
               x.attempt_status_at_detection, x.detected_at_utc, x.resolution, x.resolved_by, x.resolved_at_utc, x.resolution_reference,
               x.resolution_note, o.status,
               floor(extract(epoch FROM (COALESCE(x.resolved_at_utc, now()) - x.detected_at_utc)))::bigint
        FROM payments.payment_anomalies x LEFT JOIN payments.orders o ON o.id = x.order_id
        """;

    private static PaymentAnomaly MapAnomaly(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetGuid(9),
        r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetInt64(12),
        r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15),
        r.GetFieldValue<DateTime>(16), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetGuid(18),
        r.IsDBNull(19) ? null : r.GetFieldValue<DateTime>(19), r.IsDBNull(20) ? null : r.GetString(20), r.IsDBNull(21) ? null : r.GetString(21),
        r.IsDBNull(22) ? null : r.GetString(22))
    {
        AgeSeconds = r.IsDBNull(23) ? 0 : Math.Max(0, r.GetInt64(23))
    };

    public async Task<IReadOnlyList<PaymentAnomaly>> ListAnomaliesAsync(string? state, int limit, CancellationToken cancellationToken = default)
    {
        var list = new List<PaymentAnomaly>();
        await using var cmd = await CommandAsync(
            // Open items first and OLDEST first, so the one that has waited longest can never be pushed out of the page by newer ones;
            // resolved items follow, newest first.
            AnomalySelect + " WHERE (@state::text IS NULL OR x.state = @state) " +
            "ORDER BY (x.state = 'Open') DESC, CASE WHEN x.state = 'Open' THEN x.detected_at_utc END ASC, x.detected_at_utc DESC, x.id LIMIT @limit", cancellationToken);
        cmd.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Varchar) { Value = (object?)state ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(MapAnomaly(reader));
        return list;
    }

    private async Task<PaymentAnomaly?> GetAnomalyAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = await CommandAsync(AnomalySelect + " WHERE x.id = @id", cancellationToken);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAnomaly(reader) : null;
    }

    public async Task<PaymentAnomalyResolveResult> ResolveAnomalyAsync(
        Guid anomalyId, Guid adminUserId, PaymentAnomalyResolutionRecord record, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        string state, kind, provider;
        string? storedResolution, storedReference, storedNote;
        await using (var cmd = await CommandAsync(
            "SELECT state, kind, provider, resolution, resolution_reference, resolution_note FROM payments.payment_anomalies WHERE id = @id FOR UPDATE", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", anomalyId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new PaymentAnomalyResolveResult(PaymentAnomalyResolveOutcome.NotFound, null);
            state = reader.GetString(0);
            kind = reader.GetString(1);
            provider = reader.GetString(2);
            storedResolution = reader.IsDBNull(3) ? null : reader.GetString(3);
            storedReference = reader.IsDBNull(4) ? null : reader.GetString(4);
            storedNote = reader.IsDBNull(5) ? null : reader.GetString(5);
        }

        var reference = Truncate(record.ExternalReference?.Trim() is { Length: > 0 } r ? r : null, 200);
        var note = Truncate(record.Note?.Trim() is { Length: > 0 } n ? n : null, 300);

        if (state == Domain.PaymentAnomalyState.Resolved)
        {
            var same = storedResolution == record.Resolution && string.Equals(storedReference, reference, StringComparison.Ordinal) && string.Equals(storedNote, note, StringComparison.Ordinal);
            return new PaymentAnomalyResolveResult(same ? PaymentAnomalyResolveOutcome.Replay : PaymentAnomalyResolveOutcome.Conflict, await GetAnomalyAsync(anomalyId, cancellationToken));
        }

        await ExecAsync(
            """
            UPDATE payments.payment_anomalies
            SET state = 'Resolved', resolution = @resolution, resolved_by = @admin, resolved_at_utc = now(), resolution_reference = @reference, resolution_note = @note
            WHERE id = @id AND state = 'Open'
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", anomalyId);
                cmd.Parameters.AddWithValue("admin", adminUserId);
                cmd.Parameters.AddWithValue("resolution", record.Resolution);
                cmd.Parameters.Add(new NpgsqlParameter("reference", NpgsqlDbType.Varchar) { Value = (object?)reference ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
            }, cancellationToken);

        await AuditAsync(adminUserId, "ResolvePaymentAnomaly", "PaymentAnomaly", anomalyId,
            $"Open -> Resolved as {record.Resolution} (administrator statement after investigation, NOT provider-verified); kind {kind}; provider {provider}; reference {(reference is null ? "none" : "given")}",
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return new PaymentAnomalyResolveResult(PaymentAnomalyResolveOutcome.Resolved, await GetAnomalyAsync(anomalyId, cancellationToken));
    }

    // ───────────── payment exceptions ─────────────

    private const string ExceptionSelect =
        """
        SELECT e.id, e.kind, e.attempt_id, e.order_id, e.provider, e.is_simulated, e.amount, e.currency, e.original_attempt_status,
               e.original_failure_code, e.original_completed_at_utc, e.order_status_at_detection, e.detected_at_utc, e.state,
               e.resolution, e.resolved_by, e.resolved_at_utc, e.resolution_reference, e.resolution_note,
               o.status, o.payment_deadline_utc, r.state
        FROM payments.payment_exceptions e
        JOIN payments.orders o ON o.id = e.order_id
        LEFT JOIN payments.refunds r ON r.order_id = e.order_id
        """;

    public async Task<IReadOnlyList<PaymentException>> ListExceptionsAsync(string? state, int limit, CancellationToken cancellationToken = default)
    {
        var list = new List<PaymentException>();
        await using var cmd = await CommandAsync(
            // Same rule as anomalies: open and oldest first, then resolved newest first.
            ExceptionSelect + " WHERE (@state::text IS NULL OR e.state = @state) " +
            "ORDER BY (e.state = 'Open') DESC, CASE WHEN e.state = 'Open' THEN e.detected_at_utc END ASC, e.detected_at_utc DESC, e.id LIMIT @limit", cancellationToken);
        cmd.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Varchar) { Value = (object?)state ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(MapException(reader));
        return list;
    }

    public async Task<PaymentException?> GetExceptionAsync(Guid exceptionId, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(ExceptionSelect + " WHERE e.id = @id", cancellationToken);
        cmd.Parameters.AddWithValue("id", exceptionId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapException(reader) : null;
    }

    public async Task<PaymentExceptionResolveResult> ResolveExceptionAsync(
        Guid exceptionId, Guid adminUserId, PaymentExceptionResolutionRecord record, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        // The row lock serialises concurrent resolutions: the second one sees the first one's committed state.
        string state, provider, currency;
        decimal amount;
        Guid attemptId, orderId;
        string? storedReference, storedNote;
        await using (var cmd = await CommandAsync(
            "SELECT state, provider, currency, amount, attempt_id, order_id, resolution_reference, resolution_note FROM payments.payment_exceptions WHERE id = @id FOR UPDATE",
            cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", exceptionId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new PaymentExceptionResolveResult(PaymentExceptionResolveOutcome.NotFound, null);
            state = reader.GetString(0);
            provider = reader.GetString(1);
            currency = reader.GetString(2);
            amount = reader.GetDecimal(3);
            attemptId = reader.GetGuid(4);
            orderId = reader.GetGuid(5);
            storedReference = reader.IsDBNull(6) ? null : reader.GetString(6);
            storedNote = reader.IsDBNull(7) ? null : reader.GetString(7);
        }

        var reference = Truncate(record.ExternalReference?.Trim() is { Length: > 0 } r ? r : null, 200);
        var note = Truncate(record.Note?.Trim() is { Length: > 0 } n ? n : null, 300);

        if (state == Domain.PaymentExceptionState.Resolved)
        {
            // The same statement again changes and audits nothing; a different one never overwrites the first.
            var same = string.Equals(storedReference, reference, StringComparison.Ordinal) && string.Equals(storedNote, note, StringComparison.Ordinal);
            return new PaymentExceptionResolveResult(
                same ? PaymentExceptionResolveOutcome.Replay : PaymentExceptionResolveOutcome.Conflict, await GetExceptionAsync(exceptionId, cancellationToken));
        }

        if (record.Amount != amount || !string.Equals(record.Currency, currency, StringComparison.Ordinal))
            return new PaymentExceptionResolveResult(PaymentExceptionResolveOutcome.AmountMismatch, null);

        await ExecAsync(
            """
            UPDATE payments.payment_exceptions
            SET state = 'Resolved', resolution = 'RefundedExternally', resolved_by = @admin, resolved_at_utc = now(),
                resolution_reference = @reference, resolution_note = @note
            WHERE id = @id AND state = 'Open'
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", exceptionId);
                cmd.Parameters.AddWithValue("admin", adminUserId);
                cmd.Parameters.Add(new NpgsqlParameter("reference", NpgsqlDbType.Varchar) { Value = (object?)reference ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
            }, cancellationToken);

        // Ids and the kind of statement only: the free-text note is not copied into the log.
        await AuditAsync(adminUserId, "ResolvePaymentException", "PaymentException", exceptionId,
            $"Open -> Resolved (money returned outside this system per administrator statement, NOT provider-verified); attempt {attemptId}; order {orderId}; provider {provider}; reference {(reference is null ? "none" : "given")}",
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return new PaymentExceptionResolveResult(PaymentExceptionResolveOutcome.Resolved, await GetExceptionAsync(exceptionId, cancellationToken));
    }

    public async Task<(OperationResult Result, Refund? Refund)> RequestRefundAsync(Guid orderId, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);
        string? status;
        await using (var cmd = await CommandAsync("SELECT status FROM payments.orders WHERE id = @id FOR UPDATE", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            status = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (status is null)
            return (new OperationResult(OperationOutcome.NotFound), null);
        if (status is not (Domain.OrderStatus.RefundRequired or Domain.OrderStatus.Refunded))
            return (new OperationResult(OperationOutcome.InvalidState), null);

        var created = await ExecAsync(RefundSql.InsertForOrder,
            cmd =>
            {
                cmd.Parameters.AddWithValue("order_id", orderId);
                cmd.Parameters.AddWithValue("reason", Domain.RefundReason.AdminRequested);
                cmd.Parameters.Add(new NpgsqlParameter("requested_by", NpgsqlDbType.Uuid) { Value = adminUserId });
            }, cancellationToken);
        if (created == 1)
            await AuditAsync(adminUserId, "RequestRefund", "Order", orderId, "Refund created by an administrator", cancellationToken);

        Refund? refund = null;
        await using (var cmd = await CommandAsync($"SELECT {RefundColumns} FROM payments.refunds r WHERE r.order_id = @id", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                refund = MapRefund(reader);
        }

        await tx.CommitAsync(cancellationToken);
        return (new OperationResult(OperationOutcome.Done), refund);
    }

    // ───────────── outbox, tickets, notifications ─────────────

    public async Task<IReadOnlyList<OutboxEvent>> ClaimOutboxAsync(WorkflowOptions options, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        await ExecAsync(
            """
            UPDATE payments.outbox_events SET state = 'NeedsReview', locked_until_utc = NULL, updated_at_utc = now(), last_error = 'Worker lease expired repeatedly'
            WHERE state = 'Processing' AND locked_until_utc <= now() AND attempts >= @max
            """, cmd => cmd.Parameters.AddWithValue("max", options.MaxAttempts), cancellationToken);

        var events = new List<OutboxEvent>();
        await using (var cmd = await CommandAsync(
            """
            WITH due AS (
                SELECT id FROM payments.outbox_events
                WHERE (state = 'Pending' AND next_attempt_at_utc <= now())
                   OR (state = 'Processing' AND locked_until_utc <= now())
                ORDER BY next_attempt_at_utc
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            UPDATE payments.outbox_events e
            SET state = 'Processing', attempts = e.attempts + 1, locked_until_utc = now() + make_interval(secs => @lease), updated_at_utc = now()
            FROM due WHERE e.id = due.id
            RETURNING e.id, e.type, e.aggregate_id, e.state, e.attempts, e.last_error, e.created_at_utc, e.processed_at_utc, e.next_attempt_at_utc, e.updated_at_utc
            """, cancellationToken))
        {
            cmd.Parameters.AddWithValue("batch", options.BatchSize);
            cmd.Parameters.AddWithValue("lease", options.Lease.TotalSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                events.Add(MapOutbox(reader));
        }

        await tx.CommitAsync(cancellationToken);
        return events;
    }

    public Task CompleteOutboxAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        ExecAsync(
            "UPDATE payments.outbox_events SET state = 'Done', locked_until_utc = NULL, last_error = NULL, processed_at_utc = now(), updated_at_utc = now() WHERE id = @id AND state = 'Processing'",
            cmd => cmd.Parameters.AddWithValue("id", eventId), cancellationToken);

    public Task RetryOutboxLaterAsync(Guid eventId, string? error, WorkflowOptions options, CancellationToken cancellationToken = default) =>
        ExecAsync(
            """
            UPDATE payments.outbox_events
            SET state = CASE WHEN attempts >= @max THEN 'NeedsReview' ELSE 'Pending' END,
                next_attempt_at_utc = now() + make_interval(secs => LEAST(@max_backoff, @base * power(2, GREATEST(attempts - 1, 0)))),
                locked_until_utc = NULL, last_error = @error, updated_at_utc = now()
            WHERE id = @id AND state = 'Processing'
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", eventId);
                cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = (object?)Truncate(error, 300) ?? DBNull.Value });
                Backoff(cmd, options);
            }, cancellationToken);

    public async Task<int?> IssueTicketsAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken);

        Guid customerId, eventId;
        await using (var cmd = await CommandAsync(
            "SELECT customer_id, event_id FROM payments.orders WHERE id = @id AND status = 'Paid' FOR SHARE", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null; // not Paid (or absent): no tickets, ever
            customerId = reader.GetGuid(0);
            eventId = reader.GetGuid(1);
        }

        var seatIds = new List<Guid>();
        await using (var cmd = await CommandAsync("SELECT seat_id FROM payments.order_items WHERE order_id = @id ORDER BY seat_id", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                seatIds.Add(reader.GetGuid(0));
        }

        // 256 random bits per ticket: unguessable. A retry generates new candidates, but the (order, seat) unique
        // index discards them, so exactly one ticket per seat survives.
        var publicIds = seatIds.Select(_ => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')).ToArray();
        await ExecAsync(
            """
            INSERT INTO payments.tickets (id, public_id, order_id, seat_id, event_id, customer_id, status, issued_at_utc)
            SELECT gen_random_uuid(), t.public_id, @order_id, t.seat_id, @event_id, @customer_id, 'Valid', now()
            FROM unnest(@seat_ids, @public_ids) AS t(seat_id, public_id)
            ON CONFLICT (order_id, seat_id) DO NOTHING
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("order_id", orderId);
                cmd.Parameters.AddWithValue("event_id", eventId);
                cmd.Parameters.AddWithValue("customer_id", customerId);
                cmd.Parameters.Add(new NpgsqlParameter("seat_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seatIds.ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("public_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = publicIds });
            }, cancellationToken);

        int count;
        await using (var cmd = await CommandAsync("SELECT count(*) FROM payments.tickets WHERE order_id = @id", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            count = (int)(long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        await tx.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<IReadOnlyList<Ticket>> ListTicketsAsync(Guid orderId, Guid? customerId, CancellationToken cancellationToken = default)
    {
        var tickets = new List<Ticket>();
        await using var cmd = await CommandAsync(
            """
            SELECT id, public_id, order_id, seat_id, event_id, status, issued_at_utc FROM payments.tickets
            WHERE order_id = @order_id AND (@customer_id::uuid IS NULL OR customer_id = @customer_id)
            ORDER BY seat_id
            """, cancellationToken);
        cmd.Parameters.AddWithValue("order_id", orderId);
        cmd.Parameters.Add(new NpgsqlParameter("customer_id", NpgsqlDbType.Uuid) { Value = (object?)customerId ?? DBNull.Value });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tickets.Add(new Ticket(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetString(5), reader.GetFieldValue<DateTime>(6)));
        return tickets;
    }

    public async Task<bool> HasNotificationAsync(Guid orderId, string kind, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(
            "SELECT EXISTS (SELECT 1 FROM payments.notification_deliveries WHERE order_id = @id AND kind = @kind)", cancellationToken);
        cmd.Parameters.AddWithValue("id", orderId);
        cmd.Parameters.AddWithValue("kind", kind);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public Task RecordNotificationAsync(Guid orderId, string kind, string channel, CancellationToken cancellationToken = default) =>
        ExecAsync(
            "INSERT INTO payments.notification_deliveries (id, order_id, kind, channel, sent_at_utc) VALUES (gen_random_uuid(), @id, @kind, @channel, now()) ON CONFLICT (order_id, kind) DO NOTHING",
            cmd => { cmd.Parameters.AddWithValue("id", orderId); cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("channel", channel); }, cancellationToken);

    // ───────────── operations ─────────────

    public async Task<IReadOnlyList<ReconciliationTask>> ListTasksAsync(string? state, int limit, CancellationToken cancellationToken = default)
    {
        var list = new List<ReconciliationTask>();
        await using var cmd = await CommandAsync(
            """
            SELECT id, kind, subject_id, state, attempts, last_outcome, last_error, next_attempt_at_utc, created_at_utc, completed_at_utc
            FROM payments.reconciliation_tasks WHERE (@state::text IS NULL OR state = @state) ORDER BY created_at_utc DESC LIMIT @limit
            """, cancellationToken);
        cmd.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Varchar) { Value = (object?)state ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(MapTask(reader));
        return list;
    }

    public async Task<IReadOnlyList<OutboxEvent>> ListOutboxAsync(string? state, int limit, CancellationToken cancellationToken = default)
    {
        var list = new List<OutboxEvent>();
        await using var cmd = await CommandAsync(
            """
            SELECT id, type, aggregate_id, state, attempts, last_error, created_at_utc, processed_at_utc, next_attempt_at_utc, updated_at_utc
            FROM payments.outbox_events WHERE (@state::text IS NULL OR state = @state) ORDER BY created_at_utc DESC LIMIT @limit
            """, cancellationToken);
        cmd.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Varchar) { Value = (object?)state ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(MapOutbox(reader));
        return list;
    }

    public Task<OperationResult> RetryTaskAsync(Guid taskId, Guid adminUserId, CancellationToken cancellationToken = default) =>
        RetryWorkItemAsync("reconciliation_tasks", "RetryReconciliationTask", "ReconciliationTask", taskId, adminUserId, cancellationToken);

    public Task<OperationResult> RetryOutboxAsync(Guid eventId, Guid adminUserId, CancellationToken cancellationToken = default) =>
        RetryWorkItemAsync("outbox_events", "RetryOutboxEvent", "OutboxEvent", eventId, adminUserId, cancellationToken);

    private async Task<OperationResult> RetryWorkItemAsync(string table, string action, string subjectType, Guid id, Guid adminUserId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(cancellationToken);
        string? state;
        await using (var cmd = await CommandAsync($"SELECT state FROM payments.{table} WHERE id = @id FOR UPDATE", cancellationToken))
        {
            cmd.Parameters.AddWithValue("id", id);
            state = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (state is null)
            return new OperationResult(OperationOutcome.NotFound);
        if (state != Domain.WorkState.NeedsReview)
            return new OperationResult(OperationOutcome.InvalidState);

        await ExecAsync(
            $"UPDATE payments.{table} SET state = 'Pending', attempts = 0, last_error = NULL, locked_until_utc = NULL, next_attempt_at_utc = now(), updated_at_utc = now() WHERE id = @id",
            cmd => cmd.Parameters.AddWithValue("id", id), cancellationToken);
        await AuditAsync(adminUserId, action, subjectType, id, "NeedsReview -> Pending", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new OperationResult(OperationOutcome.Done);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken cancellationToken = default)
    {
        var list = new List<AuditEntry>();
        await using var cmd = await CommandAsync(
            "SELECT id, actor_user_id, action, subject_type, subject_id, detail, created_at_utc FROM payments.admin_audit_log ORDER BY created_at_utc DESC LIMIT @limit",
            cancellationToken);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new AuditEntry(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<DateTime>(6)));
        return list;
    }

    public async Task<IReadOnlyList<FinancialTransition>> ListTransitionsAsync(
        Guid? orderId, string? entityType, Guid? entityId, int limit, CancellationToken cancellationToken = default)
    {
        var list = new List<FinancialTransition>();
        await using var cmd = await CommandAsync(
            """
            SELECT id, occurred_at_utc, entity_type, entity_id, order_id, from_state, to_state, event_code, kind, source, fact_basis,
                   provider, provider_event_id, actor_user_id, correlation_id, reason
            FROM payments.financial_transitions
            WHERE (@order::uuid IS NULL OR order_id = @order) AND (@type::text IS NULL OR entity_type = @type) AND (@entity::uuid IS NULL OR entity_id = @entity)
            ORDER BY occurred_at_utc DESC, id
            LIMIT @limit
            """, cancellationToken);
        cmd.Parameters.Add(new NpgsqlParameter("order", NpgsqlDbType.Uuid) { Value = (object?)orderId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Varchar) { Value = (object?)entityType ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("entity", NpgsqlDbType.Uuid) { Value = (object?)entityId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new FinancialTransition(
                reader.GetGuid(0), reader.GetFieldValue<DateTime>(1), reader.GetString(2), reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetGuid(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        return list;
    }

    public async Task<WorkflowSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(
            """
            SELECT
              (SELECT count(*) FROM payments.reconciliation_tasks WHERE state = 'NeedsReview'),
              (SELECT count(*) FROM payments.reconciliation_tasks WHERE state IN ('Pending', 'Processing')),
              (SELECT count(*) FROM payments.refunds WHERE state IN ('Failed', 'NeedsReview')),
              (SELECT count(*) FROM payments.refunds WHERE state IN ('Requested', 'Unknown', 'Pending')),
              (SELECT count(*) FROM payments.outbox_events WHERE state = 'NeedsReview'),
              (SELECT count(*) FROM payments.outbox_events WHERE state IN ('Pending', 'Processing')),
              (SELECT count(*) FROM payments.orders WHERE status = 'RefundRequired')
            """, cancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var summary = new
        {
            A = (int)reader.GetInt64(0), B = (int)reader.GetInt64(1), C = (int)reader.GetInt64(2), D = (int)reader.GetInt64(3),
            E = (int)reader.GetInt64(4), F = (int)reader.GetInt64(5), G = (int)reader.GetInt64(6)
        };
        await reader.CloseAsync();

        // Unresolved financial items are always counted from the durable tables, with ages on the database clock.
        int openExceptions;
        long? oldestException;
        await using (var cmd2 = await CommandAsync(
            "SELECT count(*), floor(extract(epoch FROM (now() - min(detected_at_utc))))::bigint FROM payments.payment_exceptions WHERE state = 'Open'", cancellationToken))
        {
            await using var r2 = await cmd2.ExecuteReaderAsync(cancellationToken);
            await r2.ReadAsync(cancellationToken);
            openExceptions = (int)r2.GetInt64(0);
            oldestException = r2.IsDBNull(1) ? null : Math.Max(0, r2.GetInt64(1));
        }

        int openAnomalies;
        long? oldestAnomaly;
        var anomaliesByKind = new Dictionary<string, int>();
        await using (var cmd3 = await CommandAsync(
            "SELECT kind, count(*), floor(extract(epoch FROM (now() - min(detected_at_utc))))::bigint FROM payments.payment_anomalies WHERE state = 'Open' GROUP BY kind", cancellationToken))
        {
            await using var r3 = await cmd3.ExecuteReaderAsync(cancellationToken);
            openAnomalies = 0;
            oldestAnomaly = null;
            while (await r3.ReadAsync(cancellationToken))
            {
                var count = (int)r3.GetInt64(1);
                anomaliesByKind[r3.GetString(0)] = count;
                openAnomalies += count;
                var age = r3.IsDBNull(2) ? (long?)null : Math.Max(0, r3.GetInt64(2));
                if (age is { } a && (oldestAnomaly is null || a > oldestAnomaly))
                    oldestAnomaly = a;
            }
        }

        return new WorkflowSummary(
            summary.A, summary.B, summary.C, summary.D, summary.E, summary.F, summary.G,
            await CountByStateAsync("reconciliation_tasks", cancellationToken),
            await CountByStateAsync("refunds", cancellationToken),
            await CountByStateAsync("outbox_events", cancellationToken))
        {
            PaymentExceptionsOpen = openExceptions,
            PaymentAnomaliesOpen = openAnomalies,
            PaymentAnomaliesOpenByKind = anomaliesByKind,
            OldestOpenPaymentAnomalyAgeSeconds = oldestAnomaly,
            OldestOpenPaymentExceptionAgeSeconds = oldestException
        };
    }

    private async Task<IReadOnlyDictionary<string, int>> CountByStateAsync(string table, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>();
        await using var cmd = await CommandAsync($"SELECT state, count(*) FROM payments.{table} GROUP BY state", cancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            counts[reader.GetString(0)] = (int)reader.GetInt64(1);
        return counts;
    }

    private const string AdminOrderSelect =
        """
        SELECT o.id, o.status, o.total_amount, o.currency, o.created_at_utc, o.updated_at_utc, r.id, r.state, a.provider, a.is_simulated
        FROM payments.orders o
        LEFT JOIN payments.refunds r ON r.order_id = o.id
        LEFT JOIN LATERAL (SELECT provider, is_simulated FROM payments.payment_attempts WHERE order_id = o.id ORDER BY attempt_number DESC LIMIT 1) a ON true
        """;

    private static AdminOrder MapAdminOrder(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.GetString(3), r.GetFieldValue<DateTime>(4), r.GetFieldValue<DateTime>(5),
        r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetBoolean(9));

    public async Task<IReadOnlyList<AdminOrder>> ListOrdersForOperationsAsync(string? status, int limit, CancellationToken cancellationToken = default)
    {
        var list = new List<AdminOrder>();
        await using var cmd = await CommandAsync(
            AdminOrderSelect + " WHERE (@status::text IS NULL OR o.status = @status) ORDER BY o.updated_at_utc DESC LIMIT @limit", cancellationToken);
        cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Varchar) { Value = (object?)status ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(MapAdminOrder(reader));
        return list;
    }

    public async Task<AdminOrder?> GetOrderForOperationsAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var cmd = await CommandAsync(AdminOrderSelect + " WHERE o.id = @id", cancellationToken);
        cmd.Parameters.AddWithValue("id", orderId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAdminOrder(reader) : null;
    }

    // ───────────── helpers ─────────────

    private Task AuditAsync(Guid actor, string action, string subjectType, Guid subjectId, string? detail, CancellationToken cancellationToken) =>
        ExecAsync(
            "INSERT INTO payments.admin_audit_log (id, actor_user_id, action, subject_type, subject_id, detail, created_at_utc) VALUES (gen_random_uuid(), @actor, @action, @type, @subject, @detail, now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue("actor", actor);
                cmd.Parameters.AddWithValue("action", action);
                cmd.Parameters.AddWithValue("type", subjectType);
                cmd.Parameters.AddWithValue("subject", subjectId);
                cmd.Parameters.Add(new NpgsqlParameter("detail", NpgsqlDbType.Varchar) { Value = (object?)Truncate(detail, 500) ?? DBNull.Value });
            }, cancellationToken);

    private static void Backoff(NpgsqlCommand cmd, WorkflowOptions options)
    {
        cmd.Parameters.AddWithValue("max", options.MaxAttempts);
        cmd.Parameters.AddWithValue("base", options.BackoffBase.TotalSeconds);
        cmd.Parameters.AddWithValue("max_backoff", options.MaxBackoff.TotalSeconds);
    }

    private static ReconciliationTask MapTask(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
        r.GetFieldValue<DateTime>(7), r.GetFieldValue<DateTime>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTime>(9));

    private static Refund MapRefund(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetDecimal(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetBoolean(8), r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetGuid(13), r.GetFieldValue<DateTime>(14), r.IsDBNull(15) ? null : r.GetFieldValue<DateTime>(15),
        r.GetFieldValue<DateTime>(16), r.GetFieldValue<DateTime>(17))
    {
        ManualReference = r.IsDBNull(18) ? null : r.GetString(18),
        ManualNote = r.IsDBNull(19) ? null : r.GetString(19),
        ManuallyRecordedBy = r.IsDBNull(20) ? null : r.GetGuid(20),
        ManuallyRecordedAtUtc = r.IsDBNull(21) ? null : r.GetFieldValue<DateTime>(21)
    };

    private static PaymentException MapException(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetBoolean(5), r.GetDecimal(6), r.GetString(7),
        r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTime>(10), r.GetString(11),
        r.GetFieldValue<DateTime>(12), r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetGuid(15),
        r.IsDBNull(16) ? null : r.GetFieldValue<DateTime>(16), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetString(18),
        r.GetString(19), r.GetFieldValue<DateTime>(20), r.IsDBNull(21) ? null : r.GetString(21));

    private static OutboxEvent MapOutbox(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTime>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTime>(7),
        r.GetFieldValue<DateTime>(8), r.GetFieldValue<DateTime>(9));

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private async Task<int> ExecAsync(string sql, Action<NpgsqlCommand>? bind, CancellationToken cancellationToken)
    {
        // A statement outside any transaction still runs in one (a single statement is atomic either way), so the transition
        // context reaches the database for it too.
        if (db.Database.CurrentTransaction is not null)
            return await RunAsync(sql, bind, cancellationToken);

        await using var tx = await BeginAsync(cancellationToken);
        var rows = await RunAsync(sql, bind, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    private async Task<int> RunAsync(string sql, Action<NpgsqlCommand>? bind, CancellationToken cancellationToken)
    {
        await using var cmd = await CommandAsync(sql, cancellationToken);
        bind?.Invoke(cmd);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

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

/// <summary>SQL shared by every path that creates a refund (the system on a failed sale, or an administrator).</summary>
internal static class RefundSql
{
    public const string InsertForOrder =
        """
        INSERT INTO payments.refunds
            (id, order_id, payment_attempt_id, amount, currency, reason, state, provider, is_simulated, submission_number,
             attempts, next_attempt_at_utc, requested_by, created_at_utc, updated_at_utc)
        SELECT gen_random_uuid(), o.id, a.id, o.total_amount, o.currency, @reason, 'Requested', a.provider, a.is_simulated, 1,
               0, now(), @requested_by, now(), now()
        FROM payments.orders o
        JOIN LATERAL (SELECT * FROM payments.payment_attempts WHERE order_id = o.id AND status = 'Succeeded'
                      ORDER BY completed_at_utc DESC LIMIT 1) a ON true
        WHERE o.id = @order_id AND o.status = 'RefundRequired'
        ON CONFLICT (order_id) DO NOTHING
        """;
}

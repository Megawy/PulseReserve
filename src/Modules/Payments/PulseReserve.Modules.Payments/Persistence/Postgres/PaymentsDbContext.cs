using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Payments.Domain;

namespace PulseReserve.Modules.Payments.Persistence.Postgres;

/// <summary>
/// EF Core context owning the <c>payments</c> schema (ADR-008). It defines tables, constraints and migrations; the
/// concurrency-critical statements run as explicit SQL in <see cref="OrderStore"/>. The order snapshot (customer,
/// reservation, event, currency, total, deadline and every line) is made immutable by triggers in the migration.
/// </summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public const string Schema = "payments";

    internal DbSet<OrderRow> Orders => Set<OrderRow>();
    internal DbSet<OrderItemRow> OrderItems => Set<OrderItemRow>();
    internal DbSet<PaymentAttemptRow> PaymentAttempts => Set<PaymentAttemptRow>();
    internal DbSet<ReconciliationTaskRow> ReconciliationTasks => Set<ReconciliationTaskRow>();
    internal DbSet<RefundRow> Refunds => Set<RefundRow>();
    internal DbSet<OutboxEventRow> OutboxEvents => Set<OutboxEventRow>();
    internal DbSet<TicketRow> Tickets => Set<TicketRow>();
    internal DbSet<NotificationDeliveryRow> NotificationDeliveries => Set<NotificationDeliveryRow>();
    internal DbSet<AuditLogRow> AuditLog => Set<AuditLogRow>();
    internal DbSet<PaymentExceptionRow> PaymentExceptions => Set<PaymentExceptionRow>();
    internal DbSet<FinancialTransitionRow> FinancialTransitions => Set<FinancialTransitionRow>();
    internal DbSet<PaymentAnomalyRow> PaymentAnomalies => Set<PaymentAnomalyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<OrderRow>(b =>
        {
            b.ToTable("orders", t =>
            {
                t.HasCheckConstraint("chk_orders_status",
                    $"status IN ('{OrderStatus.PendingPayment}', '{OrderStatus.Paid}', '{OrderStatus.Cancelled}', '{OrderStatus.Expired}', '{OrderStatus.RefundRequired}', '{OrderStatus.Refunded}')");
                t.HasCheckConstraint("chk_orders_total_non_negative", "total_amount >= 0");
                t.HasCheckConstraint("chk_orders_currency_format", "currency ~ '^[A-Z]{3}$'");
            });
            b.HasKey(x => x.Id).HasName("pk_orders");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.CustomerId).HasColumnName("customer_id");
            b.Property(x => x.ReservationId).HasColumnName("reservation_id");
            b.Property(x => x.EventId).HasColumnName("event_id");
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(OrderPolicy.MaxIdempotencyKeyLength).IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
            b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            b.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 4);
            b.Property(x => x.PaymentDeadlineUtc).HasColumnName("payment_deadline_utc");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");

            // The same reservation can never produce a second order, whatever idempotency key the request carries.
            b.HasIndex(x => x.ReservationId).IsUnique().HasDatabaseName("uq_orders_reservation");
            b.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_orders_customer_idempotency_key");
            b.HasIndex(x => new { x.CustomerId, x.CreatedAtUtc }).HasDatabaseName("idx_orders_customer_created");
        });

        modelBuilder.Entity<OrderItemRow>(b =>
        {
            b.ToTable("order_items", t =>
            {
                t.HasCheckConstraint("chk_order_items_quantity", "quantity = 1");
                t.HasCheckConstraint("chk_order_items_price_non_negative", "unit_price_amount >= 0");
                t.HasCheckConstraint("chk_order_items_line_total", "line_total_amount = unit_price_amount * quantity");
            });
            b.HasKey(x => new { x.OrderId, x.SeatId }).HasName("pk_order_items");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.SeatId).HasColumnName("seat_id");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.UnitPriceAmount).HasColumnName("unit_price_amount").HasPrecision(18, 4);
            b.Property(x => x.LineTotalAmount).HasColumnName("line_total_amount").HasPrecision(18, 4);
            b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();

            b.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId)
                .HasConstraintName("fk_order_items_orders").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentAttemptRow>(b =>
        {
            b.ToTable("payment_attempts", t =>
            {
                t.HasCheckConstraint("chk_payment_attempts_status",
                    $"status IN ('{PaymentAttemptStatus.Initiated}', '{PaymentAttemptStatus.Succeeded}', '{PaymentAttemptStatus.Failed}', '{PaymentAttemptStatus.Cancelled}')");
            });
            b.HasKey(x => x.Id).HasName("pk_payment_attempts");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.AttemptNumber).HasColumnName("attempt_number");
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(OrderPolicy.MaxIdempotencyKeyLength).IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(50).IsRequired();
            b.Property(x => x.IsSimulated).HasColumnName("is_simulated");
            b.Property(x => x.ProviderReference).HasColumnName("provider_reference").HasMaxLength(200);
            b.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(100);
            b.Property(x => x.CheckoutUrl).HasColumnName("checkout_url").HasMaxLength(2000);
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");

            b.HasIndex(x => new { x.OrderId, x.AttemptNumber }).IsUnique().HasDatabaseName("uq_payment_attempts_order_number");
            b.HasIndex(x => new { x.OrderId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_payment_attempts_order_idempotency_key");
            // At most one in-flight attempt per order, decided by PostgreSQL.
            b.HasIndex(x => x.OrderId).IsUnique().HasFilter($"status = '{PaymentAttemptStatus.Initiated}'")
                .HasDatabaseName("uq_payment_attempts_one_initiated_per_order");
            b.HasIndex(x => new { x.Provider, x.ProviderReference }).IsUnique().HasFilter("provider_reference IS NOT NULL")
                .HasDatabaseName("uq_payment_attempts_provider_reference");

            b.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId)
                .HasConstraintName("fk_payment_attempts_orders").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReconciliationTaskRow>(b =>
        {
            b.ToTable("reconciliation_tasks", t =>
            {
                t.HasCheckConstraint("chk_reconciliation_tasks_state", WorkStates);
                t.HasCheckConstraint("chk_reconciliation_tasks_kind",
                    $"kind IN ('{ReconciliationKind.FinalizeSuccessfulPayment}', '{ReconciliationKind.ResolveStaleAttempt}', '{ReconciliationKind.ExpireOrder}')");
                t.HasCheckConstraint("chk_reconciliation_tasks_attempts", "attempts >= 0");
            });
            b.HasKey(x => x.Id).HasName("pk_reconciliation_tasks");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40).IsRequired();
            b.Property(x => x.SubjectId).HasColumnName("subject_id");
            b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).IsRequired();
            b.Property(x => x.Attempts).HasColumnName("attempts");
            b.Property(x => x.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
            b.Property(x => x.LockedUntilUtc).HasColumnName("locked_until_utc");
            b.Property(x => x.LastOutcome).HasColumnName("last_outcome").HasMaxLength(100);
            b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(300);
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            b.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");

            // Discovery is idempotent: one task per (kind, subject), however often a scanner finds the same problem.
            b.HasIndex(x => new { x.Kind, x.SubjectId }).IsUnique().HasDatabaseName("uq_reconciliation_tasks_kind_subject");
            b.HasIndex(x => new { x.State, x.NextAttemptAtUtc }).HasDatabaseName("idx_reconciliation_tasks_state_next");
        });

        modelBuilder.Entity<RefundRow>(b =>
        {
            b.ToTable("refunds", t =>
            {
                t.HasCheckConstraint("chk_refunds_state",
                    $"state IN ('{RefundState.Requested}', '{RefundState.Unknown}', '{RefundState.Pending}', '{RefundState.Succeeded}', '{RefundState.Failed}', '{RefundState.NeedsReview}', '{RefundState.ManuallyRecorded}')");
                // A manual record is all or nothing: who, when, and only in its own state. Provider confirmation can never
                // be mistaken for it (they use different columns and different states).
                t.HasCheckConstraint("chk_refunds_manual_fields",
                    $"(state = '{RefundState.ManuallyRecorded}') = (manual_recorded_by IS NOT NULL AND manual_recorded_at_utc IS NOT NULL) " +
                    $"AND (state = '{RefundState.ManuallyRecorded}' OR (manual_reference IS NULL AND manual_note IS NULL))");
                t.HasCheckConstraint("chk_refunds_amount_positive", "amount > 0");
                t.HasCheckConstraint("chk_refunds_currency_format", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("chk_refunds_counters", "attempts >= 0 AND submission_number >= 1");
            });
            b.HasKey(x => x.Id).HasName("pk_refunds");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.PaymentAttemptId).HasColumnName("payment_attempt_id");
            b.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 4);
            b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(40).IsRequired();
            b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).IsRequired();
            b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(50).IsRequired();
            b.Property(x => x.IsSimulated).HasColumnName("is_simulated");
            b.Property(x => x.SubmissionNumber).HasColumnName("submission_number");
            b.Property(x => x.ProviderRefundId).HasColumnName("provider_refund_id").HasMaxLength(200);
            b.Property(x => x.Attempts).HasColumnName("attempts");
            b.Property(x => x.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
            b.Property(x => x.LockedUntilUtc).HasColumnName("locked_until_utc");
            b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(300);
            b.Property(x => x.RequestedBy).HasColumnName("requested_by");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            b.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            b.Property(x => x.ManualReference).HasColumnName("manual_reference").HasMaxLength(200);
            b.Property(x => x.ManualNote).HasColumnName("manual_note").HasMaxLength(300);
            b.Property(x => x.ManualRecordedBy).HasColumnName("manual_recorded_by");
            b.Property(x => x.ManualRecordedAtUtc).HasColumnName("manual_recorded_at_utc");

            // A payment can be refunded at most once: a second refund for an order is impossible by construction.
            b.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("uq_refunds_order");
            b.HasIndex(x => new { x.State, x.NextAttemptAtUtc }).HasDatabaseName("idx_refunds_state_next");
            b.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId)
                .HasConstraintName("fk_refunds_orders").OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PaymentAttemptRow>().WithMany().HasForeignKey(x => x.PaymentAttemptId)
                .HasConstraintName("fk_refunds_payment_attempts").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentExceptionRow>(b =>
        {
            b.ToTable("payment_exceptions", t =>
            {
                t.HasCheckConstraint("chk_payment_exceptions_kind", $"kind IN ('{PaymentExceptionKind.LateSuccessAfterFailedAttempt}')");
                t.HasCheckConstraint("chk_payment_exceptions_state", $"state IN ('{PaymentExceptionState.Open}', '{PaymentExceptionState.Resolved}')");
                t.HasCheckConstraint("chk_payment_exceptions_resolution_value", $"resolution IS NULL OR resolution IN ('{PaymentExceptionResolution.RefundedExternally}')");
                // A resolution is all or nothing: who, when and what, and only in the Resolved state.
                t.HasCheckConstraint("chk_payment_exceptions_resolution_fields",
                    $"(state = '{PaymentExceptionState.Resolved}') = (resolution IS NOT NULL AND resolved_by IS NOT NULL AND resolved_at_utc IS NOT NULL) " +
                    $"AND (state = '{PaymentExceptionState.Resolved}' OR (resolution_reference IS NULL AND resolution_note IS NULL))");
                t.HasCheckConstraint("chk_payment_exceptions_amount_positive", "amount > 0");
                t.HasCheckConstraint("chk_payment_exceptions_currency_format", "currency ~ '^[A-Z]{3}$'");
            });
            b.HasKey(x => x.Id).HasName("pk_payment_exceptions");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40).IsRequired();
            b.Property(x => x.AttemptId).HasColumnName("attempt_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(50).IsRequired();
            b.Property(x => x.IsSimulated).HasColumnName("is_simulated");
            b.Property(x => x.ProviderEventId).HasColumnName("provider_event_id").HasMaxLength(200);
            b.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 4);
            b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            b.Property(x => x.OriginalAttemptStatus).HasColumnName("original_attempt_status").HasMaxLength(16).IsRequired();
            b.Property(x => x.OriginalFailureCode).HasColumnName("original_failure_code").HasMaxLength(100);
            b.Property(x => x.OriginalCompletedAtUtc).HasColumnName("original_completed_at_utc");
            b.Property(x => x.OrderStatusAtDetection).HasColumnName("order_status_at_detection").HasMaxLength(20).IsRequired();
            b.Property(x => x.DetectedAtUtc).HasColumnName("detected_at_utc");
            b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).IsRequired();
            b.Property(x => x.Resolution).HasColumnName("resolution").HasMaxLength(40);
            b.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
            b.Property(x => x.ResolvedAtUtc).HasColumnName("resolved_at_utc");
            b.Property(x => x.ResolutionReference).HasColumnName("resolution_reference").HasMaxLength(200);
            b.Property(x => x.ResolutionNote).HasColumnName("resolution_note").HasMaxLength(300);

            // One exception per (attempt, kind): however many times the provider repeats the event, it is recorded once.
            b.HasIndex(x => new { x.AttemptId, x.Kind }).IsUnique().HasDatabaseName("uq_payment_exceptions_attempt_kind");
            b.HasIndex(x => new { x.State, x.DetectedAtUtc }).HasDatabaseName("idx_payment_exceptions_state_detected");
            b.HasIndex(x => x.OrderId).HasDatabaseName("idx_payment_exceptions_order");
            b.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId)
                .HasConstraintName("fk_payment_exceptions_orders").OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PaymentAttemptRow>().WithMany().HasForeignKey(x => x.AttemptId)
                .HasConstraintName("fk_payment_exceptions_payment_attempts").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentAnomalyRow>(b =>
        {
            b.ToTable("payment_anomalies", t =>
            {
                t.HasCheckConstraint("chk_payment_anomalies_kind",
                    $"kind IN ('{PaymentAnomalyKind.AmountOrCurrencyMismatch}', '{PaymentAnomalyKind.ReferenceMismatch}', '{PaymentAnomalyKind.UnknownPaymentReference}', '{PaymentAnomalyKind.InconsistentAssociation}')");
                t.HasCheckConstraint("chk_payment_anomalies_state", $"state IN ('{PaymentAnomalyState.Open}', '{PaymentAnomalyState.Resolved}')");
                t.HasCheckConstraint("chk_payment_anomalies_resolution_value",
                    $"resolution IS NULL OR resolution IN ('{PaymentAnomalyResolution.NotOurPayment}', '{PaymentAnomalyResolution.RefundedExternally}', '{PaymentAnomalyResolution.NoActionNeeded}')");
                t.HasCheckConstraint("chk_payment_anomalies_resolution_fields",
                    $"(state = '{PaymentAnomalyState.Resolved}') = (resolution IS NOT NULL AND resolved_by IS NOT NULL AND resolved_at_utc IS NOT NULL) " +
                    $"AND (state = '{PaymentAnomalyState.Resolved}' OR (resolution_reference IS NULL AND resolution_note IS NULL))");
                // No false association: an event that matched nothing, or matched inconsistently, is never linked to an attempt or order.
                t.HasCheckConstraint("chk_payment_anomalies_association",
                    $"(kind IN ('{PaymentAnomalyKind.UnknownPaymentReference}', '{PaymentAnomalyKind.InconsistentAssociation}') AND attempt_id IS NULL AND order_id IS NULL) " +
                    $"OR (kind IN ('{PaymentAnomalyKind.AmountOrCurrencyMismatch}', '{PaymentAnomalyKind.ReferenceMismatch}') AND attempt_id IS NOT NULL AND order_id IS NOT NULL)");
                t.HasCheckConstraint("chk_payment_anomalies_mismatch_has_expected",
                    $"kind <> '{PaymentAnomalyKind.AmountOrCurrencyMismatch}' OR (expected_amount_minor IS NOT NULL AND expected_currency IS NOT NULL)");
                t.HasCheckConstraint("chk_payment_anomalies_currency_format",
                    "(received_currency IS NULL OR received_currency ~ '^[A-Z]{3}$') AND (expected_currency IS NULL OR expected_currency ~ '^[A-Z]{3}$')");
            });
            b.HasKey(x => x.Id).HasName("pk_payment_anomalies");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40).IsRequired();
            b.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(60).IsRequired();
            b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).IsRequired();
            b.Property(x => x.DedupeKey).HasColumnName("dedupe_key").HasMaxLength(300).IsRequired();
            b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(50).IsRequired();
            b.Property(x => x.ProviderEventId).HasColumnName("provider_event_id").HasMaxLength(200);
            b.Property(x => x.AttemptId).HasColumnName("attempt_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.ClaimedAttemptId).HasColumnName("claimed_attempt_id");
            b.Property(x => x.ClaimedOrderId).HasColumnName("claimed_order_id");
            b.Property(x => x.ReceivedAmountMinor).HasColumnName("received_amount_minor");
            b.Property(x => x.ReceivedCurrency).HasColumnName("received_currency").HasMaxLength(3);
            b.Property(x => x.ExpectedAmountMinor).HasColumnName("expected_amount_minor");
            b.Property(x => x.ExpectedCurrency).HasColumnName("expected_currency").HasMaxLength(3);
            b.Property(x => x.OrderStatusAtDetection).HasColumnName("order_status_at_detection").HasMaxLength(20);
            b.Property(x => x.AttemptStatusAtDetection).HasColumnName("attempt_status_at_detection").HasMaxLength(16);
            b.Property(x => x.DetectedAtUtc).HasColumnName("detected_at_utc");
            b.Property(x => x.Resolution).HasColumnName("resolution").HasMaxLength(40);
            b.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
            b.Property(x => x.ResolvedAtUtc).HasColumnName("resolved_at_utc");
            b.Property(x => x.ResolutionReference).HasColumnName("resolution_reference").HasMaxLength(200);
            b.Property(x => x.ResolutionNote).HasColumnName("resolution_note").HasMaxLength(300);

            // The same provider event is one anomaly however often it is redelivered.
            b.HasIndex(x => new { x.Kind, x.DedupeKey }).IsUnique().HasDatabaseName("uq_payment_anomalies_kind_dedupe");
            b.HasIndex(x => new { x.State, x.DetectedAtUtc }).HasDatabaseName("idx_payment_anomalies_state_detected");
            b.HasIndex(x => x.OrderId).HasDatabaseName("idx_payment_anomalies_order");
            b.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId)
                .HasConstraintName("fk_payment_anomalies_orders").OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PaymentAttemptRow>().WithMany().HasForeignKey(x => x.AttemptId)
                .HasConstraintName("fk_payment_anomalies_payment_attempts").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialTransitionRow>(b =>
        {
            b.ToTable("financial_transitions", t =>
            {
                t.HasCheckConstraint("chk_financial_transitions_kind", "kind IN ('Transition', 'Rejected')");
            });
            b.HasKey(x => x.Id).HasName("pk_financial_transitions");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            b.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(24).IsRequired();
            b.Property(x => x.EntityId).HasColumnName("entity_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.FromState).HasColumnName("from_state").HasMaxLength(20);
            b.Property(x => x.ToState).HasColumnName("to_state").HasMaxLength(20).IsRequired();
            b.Property(x => x.EventCode).HasColumnName("event_code").HasMaxLength(80).IsRequired();
            b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(12).IsRequired();
            b.Property(x => x.Source).HasColumnName("source").HasMaxLength(24).IsRequired();
            b.Property(x => x.FactBasis).HasColumnName("fact_basis").HasMaxLength(24).IsRequired();
            b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(50);
            b.Property(x => x.ProviderEventId).HasColumnName("provider_event_id").HasMaxLength(200);
            b.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            b.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(100);

            // Deliberately NO foreign keys: the history must outlive and never block any other row. Investigation queries go by
            // order or by entity, newest first.
            b.HasIndex(x => new { x.OrderId, x.OccurredAtUtc }).HasDatabaseName("idx_financial_transitions_order");
            b.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAtUtc }).HasDatabaseName("idx_financial_transitions_entity");
            // A rejected/conflicting event is recorded once per provider event, however often the provider redelivers it.
            b.HasIndex(x => new { x.EntityType, x.EntityId, x.EventCode, x.ProviderEventId }).IsUnique()
                .HasFilter("kind = 'Rejected' AND provider_event_id IS NOT NULL").HasDatabaseName("uq_financial_transitions_rejected_event");
        });

        modelBuilder.Entity<OutboxEventRow>(b =>
        {
            b.ToTable("outbox_events", t =>
            {
                t.HasCheckConstraint("chk_outbox_events_state", WorkStates);
                t.HasCheckConstraint("chk_outbox_events_type", $"type IN ('{OutboxEventType.IssueTickets}', '{OutboxEventType.NotifyOrderPaid}')");
                t.HasCheckConstraint("chk_outbox_events_attempts", "attempts >= 0");
            });
            b.HasKey(x => x.Id).HasName("pk_outbox_events");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.Type).HasColumnName("type").HasMaxLength(40).IsRequired();
            b.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).IsRequired();
            b.Property(x => x.Attempts).HasColumnName("attempts");
            b.Property(x => x.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
            b.Property(x => x.LockedUntilUtc).HasColumnName("locked_until_utc");
            b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(300);
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            b.Property(x => x.ProcessedAtUtc).HasColumnName("processed_at_utc");

            // At most one event of each type per order: a paid order's work cannot be enqueued twice.
            b.HasIndex(x => new { x.Type, x.AggregateId }).IsUnique().HasDatabaseName("uq_outbox_events_type_aggregate");
            b.HasIndex(x => new { x.State, x.NextAttemptAtUtc }).HasDatabaseName("idx_outbox_events_state_next");
            b.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.AggregateId)
                .HasConstraintName("fk_outbox_events_orders").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TicketRow>(b =>
        {
            b.ToTable("tickets", t => t.HasCheckConstraint("chk_tickets_status", "status IN ('Valid')"));
            b.HasKey(x => x.Id).HasName("pk_tickets");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.PublicId).HasColumnName("public_id").HasMaxLength(64).IsRequired();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.SeatId).HasColumnName("seat_id");
            b.Property(x => x.EventId).HasColumnName("event_id");
            b.Property(x => x.CustomerId).HasColumnName("customer_id");
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            b.Property(x => x.IssuedAtUtc).HasColumnName("issued_at_utc");

            // Exactly one ticket per purchased seat of an order, whatever retries or concurrent workers do.
            b.HasIndex(x => new { x.OrderId, x.SeatId }).IsUnique().HasDatabaseName("uq_tickets_order_seat");
            b.HasIndex(x => x.PublicId).IsUnique().HasDatabaseName("uq_tickets_public_id");
            b.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId)
                .HasConstraintName("fk_tickets_orders").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NotificationDeliveryRow>(b =>
        {
            b.ToTable("notification_deliveries");
            b.HasKey(x => x.Id).HasName("pk_notification_deliveries");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40).IsRequired();
            b.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(40).IsRequired();
            b.Property(x => x.SentAtUtc).HasColumnName("sent_at_utc");
            // Redelivery of the event never sends the same notification twice.
            b.HasIndex(x => new { x.OrderId, x.Kind }).IsUnique().HasDatabaseName("uq_notification_deliveries_order_kind");
            b.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId)
                .HasConstraintName("fk_notification_deliveries_orders").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLogRow>(b =>
        {
            b.ToTable("admin_audit_log");
            b.HasKey(x => x.Id).HasName("pk_admin_audit_log");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            b.Property(x => x.Action).HasColumnName("action").HasMaxLength(60).IsRequired();
            b.Property(x => x.SubjectType).HasColumnName("subject_type").HasMaxLength(40).IsRequired();
            b.Property(x => x.SubjectId).HasColumnName("subject_id");
            b.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(500);
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.HasIndex(x => x.CreatedAtUtc).HasDatabaseName("idx_admin_audit_log_created");
        });
    }

    private static readonly string WorkStates =
        $"state IN ('{WorkState.Pending}', '{WorkState.Processing}', '{WorkState.Done}', '{WorkState.NeedsReview}')";
}

internal sealed class OrderRow
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid EventId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = OrderStatus.PendingPayment;
    public string Currency { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateTime PaymentDeadlineUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class OrderItemRow
{
    public Guid OrderId { get; set; }
    public Guid SeatId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPriceAmount { get; set; }
    public decimal LineTotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
}

internal sealed class PaymentAttemptRow
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int AttemptNumber { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = PaymentAttemptStatus.Initiated;
    public string Provider { get; set; } = string.Empty;
    public bool IsSimulated { get; set; }
    public string? ProviderReference { get; set; }
    public string? FailureCode { get; set; }
    public string? CheckoutUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

internal sealed class ReconciliationTaskRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public string State { get; set; } = WorkState.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LastOutcome { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

internal sealed class RefundRow
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid PaymentAttemptId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string State { get; set; } = RefundState.Requested;
    public string Provider { get; set; } = string.Empty;
    public bool IsSimulated { get; set; }
    public int SubmissionNumber { get; set; } = 1;
    public string? ProviderRefundId { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LastError { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ManualReference { get; set; }
    public string? ManualNote { get; set; }
    public Guid? ManualRecordedBy { get; set; }
    public DateTime? ManualRecordedAtUtc { get; set; }
}

internal sealed class PaymentExceptionRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = PaymentExceptionKind.LateSuccessAfterFailedAttempt;
    public Guid AttemptId { get; set; }
    public Guid OrderId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public bool IsSimulated { get; set; }
    public string? ProviderEventId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string OriginalAttemptStatus { get; set; } = string.Empty;
    public string? OriginalFailureCode { get; set; }
    public DateTime? OriginalCompletedAtUtc { get; set; }
    public string OrderStatusAtDetection { get; set; } = string.Empty;
    public DateTime DetectedAtUtc { get; set; }
    public string State { get; set; } = PaymentExceptionState.Open;
    public string? Resolution { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolutionReference { get; set; }
    public string? ResolutionNote { get; set; }
}

internal sealed class PaymentAnomalyRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string State { get; set; } = PaymentAnomalyState.Open;
    public string DedupeKey { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string? ProviderEventId { get; set; }
    public Guid? AttemptId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? ClaimedAttemptId { get; set; }
    public Guid? ClaimedOrderId { get; set; }
    public long? ReceivedAmountMinor { get; set; }
    public string? ReceivedCurrency { get; set; }
    public long? ExpectedAmountMinor { get; set; }
    public string? ExpectedCurrency { get; set; }
    public string? OrderStatusAtDetection { get; set; }
    public string? AttemptStatusAtDetection { get; set; }
    public DateTime DetectedAtUtc { get; set; }
    public string? Resolution { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolutionReference { get; set; }
    public string? ResolutionNote { get; set; }
}

internal sealed class FinancialTransitionRow
{
    public Guid Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public Guid? OrderId { get; set; }
    public string? FromState { get; set; }
    public string ToState { get; set; } = string.Empty;
    public string EventCode { get; set; } = string.Empty;
    public string Kind { get; set; } = "Transition";
    public string Source { get; set; } = string.Empty;
    public string FactBasis { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public string? ProviderEventId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Reason { get; set; }
}

internal sealed class OutboxEventRow
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public Guid AggregateId { get; set; }
    public string State { get; set; } = WorkState.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}

internal sealed class TicketRow
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public Guid OrderId { get; set; }
    public Guid SeatId { get; set; }
    public Guid EventId { get; set; }
    public Guid CustomerId { get; set; }
    public string Status { get; set; } = "Valid";
    public DateTime IssuedAtUtc { get; set; }
}

internal sealed class NotificationDeliveryRow
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; }
}

internal sealed class AuditLogRow
{
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public string? Detail { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

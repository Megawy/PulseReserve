using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Payments.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ReconciliationRefundsOutboxTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_orders_status",
                schema: "payments",
                table: "orders");

            migrationBuilder.CreateTable(
                name: "admin_audit_log",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_deliveries_orders",
                        column: x => x.order_id,
                        principalSchema: "payments",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox_events",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_events", x => x.id);
                    table.CheckConstraint("chk_outbox_events_attempts", "attempts >= 0");
                    table.CheckConstraint("chk_outbox_events_state", "state IN ('Pending', 'Processing', 'Done', 'NeedsReview')");
                    table.CheckConstraint("chk_outbox_events_type", "type IN ('IssueTickets', 'NotifyOrderPaid')");
                    table.ForeignKey(
                        name: "fk_outbox_events_orders",
                        column: x => x.aggregate_id,
                        principalSchema: "payments",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_tasks",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_tasks", x => x.id);
                    table.CheckConstraint("chk_reconciliation_tasks_attempts", "attempts >= 0");
                    table.CheckConstraint("chk_reconciliation_tasks_kind", "kind IN ('FinalizeSuccessfulPayment', 'ResolveStaleAttempt', 'ExpireOrder')");
                    table.CheckConstraint("chk_reconciliation_tasks_state", "state IN ('Pending', 'Processing', 'Done', 'NeedsReview')");
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_simulated = table.Column<bool>(type: "boolean", nullable: false),
                    submission_number = table.Column<int>(type: "integer", nullable: false),
                    provider_refund_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("chk_refunds_amount_positive", "amount > 0");
                    table.CheckConstraint("chk_refunds_counters", "attempts >= 0 AND submission_number >= 1");
                    table.CheckConstraint("chk_refunds_currency_format", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("chk_refunds_state", "state IN ('Requested', 'Unknown', 'Pending', 'Succeeded', 'Failed', 'NeedsReview')");
                    table.ForeignKey(
                        name: "fk_refunds_orders",
                        column: x => x.order_id,
                        principalSchema: "payments",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_payment_attempts",
                        column: x => x.payment_attempt_id,
                        principalSchema: "payments",
                        principalTable: "payment_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.CheckConstraint("chk_tickets_status", "status IN ('Valid')");
                    table.ForeignKey(
                        name: "fk_tickets_orders",
                        column: x => x.order_id,
                        principalSchema: "payments",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "chk_orders_status",
                schema: "payments",
                table: "orders",
                sql: "status IN ('PendingPayment', 'Paid', 'Cancelled', 'Expired', 'RefundRequired', 'Refunded')");

            migrationBuilder.CreateIndex(
                name: "idx_admin_audit_log_created",
                schema: "payments",
                table: "admin_audit_log",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "uq_notification_deliveries_order_kind",
                schema: "payments",
                table: "notification_deliveries",
                columns: new[] { "order_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_outbox_events_state_next",
                schema: "payments",
                table: "outbox_events",
                columns: new[] { "state", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_events_aggregate_id",
                schema: "payments",
                table: "outbox_events",
                column: "aggregate_id");

            migrationBuilder.CreateIndex(
                name: "uq_outbox_events_type_aggregate",
                schema: "payments",
                table: "outbox_events",
                columns: new[] { "type", "aggregate_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_reconciliation_tasks_state_next",
                schema: "payments",
                table: "reconciliation_tasks",
                columns: new[] { "state", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "uq_reconciliation_tasks_kind_subject",
                schema: "payments",
                table: "reconciliation_tasks",
                columns: new[] { "kind", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_refunds_state_next",
                schema: "payments",
                table: "refunds",
                columns: new[] { "state", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_refunds_payment_attempt_id",
                schema: "payments",
                table: "refunds",
                column: "payment_attempt_id");

            migrationBuilder.CreateIndex(
                name: "uq_refunds_order",
                schema: "payments",
                table: "refunds",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_tickets_order_seat",
                schema: "payments",
                table: "tickets",
                columns: new[] { "order_id", "seat_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_tickets_public_id",
                schema: "payments",
                table: "tickets",
                column: "public_id",
                unique: true);

            migrationBuilder.Sql("""
                -- Order lifecycle guard: Paid and Refunded are final; RefundRequired may only become Refunded.
                CREATE OR REPLACE FUNCTION payments.forbid_order_snapshot_change() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'payments.orders rows cannot be deleted' USING ERRCODE = '23000';
                    END IF;
                    IF NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.customer_id IS DISTINCT FROM OLD.customer_id
                       OR NEW.reservation_id IS DISTINCT FROM OLD.reservation_id
                       OR NEW.event_id IS DISTINCT FROM OLD.event_id
                       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                       OR NEW.currency IS DISTINCT FROM OLD.currency
                       OR NEW.total_amount IS DISTINCT FROM OLD.total_amount
                       OR NEW.payment_deadline_utc IS DISTINCT FROM OLD.payment_deadline_utc
                       OR NEW.created_at_utc IS DISTINCT FROM OLD.created_at_utc THEN
                        RAISE EXCEPTION 'the price snapshot of an order is immutable' USING ERRCODE = '23000';
                    END IF;
                    IF OLD.status IN ('Paid', 'Refunded') AND NEW.status IS DISTINCT FROM OLD.status THEN
                        RAISE EXCEPTION 'order status % is final', OLD.status USING ERRCODE = '23000';
                    END IF;
                    IF OLD.status = 'RefundRequired' AND NEW.status NOT IN ('RefundRequired', 'Refunded') THEN
                        RAISE EXCEPTION 'a RefundRequired order can only become Refunded' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                -- Tickets exist only for Paid orders, and never change or disappear.
                CREATE FUNCTION payments.guard_ticket_insert() RETURNS trigger AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM payments.orders WHERE id = NEW.order_id AND status = 'Paid') THEN
                        RAISE EXCEPTION 'tickets can only be issued for a Paid order' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_tickets_insert_guard BEFORE INSERT ON payments.tickets
                    FOR EACH ROW EXECUTE FUNCTION payments.guard_ticket_insert();

                CREATE FUNCTION payments.forbid_row_change() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION '% rows are append-only', TG_TABLE_NAME USING ERRCODE = '23000';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_tickets_immutable BEFORE UPDATE OR DELETE ON payments.tickets
                    FOR EACH ROW EXECUTE FUNCTION payments.forbid_row_change();
                CREATE TRIGGER trg_audit_log_immutable BEFORE UPDATE OR DELETE ON payments.admin_audit_log
                    FOR EACH ROW EXECUTE FUNCTION payments.forbid_row_change();
                CREATE TRIGGER trg_notification_deliveries_immutable BEFORE UPDATE OR DELETE ON payments.notification_deliveries
                    FOR EACH ROW EXECUTE FUNCTION payments.forbid_row_change();

                -- A refund must refund exactly the order's recorded payment: same order, a Succeeded attempt of that
                -- order, the order's total and currency, and only for an order that required a refund.
                CREATE FUNCTION payments.guard_refund_insert() RETURNS trigger AS $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM payments.orders o
                        JOIN payments.payment_attempts a ON a.order_id = o.id AND a.id = NEW.payment_attempt_id AND a.status = 'Succeeded'
                        WHERE o.id = NEW.order_id AND o.status = 'RefundRequired'
                          AND o.total_amount = NEW.amount AND o.currency = NEW.currency) THEN
                        RAISE EXCEPTION 'a refund must match a RefundRequired order and its succeeded payment' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_refunds_insert_guard BEFORE INSERT ON payments.refunds
                    FOR EACH ROW EXECUTE FUNCTION payments.guard_refund_insert();

                -- Refund identity is immutable, a confirmed refund never changes, and refunds are never deleted.
                CREATE FUNCTION payments.guard_refund_update() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'refunds cannot be deleted' USING ERRCODE = '23000';
                    END IF;
                    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.order_id IS DISTINCT FROM OLD.order_id
                       OR NEW.payment_attempt_id IS DISTINCT FROM OLD.payment_attempt_id
                       OR NEW.amount IS DISTINCT FROM OLD.amount OR NEW.currency IS DISTINCT FROM OLD.currency
                       OR NEW.reason IS DISTINCT FROM OLD.reason OR NEW.created_at_utc IS DISTINCT FROM OLD.created_at_utc THEN
                        RAISE EXCEPTION 'refund identity and amount are immutable' USING ERRCODE = '23000';
                    END IF;
                    IF OLD.state = 'Succeeded' AND (NEW.state IS DISTINCT FROM OLD.state OR NEW.provider_refund_id IS DISTINCT FROM OLD.provider_refund_id) THEN
                        RAISE EXCEPTION 'a succeeded refund is final' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_refunds_guard_update BEFORE UPDATE OR DELETE ON payments.refunds
                    FOR EACH ROW EXECUTE FUNCTION payments.guard_refund_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refunds, tickets, the administrator audit log and the notification ledger are financial and customer-facing evidence. This
            // rollback drops them, so it REFUSES while any of them holds a row (the surrounding transaction changes nothing). Export first.
            // Reconciliation tasks and outbox events are derived work queues that the system rebuilds, so they do not block it.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM payments.refunds) THEN
                        RAISE EXCEPTION 'Refusing to roll back: payments.refunds holds refunds. Export them first.' USING ERRCODE = '23000';
                    END IF;
                    IF EXISTS (SELECT 1 FROM payments.tickets) THEN
                        RAISE EXCEPTION 'Refusing to roll back: payments.tickets holds issued tickets. Export them first.' USING ERRCODE = '23000';
                    END IF;
                    IF EXISTS (SELECT 1 FROM payments.admin_audit_log) THEN
                        RAISE EXCEPTION 'Refusing to roll back: payments.admin_audit_log holds the administrator audit trail. Export it first.' USING ERRCODE = '23000';
                    END IF;
                    IF EXISTS (SELECT 1 FROM payments.notification_deliveries) THEN
                        RAISE EXCEPTION 'Refusing to roll back: payments.notification_deliveries holds the notification ledger. Export it first.' USING ERRCODE = '23000';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_refunds_guard_update ON payments.refunds;
                DROP TRIGGER IF EXISTS trg_refunds_insert_guard ON payments.refunds;
                DROP TRIGGER IF EXISTS trg_notification_deliveries_immutable ON payments.notification_deliveries;
                DROP TRIGGER IF EXISTS trg_audit_log_immutable ON payments.admin_audit_log;
                DROP TRIGGER IF EXISTS trg_tickets_immutable ON payments.tickets;
                DROP TRIGGER IF EXISTS trg_tickets_insert_guard ON payments.tickets;
                DROP FUNCTION IF EXISTS payments.guard_refund_update();
                DROP FUNCTION IF EXISTS payments.guard_refund_insert();
                DROP FUNCTION IF EXISTS payments.forbid_row_change();
                DROP FUNCTION IF EXISTS payments.guard_ticket_insert();
                """);

            migrationBuilder.DropTable(
                name: "admin_audit_log",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "notification_deliveries",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "outbox_events",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "reconciliation_tasks",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "tickets",
                schema: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "chk_orders_status",
                schema: "payments",
                table: "orders");

            migrationBuilder.AddCheckConstraint(
                name: "chk_orders_status",
                schema: "payments",
                table: "orders",
                sql: "status IN ('PendingPayment', 'Paid', 'Cancelled', 'Expired', 'RefundRequired')");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Payments.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class LateSuccessPaymentExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_exceptions",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_simulated = table.Column<bool>(type: "boolean", nullable: false),
                    provider_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    original_attempt_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    original_failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    original_completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    order_status_at_detection = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detected_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    resolution = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolution_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_exceptions", x => x.id);
                    table.CheckConstraint("chk_payment_exceptions_amount_positive", "amount > 0");
                    table.CheckConstraint("chk_payment_exceptions_currency_format", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("chk_payment_exceptions_kind", "kind IN ('LateSuccessAfterFailedAttempt')");
                    table.CheckConstraint("chk_payment_exceptions_resolution_fields", "(state = 'Resolved') = (resolution IS NOT NULL AND resolved_by IS NOT NULL AND resolved_at_utc IS NOT NULL) AND (state = 'Resolved' OR (resolution_reference IS NULL AND resolution_note IS NULL))");
                    table.CheckConstraint("chk_payment_exceptions_resolution_value", "resolution IS NULL OR resolution IN ('RefundedExternally')");
                    table.CheckConstraint("chk_payment_exceptions_state", "state IN ('Open', 'Resolved')");
                    table.ForeignKey(
                        name: "fk_payment_exceptions_orders",
                        column: x => x.order_id,
                        principalSchema: "payments",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_exceptions_payment_attempts",
                        column: x => x.attempt_id,
                        principalSchema: "payments",
                        principalTable: "payment_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_payment_exceptions_order",
                schema: "payments",
                table: "payment_exceptions",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "idx_payment_exceptions_state_detected",
                schema: "payments",
                table: "payment_exceptions",
                columns: new[] { "state", "detected_at_utc" });

            migrationBuilder.CreateIndex(
                name: "uq_payment_exceptions_attempt_kind",
                schema: "payments",
                table: "payment_exceptions",
                columns: new[] { "attempt_id", "kind" },
                unique: true);

            migrationBuilder.Sql(
                """
                -- An exception names a Failed attempt of its own order and starts Open.
                CREATE FUNCTION payments.guard_payment_exception_insert() RETURNS trigger AS $$
                BEGIN
                    IF NEW.state <> 'Open' THEN
                        RAISE EXCEPTION 'a payment exception starts Open' USING ERRCODE = '23000';
                    END IF;
                    IF NOT EXISTS (
                        SELECT 1 FROM payments.payment_attempts a
                        WHERE a.id = NEW.attempt_id AND a.order_id = NEW.order_id AND a.status = 'Failed') THEN
                        RAISE EXCEPTION 'a payment exception must name a Failed attempt of its order' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_payment_exceptions_insert_guard BEFORE INSERT ON payments.payment_exceptions
                    FOR EACH ROW EXECUTE FUNCTION payments.guard_payment_exception_insert();

                -- The facts of an exception never change; only Open -> Resolved (with its resolution fields) is allowed, then final.
                CREATE FUNCTION payments.guard_payment_exception_update() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'payment exceptions cannot be deleted' USING ERRCODE = '23000';
                    END IF;
                    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.kind IS DISTINCT FROM OLD.kind
                       OR NEW.attempt_id IS DISTINCT FROM OLD.attempt_id OR NEW.order_id IS DISTINCT FROM OLD.order_id
                       OR NEW.provider IS DISTINCT FROM OLD.provider OR NEW.is_simulated IS DISTINCT FROM OLD.is_simulated
                       OR NEW.provider_event_id IS DISTINCT FROM OLD.provider_event_id
                       OR NEW.amount IS DISTINCT FROM OLD.amount OR NEW.currency IS DISTINCT FROM OLD.currency
                       OR NEW.original_attempt_status IS DISTINCT FROM OLD.original_attempt_status
                       OR NEW.original_failure_code IS DISTINCT FROM OLD.original_failure_code
                       OR NEW.original_completed_at_utc IS DISTINCT FROM OLD.original_completed_at_utc
                       OR NEW.order_status_at_detection IS DISTINCT FROM OLD.order_status_at_detection
                       OR NEW.detected_at_utc IS DISTINCT FROM OLD.detected_at_utc THEN
                        RAISE EXCEPTION 'the facts of a payment exception are immutable' USING ERRCODE = '23000';
                    END IF;
                    IF OLD.state = 'Resolved' AND (
                           NEW.state IS DISTINCT FROM OLD.state OR NEW.resolution IS DISTINCT FROM OLD.resolution
                           OR NEW.resolved_by IS DISTINCT FROM OLD.resolved_by OR NEW.resolved_at_utc IS DISTINCT FROM OLD.resolved_at_utc
                           OR NEW.resolution_reference IS DISTINCT FROM OLD.resolution_reference
                           OR NEW.resolution_note IS DISTINCT FROM OLD.resolution_note) THEN
                        RAISE EXCEPTION 'a resolved payment exception is final' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_payment_exceptions_guard_update BEFORE UPDATE OR DELETE ON payments.payment_exceptions
                    FOR EACH ROW EXECUTE FUNCTION payments.guard_payment_exception_update();

                -- The recorded verdict of an attempt that has an exception is evidence: it can never be rewritten.
                CREATE FUNCTION payments.guard_attempt_with_exception() RETURNS trigger AS $$
                BEGIN
                    IF OLD.status = 'Failed' AND NEW.status IS DISTINCT FROM OLD.status
                       AND EXISTS (SELECT 1 FROM payments.payment_exceptions e WHERE e.attempt_id = OLD.id) THEN
                        RAISE EXCEPTION 'the Failed verdict of an attempt with a payment exception cannot change' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_payment_attempts_keep_failed_verdict BEFORE UPDATE ON payments.payment_attempts
                    FOR EACH ROW EXECUTE FUNCTION payments.guard_attempt_with_exception();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The exception table is the only record of a verified provider success for an attempt recorded as failed, plus any
            // administrator statement about it. Like the manual-refund migration, this rollback therefore REFUSES to destroy
            // financial records: it fails while any row exists. If the rollback is really intended, export and reconcile the
            // rows, then drop the table by hand (row deletes are blocked by a trigger on purpose) and run the rollback again.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM payments.payment_exceptions) THEN
                        RAISE EXCEPTION 'Refusing to roll back: payments.payment_exceptions holds recorded payment exceptions. Export and reconcile them first.' USING ERRCODE = '23000';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_payment_attempts_keep_failed_verdict ON payments.payment_attempts;
                DROP FUNCTION IF EXISTS payments.guard_attempt_with_exception();
                """);

            migrationBuilder.DropTable(
                name: "payment_exceptions",
                schema: "payments");

            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS payments.guard_payment_exception_update();
                DROP FUNCTION IF EXISTS payments.guard_payment_exception_insert();
                """);
        }
    }
}

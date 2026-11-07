using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Payments.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PaymentAnomalies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_anomalies",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    provider_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    received_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    expected_amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    expected_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    order_status_at_detection = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    attempt_status_at_detection = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    detected_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolution = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolution_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_anomalies", x => x.id);
                    table.CheckConstraint("chk_payment_anomalies_association", "(kind IN ('UnknownPaymentReference', 'InconsistentAssociation') AND attempt_id IS NULL AND order_id IS NULL) OR (kind IN ('AmountOrCurrencyMismatch', 'ReferenceMismatch') AND attempt_id IS NOT NULL AND order_id IS NOT NULL)");
                    table.CheckConstraint("chk_payment_anomalies_currency_format", "(received_currency IS NULL OR received_currency ~ '^[A-Z]{3}$') AND (expected_currency IS NULL OR expected_currency ~ '^[A-Z]{3}$')");
                    table.CheckConstraint("chk_payment_anomalies_kind", "kind IN ('AmountOrCurrencyMismatch', 'ReferenceMismatch', 'UnknownPaymentReference', 'InconsistentAssociation')");
                    table.CheckConstraint("chk_payment_anomalies_mismatch_has_expected", "kind <> 'AmountOrCurrencyMismatch' OR (expected_amount_minor IS NOT NULL AND expected_currency IS NOT NULL)");
                    table.CheckConstraint("chk_payment_anomalies_resolution_fields", "(state = 'Resolved') = (resolution IS NOT NULL AND resolved_by IS NOT NULL AND resolved_at_utc IS NOT NULL) AND (state = 'Resolved' OR (resolution_reference IS NULL AND resolution_note IS NULL))");
                    table.CheckConstraint("chk_payment_anomalies_resolution_value", "resolution IS NULL OR resolution IN ('NotOurPayment', 'RefundedExternally', 'NoActionNeeded')");
                    table.CheckConstraint("chk_payment_anomalies_state", "state IN ('Open', 'Resolved')");
                    table.ForeignKey(
                        name: "fk_payment_anomalies_orders",
                        column: x => x.order_id,
                        principalSchema: "payments",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_anomalies_payment_attempts",
                        column: x => x.attempt_id,
                        principalSchema: "payments",
                        principalTable: "payment_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_payment_anomalies_order",
                schema: "payments",
                table: "payment_anomalies",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "idx_payment_anomalies_state_detected",
                schema: "payments",
                table: "payment_anomalies",
                columns: new[] { "state", "detected_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_anomalies_attempt_id",
                schema: "payments",
                table: "payment_anomalies",
                column: "attempt_id");

            migrationBuilder.CreateIndex(
                name: "uq_payment_anomalies_kind_dedupe",
                schema: "payments",
                table: "payment_anomalies",
                columns: new[] { "kind", "dedupe_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                -- The facts of an anomaly never change; only Open -> Resolved (with its resolution fields) is allowed, then final.
                CREATE FUNCTION payments.guard_payment_anomaly_update() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'payment anomalies cannot be deleted' USING ERRCODE = '23000';
                    END IF;
                    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.kind IS DISTINCT FROM OLD.kind OR NEW.reason_code IS DISTINCT FROM OLD.reason_code
                       OR NEW.dedupe_key IS DISTINCT FROM OLD.dedupe_key OR NEW.provider IS DISTINCT FROM OLD.provider
                       OR NEW.provider_event_id IS DISTINCT FROM OLD.provider_event_id
                       OR NEW.attempt_id IS DISTINCT FROM OLD.attempt_id OR NEW.order_id IS DISTINCT FROM OLD.order_id
                       OR NEW.claimed_attempt_id IS DISTINCT FROM OLD.claimed_attempt_id OR NEW.claimed_order_id IS DISTINCT FROM OLD.claimed_order_id
                       OR NEW.received_amount_minor IS DISTINCT FROM OLD.received_amount_minor OR NEW.received_currency IS DISTINCT FROM OLD.received_currency
                       OR NEW.expected_amount_minor IS DISTINCT FROM OLD.expected_amount_minor OR NEW.expected_currency IS DISTINCT FROM OLD.expected_currency
                       OR NEW.order_status_at_detection IS DISTINCT FROM OLD.order_status_at_detection
                       OR NEW.attempt_status_at_detection IS DISTINCT FROM OLD.attempt_status_at_detection
                       OR NEW.detected_at_utc IS DISTINCT FROM OLD.detected_at_utc THEN
                        RAISE EXCEPTION 'the facts of a payment anomaly are immutable' USING ERRCODE = '23000';
                    END IF;
                    IF OLD.state = 'Resolved' AND (
                           NEW.state IS DISTINCT FROM OLD.state OR NEW.resolution IS DISTINCT FROM OLD.resolution
                           OR NEW.resolved_by IS DISTINCT FROM OLD.resolved_by OR NEW.resolved_at_utc IS DISTINCT FROM OLD.resolved_at_utc
                           OR NEW.resolution_reference IS DISTINCT FROM OLD.resolution_reference
                           OR NEW.resolution_note IS DISTINCT FROM OLD.resolution_note) THEN
                        RAISE EXCEPTION 'a resolved payment anomaly is final' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_payment_anomalies_guard_update BEFORE UPDATE OR DELETE ON payments.payment_anomalies
                    FOR EACH ROW EXECUTE FUNCTION payments.guard_payment_anomaly_update();

                CREATE FUNCTION payments.guard_payment_anomaly_insert() RETURNS trigger AS $$
                BEGIN
                    IF NEW.state <> 'Open' THEN
                        RAISE EXCEPTION 'a payment anomaly starts Open' USING ERRCODE = '23000';
                    END IF;
                    -- A correlated anomaly must really be about that attempt of that order and provider: no false association.
                    IF NEW.attempt_id IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM payments.payment_attempts a
                        WHERE a.id = NEW.attempt_id AND a.order_id = NEW.order_id AND a.provider = NEW.provider) THEN
                        RAISE EXCEPTION 'a payment anomaly may only be associated with an attempt of its own order and provider' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_payment_anomalies_insert_guard BEFORE INSERT ON payments.payment_anomalies
                    FOR EACH ROW EXECUTE FUNCTION payments.guard_payment_anomaly_insert();

                -- Every anomaly, and its resolution, also lands in the financial transition history.
                CREATE TRIGGER trg_payment_anomalies_record_insert AFTER INSERT ON payments.payment_anomalies
                    FOR EACH ROW EXECUTE FUNCTION payments.record_financial_transition();
                CREATE TRIGGER trg_payment_anomalies_record_transition AFTER UPDATE OF state ON payments.payment_anomalies
                    FOR EACH ROW WHEN (OLD.state IS DISTINCT FROM NEW.state) EXECUTE FUNCTION payments.record_financial_transition();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // An anomaly is the only record of a verified-signature payment event that this system did not apply, and of what an
            // administrator concluded. The rollback REFUSES to destroy it: it fails while any row exists. If it is really intended,
            // export the rows, then drop the table by hand (row deletes are blocked by a trigger on purpose).
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM payments.payment_anomalies) THEN
                        RAISE EXCEPTION 'Refusing to roll back: payments.payment_anomalies holds recorded payment anomalies. Export and reconcile them first.' USING ERRCODE = '23000';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "payment_anomalies",
                schema: "payments");

            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS payments.guard_payment_anomaly_update();
                DROP FUNCTION IF EXISTS payments.guard_payment_anomaly_insert();
                """);
        }
    }
}

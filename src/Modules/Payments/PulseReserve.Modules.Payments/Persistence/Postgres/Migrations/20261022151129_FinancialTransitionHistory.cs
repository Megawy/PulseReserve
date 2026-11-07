using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Payments.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class FinancialTransitionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_transitions",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    event_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    fact_basis = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    provider_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_transitions", x => x.id);
                    table.CheckConstraint("chk_financial_transitions_kind", "kind IN ('Transition', 'Rejected')");
                });

            migrationBuilder.CreateIndex(
                name: "idx_financial_transitions_entity",
                schema: "payments",
                table: "financial_transitions",
                columns: new[] { "entity_type", "entity_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "idx_financial_transitions_order",
                schema: "payments",
                table: "financial_transitions",
                columns: new[] { "order_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "uq_financial_transitions_rejected_event",
                schema: "payments",
                table: "financial_transitions",
                columns: new[] { "entity_type", "entity_id", "event_code", "provider_event_id" },
                unique: true,
                filter: "kind = 'Rejected' AND provider_event_id IS NOT NULL");

            migrationBuilder.Sql(
                """
                -- One generic recorder for every financial state machine. It runs AFTER the change, in the SAME transaction, so a
                -- rolled-back change leaves no history and a committed change always has its row; if the insert itself ever
                -- failed, the financial change would fail with it (and a webhook would be redelivered) instead of being lost
                -- silently. It reads only identifiers and codes that the application put into transaction-local settings.
                CREATE FUNCTION payments.record_financial_transition() RETURNS trigger AS $$
                DECLARE
                    _new jsonb := to_jsonb(NEW);
                    _entity text;
                    _from text;
                    _to text;
                    _order uuid;
                    _provider text;
                BEGIN
                    IF TG_TABLE_NAME = 'orders' THEN
                        _entity := 'Order'; _to := _new->>'status'; _order := (_new->>'id')::uuid;
                        IF TG_OP = 'UPDATE' THEN _from := to_jsonb(OLD)->>'status'; END IF;
                    ELSIF TG_TABLE_NAME = 'payment_attempts' THEN
                        _entity := 'PaymentAttempt'; _to := _new->>'status'; _order := (_new->>'order_id')::uuid; _provider := _new->>'provider';
                        IF TG_OP = 'UPDATE' THEN _from := to_jsonb(OLD)->>'status'; END IF;
                    ELSIF TG_TABLE_NAME = 'refunds' THEN
                        _entity := 'Refund'; _to := _new->>'state'; _order := (_new->>'order_id')::uuid; _provider := _new->>'provider';
                        IF TG_OP = 'UPDATE' THEN _from := to_jsonb(OLD)->>'state'; END IF;
                    ELSIF TG_TABLE_NAME = 'payment_exceptions' THEN
                        _entity := 'PaymentException'; _to := _new->>'state'; _order := (_new->>'order_id')::uuid; _provider := _new->>'provider';
                        IF TG_OP = 'UPDATE' THEN _from := to_jsonb(OLD)->>'state'; END IF;
                    ELSIF TG_TABLE_NAME = 'payment_anomalies' THEN
                        _entity := 'PaymentAnomaly'; _to := _new->>'state'; _order := (_new->>'order_id')::uuid; _provider := _new->>'provider';
                        IF TG_OP = 'UPDATE' THEN _from := to_jsonb(OLD)->>'state'; END IF;
                    ELSE
                        RETURN NEW;
                    END IF;

                    INSERT INTO payments.financial_transitions
                        (id, occurred_at_utc, entity_type, entity_id, order_id, from_state, to_state, event_code, kind, source, fact_basis,
                         provider, provider_event_id, actor_user_id, correlation_id, reason)
                    VALUES
                        (gen_random_uuid(), now(), _entity, (_new->>'id')::uuid, _order, _from, _to,
                         lower(_entity) || '.' || COALESCE(lower(_from), 'new') || '_to_' || lower(_to), 'Transition',
                         COALESCE(NULLIF(current_setting('pulsereserve.source', true), ''), 'Unspecified'),
                         COALESCE(NULLIF(current_setting('pulsereserve.fact_basis', true), ''), 'Unspecified'),
                         _provider,
                         NULLIF(current_setting('pulsereserve.provider_event_id', true), ''),
                         NULLIF(current_setting('pulsereserve.actor_user_id', true), '')::uuid,
                         NULLIF(current_setting('pulsereserve.correlation_id', true), ''),
                         NULLIF(current_setting('pulsereserve.reason', true), ''));
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_orders_record_insert AFTER INSERT ON payments.orders
                    FOR EACH ROW EXECUTE FUNCTION payments.record_financial_transition();
                CREATE TRIGGER trg_orders_record_transition AFTER UPDATE OF status ON payments.orders
                    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status) EXECUTE FUNCTION payments.record_financial_transition();

                CREATE TRIGGER trg_payment_attempts_record_insert AFTER INSERT ON payments.payment_attempts
                    FOR EACH ROW EXECUTE FUNCTION payments.record_financial_transition();
                CREATE TRIGGER trg_payment_attempts_record_transition AFTER UPDATE OF status ON payments.payment_attempts
                    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status) EXECUTE FUNCTION payments.record_financial_transition();

                CREATE TRIGGER trg_refunds_record_insert AFTER INSERT ON payments.refunds
                    FOR EACH ROW EXECUTE FUNCTION payments.record_financial_transition();
                CREATE TRIGGER trg_refunds_record_transition AFTER UPDATE OF state ON payments.refunds
                    FOR EACH ROW WHEN (OLD.state IS DISTINCT FROM NEW.state) EXECUTE FUNCTION payments.record_financial_transition();

                CREATE TRIGGER trg_payment_exceptions_record_insert AFTER INSERT ON payments.payment_exceptions
                    FOR EACH ROW EXECUTE FUNCTION payments.record_financial_transition();
                CREATE TRIGGER trg_payment_exceptions_record_transition AFTER UPDATE OF state ON payments.payment_exceptions
                    FOR EACH ROW WHEN (OLD.state IS DISTINCT FROM NEW.state) EXECUTE FUNCTION payments.record_financial_transition();

                -- The history is append-only: no update, no delete (the same guard the audit log and tickets use).
                CREATE TRIGGER trg_financial_transitions_append_only BEFORE UPDATE OR DELETE ON payments.financial_transitions
                    FOR EACH ROW EXECUTE FUNCTION payments.forbid_row_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The history is the only record of how the financial state moved. Like the other financial rollbacks this one REFUSES to
            // destroy it: it fails while any row exists. If the rollback is really intended, export the rows, then drop the table by
            // hand (deletes are blocked by a trigger on purpose) and run the rollback again.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM payments.financial_transitions) THEN
                        RAISE EXCEPTION 'Refusing to roll back: payments.financial_transitions holds financial history. Export it first.' USING ERRCODE = '23000';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_orders_record_insert ON payments.orders;
                DROP TRIGGER IF EXISTS trg_orders_record_transition ON payments.orders;
                DROP TRIGGER IF EXISTS trg_payment_attempts_record_insert ON payments.payment_attempts;
                DROP TRIGGER IF EXISTS trg_payment_attempts_record_transition ON payments.payment_attempts;
                DROP TRIGGER IF EXISTS trg_refunds_record_insert ON payments.refunds;
                DROP TRIGGER IF EXISTS trg_refunds_record_transition ON payments.refunds;
                DROP TRIGGER IF EXISTS trg_payment_exceptions_record_insert ON payments.payment_exceptions;
                DROP TRIGGER IF EXISTS trg_payment_exceptions_record_transition ON payments.payment_exceptions;
                DROP FUNCTION IF EXISTS payments.record_financial_transition();
                """);

            migrationBuilder.DropTable(
                name: "financial_transitions",
                schema: "payments");
        }
    }
}

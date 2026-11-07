using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Payments.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class LiveChangeNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Real-time updates are announced by the DATABASE (see the Reservation migration of the same name): pg_notify is delivered only
            // when the transaction COMMITS and dropped on rollback, and a trigger on the append-only transition history covers EVERY
            // financial transition from every code path (webhooks, workers, administrators, customers) in one place.
            // Payloads carry ids and a coarse kind only; the listener reads current state itself. A failure to notify must never fail
            // the financial write that caused it, hence the nested exception block.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION payments.notify_live_change() RETURNS trigger AS $$
                BEGIN
                    BEGIN
                        IF TG_TABLE_NAME = 'financial_transitions' THEN
                            IF NEW.kind = 'Transition' THEN
                                PERFORM pg_notify('pulsereserve_live',
                                    json_build_object('k', 'fin', 't', NEW.entity_type, 'o', NEW.order_id)::text);
                            END IF;
                        ELSIF TG_TABLE_NAME = 'tickets' THEN
                            PERFORM pg_notify('pulsereserve_live', json_build_object('k', 'tkt', 'o', NEW.order_id)::text);
                        ELSE
                            PERFORM pg_notify('pulsereserve_live', json_build_object('k', 'wrk', 't', TG_TABLE_NAME)::text);
                        END IF;
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_financial_transitions_notify AFTER INSERT ON payments.financial_transitions
                    FOR EACH ROW EXECUTE FUNCTION payments.notify_live_change();
                CREATE TRIGGER trg_tickets_notify AFTER INSERT ON payments.tickets
                    FOR EACH ROW EXECUTE FUNCTION payments.notify_live_change();

                -- Work queues are announced only when an item needs a person (enters or leaves NeedsReview), not for every claim and retry.
                CREATE TRIGGER trg_reconciliation_tasks_notify_insert AFTER INSERT ON payments.reconciliation_tasks
                    FOR EACH ROW WHEN (NEW.state = 'NeedsReview') EXECUTE FUNCTION payments.notify_live_change();
                CREATE TRIGGER trg_reconciliation_tasks_notify_state AFTER UPDATE OF state ON payments.reconciliation_tasks
                    FOR EACH ROW WHEN (OLD.state IS DISTINCT FROM NEW.state AND (OLD.state = 'NeedsReview' OR NEW.state = 'NeedsReview'))
                    EXECUTE FUNCTION payments.notify_live_change();
                CREATE TRIGGER trg_outbox_events_notify_insert AFTER INSERT ON payments.outbox_events
                    FOR EACH ROW WHEN (NEW.state = 'NeedsReview') EXECUTE FUNCTION payments.notify_live_change();
                CREATE TRIGGER trg_outbox_events_notify_state AFTER UPDATE OF state ON payments.outbox_events
                    FOR EACH ROW WHEN (OLD.state IS DISTINCT FROM NEW.state AND (OLD.state = 'NeedsReview' OR NEW.state = 'NeedsReview'))
                    EXECUTE FUNCTION payments.notify_live_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removes only the announcement triggers; no financial record is touched, so no rollback guard is needed.
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_outbox_events_notify_state ON payments.outbox_events;
                DROP TRIGGER IF EXISTS trg_outbox_events_notify_insert ON payments.outbox_events;
                DROP TRIGGER IF EXISTS trg_reconciliation_tasks_notify_state ON payments.reconciliation_tasks;
                DROP TRIGGER IF EXISTS trg_reconciliation_tasks_notify_insert ON payments.reconciliation_tasks;
                DROP TRIGGER IF EXISTS trg_tickets_notify ON payments.tickets;
                DROP TRIGGER IF EXISTS trg_financial_transitions_notify ON payments.financial_transitions;
                DROP FUNCTION IF EXISTS payments.notify_live_change();
                """);
        }
    }
}

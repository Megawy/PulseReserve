using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Payments.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class LiveQueueStateNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Found by looking at the real administrator overview in a browser: it shows a count for EVERY state of the work queues
            // (queued, processing, completed, needs review), but the first version announced only items entering or leaving NeedsReview,
            // so the counts went stale as the workers moved items Pending -> Processing -> Done. Every insert and every state change of a
            // reconciliation task or outbox event is announced now. The payload is the same coarse hint (no ids), identical hints within
            // one transaction are merged by PostgreSQL itself, and the dispatcher coalesces bursts, so a busy worker costs an administrator's
            // page one quiet re-read per moment, not one per item. The trigger function is unchanged (and still cannot fail a write).
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_outbox_events_notify_state ON payments.outbox_events;
                DROP TRIGGER IF EXISTS trg_outbox_events_notify_insert ON payments.outbox_events;
                DROP TRIGGER IF EXISTS trg_reconciliation_tasks_notify_state ON payments.reconciliation_tasks;
                DROP TRIGGER IF EXISTS trg_reconciliation_tasks_notify_insert ON payments.reconciliation_tasks;

                CREATE TRIGGER trg_reconciliation_tasks_notify_insert AFTER INSERT ON payments.reconciliation_tasks
                    FOR EACH ROW EXECUTE FUNCTION payments.notify_live_change();
                CREATE TRIGGER trg_reconciliation_tasks_notify_state AFTER UPDATE OF state ON payments.reconciliation_tasks
                    FOR EACH ROW WHEN (OLD.state IS DISTINCT FROM NEW.state) EXECUTE FUNCTION payments.notify_live_change();
                CREATE TRIGGER trg_outbox_events_notify_insert AFTER INSERT ON payments.outbox_events
                    FOR EACH ROW EXECUTE FUNCTION payments.notify_live_change();
                CREATE TRIGGER trg_outbox_events_notify_state AFTER UPDATE OF state ON payments.outbox_events
                    FOR EACH ROW WHEN (OLD.state IS DISTINCT FROM NEW.state) EXECUTE FUNCTION payments.notify_live_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to the narrower announcements of LiveChangeNotifications. No record is touched, so no rollback guard is needed.
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_outbox_events_notify_state ON payments.outbox_events;
                DROP TRIGGER IF EXISTS trg_outbox_events_notify_insert ON payments.outbox_events;
                DROP TRIGGER IF EXISTS trg_reconciliation_tasks_notify_state ON payments.reconciliation_tasks;
                DROP TRIGGER IF EXISTS trg_reconciliation_tasks_notify_insert ON payments.reconciliation_tasks;

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
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Reservation.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class LiveChangeNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Real-time updates are announced by the DATABASE, so that they follow commits and cover every code path (the booking API,
            // the proactive hold expiry, the payment workflows that release or confirm a reservation) without scattering publish calls.
            // pg_notify is transactional: PostgreSQL delivers the notification only when the surrounding transaction COMMITS, and drops
            // it on rollback, so a failed hold or a rolled-back confirmation can never be announced.
            // The payload carries the reservation id only. The listener reads the current state itself (routing and content), so a
            // late, duplicated or reordered notification can at worst cause an extra read.
            // A failure to notify must never fail the booking that caused it, hence the nested exception block.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION reservation.notify_reservation_change() RETURNS trigger AS $$
                BEGIN
                    BEGIN
                        PERFORM pg_notify('pulsereserve_live', json_build_object('k', 'res', 'id', NEW.id)::text);
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_reservations_notify_insert AFTER INSERT ON reservation.reservations
                    FOR EACH ROW EXECUTE FUNCTION reservation.notify_reservation_change();
                CREATE TRIGGER trg_reservations_notify_status AFTER UPDATE OF status ON reservation.reservations
                    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status) EXECUTE FUNCTION reservation.notify_reservation_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removes only the announcement triggers; no booking data is touched.
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_reservations_notify_status ON reservation.reservations;
                DROP TRIGGER IF EXISTS trg_reservations_notify_insert ON reservation.reservations;
                DROP FUNCTION IF EXISTS reservation.notify_reservation_change();
                """);
        }
    }
}

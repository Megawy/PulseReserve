using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Reservation.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CheckoutStatesAndActiveHoldIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_reservations_active_hold_per_customer_event",
                schema: "reservation",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "chk_reservations_status",
                schema: "reservation",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "chk_event_seats_status",
                schema: "reservation",
                table: "event_seats");

            migrationBuilder.CreateIndex(
                name: "uq_reservations_active_hold_per_customer_event",
                schema: "reservation",
                table: "reservations",
                columns: new[] { "event_id", "customer_id" },
                unique: true,
                filter: "status IN ('Held', 'Checkout')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_reservations_status",
                schema: "reservation",
                table: "reservations",
                sql: "status IN ('Held', 'Released', 'Expired', 'Checkout', 'Confirmed')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_event_seats_status",
                schema: "reservation",
                table: "event_seats",
                sql: "status IN ('Available', 'Held', 'Sold')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_reservations_active_hold_per_customer_event",
                schema: "reservation",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "chk_reservations_status",
                schema: "reservation",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "chk_event_seats_status",
                schema: "reservation",
                table: "event_seats");

            migrationBuilder.CreateIndex(
                name: "uq_reservations_active_hold_per_customer_event",
                schema: "reservation",
                table: "reservations",
                columns: new[] { "event_id", "customer_id" },
                unique: true,
                filter: "status = 'Held'");

            migrationBuilder.AddCheckConstraint(
                name: "chk_reservations_status",
                schema: "reservation",
                table: "reservations",
                sql: "status IN ('Held', 'Released', 'Expired')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_event_seats_status",
                schema: "reservation",
                table: "event_seats",
                sql: "status IN ('Available', 'Held')");
        }
    }
}

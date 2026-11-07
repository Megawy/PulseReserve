using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Reservation.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialReservationCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reservation");

            migrationBuilder.CreateTable(
                name: "event_inventories",
                schema: "reservation",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_count = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_inventories", x => x.event_id);
                    table.CheckConstraint("chk_event_inventories_seat_count", "seat_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "reservation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservations", x => x.id);
                    table.CheckConstraint("chk_reservations_expiry_after_creation", "expires_at_utc > created_at_utc");
                    table.CheckConstraint("chk_reservations_status", "status IN ('Held', 'Released', 'Expired')");
                    table.CheckConstraint("chk_reservations_total_non_negative", "total_amount >= 0");
                    table.ForeignKey(
                        name: "fk_reservations_event_inventories",
                        column: x => x.event_id,
                        principalSchema: "reservation",
                        principalTable: "event_inventories",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "event_seats",
                schema: "reservation",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hold_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    price_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    price_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_seats", x => new { x.event_id, x.seat_id });
                    table.CheckConstraint("chk_event_seats_hold_expiry", "status <> 'Held' OR hold_expires_at_utc IS NOT NULL");
                    table.CheckConstraint("chk_event_seats_owner", "(status = 'Available') = (reservation_id IS NULL)");
                    table.CheckConstraint("chk_event_seats_price_non_negative", "price_amount >= 0");
                    table.CheckConstraint("chk_event_seats_status", "status IN ('Available', 'Held')");
                    table.ForeignKey(
                        name: "fk_event_seats_event_inventories",
                        column: x => x.event_id,
                        principalSchema: "reservation",
                        principalTable: "event_inventories",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_event_seats_reservations",
                        column: x => x.reservation_id,
                        principalSchema: "reservation",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reservation_items",
                schema: "reservation",
                columns: table => new
                {
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    price_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservation_items", x => new { x.reservation_id, x.seat_id });
                    table.CheckConstraint("chk_reservation_items_price_non_negative", "price_amount >= 0");
                    table.ForeignKey(
                        name: "fk_reservation_items_event_seats",
                        columns: x => new { x.event_id, x.seat_id },
                        principalSchema: "reservation",
                        principalTable: "event_seats",
                        principalColumns: new[] { "event_id", "seat_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservation_items_reservations",
                        column: x => x.reservation_id,
                        principalSchema: "reservation",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_event_seats_reservation_id",
                schema: "reservation",
                table: "event_seats",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_items_event_id_seat_id",
                schema: "reservation",
                table: "reservation_items",
                columns: new[] { "event_id", "seat_id" });

            migrationBuilder.CreateIndex(
                name: "idx_reservations_status_expires",
                schema: "reservation",
                table: "reservations",
                columns: new[] { "status", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "uq_reservations_active_hold_per_customer_event",
                schema: "reservation",
                table: "reservations",
                columns: new[] { "event_id", "customer_id" },
                unique: true,
                filter: "status = 'Held'");

            migrationBuilder.CreateIndex(
                name: "uq_reservations_customer_idempotency_key",
                schema: "reservation",
                table: "reservations",
                columns: new[] { "customer_id", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reservation_items",
                schema: "reservation");

            migrationBuilder.DropTable(
                name: "event_seats",
                schema: "reservation");

            migrationBuilder.DropTable(
                name: "reservations",
                schema: "reservation");

            migrationBuilder.DropTable(
                name: "event_inventories",
                schema: "reservation");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Infrastructure.Persistence.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalogCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "venues",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CLOCK_TIMESTAMP()"),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CLOCK_TIMESTAMP()"),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    address_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address_country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address_postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: ""),
                    address_street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_venues", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seating_layouts",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CLOCK_TIMESTAMP()"),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CLOCK_TIMESTAMP()"),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seating_layouts", x => x.id);
                    table.ForeignKey(
                        name: "fk_seating_layouts_venues",
                        column: x => x.venue_id,
                        principalSchema: "catalog",
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seating_layout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheduled_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    scheduled_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Draft"),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    on_sale_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CLOCK_TIMESTAMP()"),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CLOCK_TIMESTAMP()"),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                    table.UniqueConstraint("uq_events_id_layout", x => new { x.id, x.seating_layout_id });
                    table.CheckConstraint("chk_events_chronological", "scheduled_start_utc < scheduled_end_utc");
                    table.CheckConstraint("chk_events_published_layout", "status = 'Draft' OR seating_layout_id IS NOT NULL");
                    table.CheckConstraint("chk_events_status_valid", "status IN ('Draft', 'Published', 'OnSale', 'Closed', 'Cancelled')");
                    table.ForeignKey(
                        name: "fk_events_seating_layouts",
                        column: x => x.seating_layout_id,
                        principalSchema: "catalog",
                        principalTable: "seating_layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_events_venues",
                        column: x => x.venue_id,
                        principalSchema: "catalog",
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_tiers",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    seating_layout_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_tiers", x => x.id);
                    table.UniqueConstraint("uq_price_tiers_layout_id", x => new { x.seating_layout_id, x.id });
                    table.CheckConstraint("chk_price_tiers_name_non_empty", "length(trim(name)) > 0");
                    table.ForeignKey(
                        name: "fk_price_tiers_seating_layouts",
                        column: x => x.seating_layout_id,
                        principalSchema: "catalog",
                        principalTable: "seating_layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sections",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    seating_layout_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sections", x => x.id);
                    table.UniqueConstraint("uq_sections_layout_id", x => new { x.seating_layout_id, x.id });
                    table.ForeignKey(
                        name: "fk_sections_seating_layouts",
                        column: x => x.seating_layout_id,
                        principalSchema: "catalog",
                        principalTable: "seating_layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_prices",
                schema: "catalog",
                columns: table => new
                {
                    price_tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seating_layout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    price_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_prices", x => new { x.event_id, x.price_tier_id });
                    table.CheckConstraint("chk_event_prices_amount_non_negative", "price_amount >= 0");
                    table.CheckConstraint("chk_event_prices_currency_iso", "length(price_currency) = 3 AND price_currency = upper(price_currency)");
                    table.ForeignKey(
                        name: "fk_event_prices_events_composite",
                        columns: x => new { x.event_id, x.seating_layout_id },
                        principalSchema: "catalog",
                        principalTable: "events",
                        principalColumns: new[] { "id", "seating_layout_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_event_prices_price_tiers_composite",
                        columns: x => new { x.seating_layout_id, x.price_tier_id },
                        principalSchema: "catalog",
                        principalTable: "price_tiers",
                        principalColumns: new[] { "seating_layout_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rows",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    seating_layout_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rows", x => x.id);
                    table.UniqueConstraint("uq_rows_layout_section_id", x => new { x.seating_layout_id, x.section_id, x.id });
                    table.ForeignKey(
                        name: "FK_rows_seating_layouts_seating_layout_id",
                        column: x => x.seating_layout_id,
                        principalSchema: "catalog",
                        principalTable: "seating_layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_rows_sections_composite",
                        columns: x => new { x.seating_layout_id, x.section_id },
                        principalSchema: "catalog",
                        principalTable: "sections",
                        principalColumns: new[] { "seating_layout_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seats",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    price_tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seating_layout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coordinate_x = table.Column<int>(type: "integer", nullable: false),
                    coordinate_y = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seats", x => x.id);
                    table.CheckConstraint("chk_seats_coordinates", "coordinate_x >= 0 AND coordinate_y >= 0");
                    table.ForeignKey(
                        name: "FK_seats_seating_layouts_seating_layout_id",
                        column: x => x.seating_layout_id,
                        principalSchema: "catalog",
                        principalTable: "seating_layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_seats_price_tiers_composite",
                        columns: x => new { x.seating_layout_id, x.price_tier_id },
                        principalSchema: "catalog",
                        principalTable: "price_tiers",
                        principalColumns: new[] { "seating_layout_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_seats_rows_composite",
                        columns: x => new { x.seating_layout_id, x.section_id, x.row_id },
                        principalSchema: "catalog",
                        principalTable: "rows",
                        principalColumns: new[] { "seating_layout_id", "section_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_prices_event_id_seating_layout_id",
                schema: "catalog",
                table: "event_prices",
                columns: new[] { "event_id", "seating_layout_id" });

            migrationBuilder.CreateIndex(
                name: "IX_event_prices_seating_layout_id_price_tier_id",
                schema: "catalog",
                table: "event_prices",
                columns: new[] { "seating_layout_id", "price_tier_id" });

            migrationBuilder.CreateIndex(
                name: "idx_events_seating_layout_id",
                schema: "catalog",
                table: "events",
                column: "seating_layout_id");

            migrationBuilder.CreateIndex(
                name: "idx_events_status_start",
                schema: "catalog",
                table: "events",
                columns: new[] { "status", "scheduled_start_utc" });

            migrationBuilder.CreateIndex(
                name: "idx_events_venue_id",
                schema: "catalog",
                table: "events",
                column: "venue_id");

            migrationBuilder.CreateIndex(
                name: "idx_price_tiers_layout_id",
                schema: "catalog",
                table: "price_tiers",
                column: "seating_layout_id");

            migrationBuilder.CreateIndex(
                name: "idx_rows_layout_id",
                schema: "catalog",
                table: "rows",
                column: "seating_layout_id");

            migrationBuilder.CreateIndex(
                name: "uq_rows_section_label_lower",
                schema: "catalog",
                table: "rows",
                columns: new[] { "section_id", "label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_seating_layouts_venue_id",
                schema: "catalog",
                table: "seating_layouts",
                column: "venue_id");

            migrationBuilder.CreateIndex(
                name: "idx_seats_layout_id",
                schema: "catalog",
                table: "seats",
                column: "seating_layout_id");

            migrationBuilder.CreateIndex(
                name: "idx_seats_price_tier_id",
                schema: "catalog",
                table: "seats",
                column: "price_tier_id");

            migrationBuilder.CreateIndex(
                name: "IX_seats_seating_layout_id_price_tier_id",
                schema: "catalog",
                table: "seats",
                columns: new[] { "seating_layout_id", "price_tier_id" });

            migrationBuilder.CreateIndex(
                name: "IX_seats_seating_layout_id_section_id_row_id",
                schema: "catalog",
                table: "seats",
                columns: new[] { "seating_layout_id", "section_id", "row_id" });

            migrationBuilder.CreateIndex(
                name: "uq_seats_row_label_lower",
                schema: "catalog",
                table: "seats",
                columns: new[] { "row_id", "label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_sections_layout_name_lower",
                schema: "catalog",
                table: "sections",
                columns: new[] { "seating_layout_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_venues_name",
                schema: "catalog",
                table: "venues",
                column: "name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_prices",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "seats",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "events",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "price_tiers",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "rows",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "sections",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "seating_layouts",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "venues",
                schema: "catalog");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Payments.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    payment_deadline_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("chk_orders_currency_format", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("chk_orders_status", "status IN ('PendingPayment', 'Paid', 'Cancelled', 'Expired', 'RefundRequired')");
                    table.CheckConstraint("chk_orders_total_non_negative", "total_amount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                schema: "payments",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    line_total_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_items", x => new { x.order_id, x.seat_id });
                    table.CheckConstraint("chk_order_items_line_total", "line_total_amount = unit_price_amount * quantity");
                    table.CheckConstraint("chk_order_items_price_non_negative", "unit_price_amount >= 0");
                    table.CheckConstraint("chk_order_items_quantity", "quantity = 1");
                    table.ForeignKey(
                        name: "fk_order_items_orders",
                        column: x => x.order_id,
                        principalSchema: "payments",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_attempts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_simulated = table.Column<bool>(type: "boolean", nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_attempts", x => x.id);
                    table.CheckConstraint("chk_payment_attempts_status", "status IN ('Initiated', 'Succeeded', 'Failed', 'Cancelled')");
                    table.ForeignKey(
                        name: "fk_payment_attempts_orders",
                        column: x => x.order_id,
                        principalSchema: "payments",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_orders_customer_created",
                schema: "payments",
                table: "orders",
                columns: new[] { "customer_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "uq_orders_customer_idempotency_key",
                schema: "payments",
                table: "orders",
                columns: new[] { "customer_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_orders_reservation",
                schema: "payments",
                table: "orders",
                column: "reservation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_payment_attempts_one_initiated_per_order",
                schema: "payments",
                table: "payment_attempts",
                column: "order_id",
                unique: true,
                filter: "status = 'Initiated'");

            migrationBuilder.CreateIndex(
                name: "uq_payment_attempts_order_idempotency_key",
                schema: "payments",
                table: "payment_attempts",
                columns: new[] { "order_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_payment_attempts_order_number",
                schema: "payments",
                table: "payment_attempts",
                columns: new[] { "order_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_payment_attempts_provider_reference",
                schema: "payments",
                table: "payment_attempts",
                columns: new[] { "provider", "provider_reference" },
                unique: true,
                filter: "provider_reference IS NOT NULL");

            // The order snapshot is immutable: lines can never change or disappear, and an order may only change
            // status/updated_at. Later catalog or reservation changes therefore cannot rewrite historical amounts.
            migrationBuilder.Sql("""
                CREATE FUNCTION payments.forbid_order_item_change() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'payments.order_items rows are an immutable price snapshot' USING ERRCODE = '23000';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_order_items_immutable
                    BEFORE UPDATE OR DELETE ON payments.order_items
                    FOR EACH ROW EXECUTE FUNCTION payments.forbid_order_item_change();

                CREATE FUNCTION payments.forbid_order_snapshot_change() RETURNS trigger AS $$
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
                    -- Final states are never left (a paid order stays paid; refund handling will add its own transition).
                    IF OLD.status IN ('Paid', 'RefundRequired') AND NEW.status IS DISTINCT FROM OLD.status THEN
                        RAISE EXCEPTION 'order status % is final', OLD.status USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_orders_immutable
                    BEFORE UPDATE OR DELETE ON payments.orders
                    FOR EACH ROW EXECUTE FUNCTION payments.forbid_order_snapshot_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping these tables destroys every order, its items and its payment attempts. Like every other financial rollback this one
            // REFUSES while any order exists (the surrounding transaction changes nothing). To really remove the schema: export the
            // rows, delete them by hand, and run the rollback again.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM payments.orders) THEN
                        RAISE EXCEPTION 'Refusing to roll back: payments.orders holds orders with their items and payment attempts. Export them first.' USING ERRCODE = '23000';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_orders_immutable ON payments.orders; DROP TRIGGER IF EXISTS trg_order_items_immutable ON payments.order_items; DROP FUNCTION IF EXISTS payments.forbid_order_snapshot_change(); DROP FUNCTION IF EXISTS payments.forbid_order_item_change();");

            migrationBuilder.DropTable(
                name: "order_items",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "payment_attempts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "payments");
        }
    }
}

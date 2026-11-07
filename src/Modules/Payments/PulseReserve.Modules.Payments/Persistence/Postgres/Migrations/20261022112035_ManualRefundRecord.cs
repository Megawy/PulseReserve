using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PulseReserve.Modules.Payments.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ManualRefundRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_refunds_state",
                schema: "payments",
                table: "refunds");

            migrationBuilder.AddColumn<string>(
                name: "manual_note",
                schema: "payments",
                table: "refunds",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "manual_recorded_at_utc",
                schema: "payments",
                table: "refunds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "manual_recorded_by",
                schema: "payments",
                table: "refunds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manual_reference",
                schema: "payments",
                table: "refunds",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "chk_refunds_manual_fields",
                schema: "payments",
                table: "refunds",
                sql: "(state = 'ManuallyRecorded') = (manual_recorded_by IS NOT NULL AND manual_recorded_at_utc IS NOT NULL) AND (state = 'ManuallyRecorded' OR (manual_reference IS NULL AND manual_note IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "chk_refunds_state",
                schema: "payments",
                table: "refunds",
                sql: "state IN ('Requested', 'Unknown', 'Pending', 'Succeeded', 'Failed', 'NeedsReview', 'ManuallyRecorded')");

            // A manual record is final like a provider-confirmed one, can only be made from NeedsReview, and its who/when/
            // reference/note can never change afterwards. (Identity and amount were already immutable.)
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION payments.guard_refund_update() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'refunds cannot be deleted' USING ERRCODE = '23000';
                    END IF;
                    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.order_id IS DISTINCT FROM OLD.order_id
                       OR NEW.payment_attempt_id IS DISTINCT FROM OLD.payment_attempt_id
                       OR NEW.amount IS DISTINCT FROM OLD.amount OR NEW.currency IS DISTINCT FROM OLD.currency
                       OR NEW.reason IS DISTINCT FROM OLD.reason OR NEW.created_at_utc IS DISTINCT FROM OLD.created_at_utc THEN
                        RAISE EXCEPTION 'refund identity and amount are immutable' USING ERRCODE = '23000';
                    END IF;
                    IF OLD.state = 'Succeeded' AND (NEW.state IS DISTINCT FROM OLD.state OR NEW.provider_refund_id IS DISTINCT FROM OLD.provider_refund_id) THEN
                        RAISE EXCEPTION 'a succeeded refund is final' USING ERRCODE = '23000';
                    END IF;
                    IF OLD.state = 'ManuallyRecorded' AND (
                           NEW.state IS DISTINCT FROM OLD.state OR NEW.provider_refund_id IS DISTINCT FROM OLD.provider_refund_id
                           OR NEW.manual_reference IS DISTINCT FROM OLD.manual_reference OR NEW.manual_note IS DISTINCT FROM OLD.manual_note
                           OR NEW.manual_recorded_by IS DISTINCT FROM OLD.manual_recorded_by
                           OR NEW.manual_recorded_at_utc IS DISTINCT FROM OLD.manual_recorded_at_utc) THEN
                        RAISE EXCEPTION 'a manually recorded refund is final' USING ERRCODE = '23000';
                    END IF;
                    IF NEW.state = 'ManuallyRecorded' AND OLD.state IS DISTINCT FROM 'ManuallyRecorded' AND OLD.state <> 'NeedsReview' THEN
                        RAISE EXCEPTION 'only a refund waiting for review can be recorded as manually completed' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A manually recorded refund is the only record that money was returned outside this system. Rolling back would drop its
            // columns, so the rollback REFUSES while any exists (and the surrounding transaction then changes nothing). If it is
            // really intended, export and reconcile those rows first and remove them by hand.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM payments.refunds WHERE state = 'ManuallyRecorded') THEN
                        RAISE EXCEPTION 'Refusing to roll back: payments.refunds holds manually recorded refunds. Export and reconcile them first.' USING ERRCODE = '23000';
                    END IF;
                END $$;
                """);

            // Rolling back is refused while any manual record exists (the restored state constraint cannot hold it): the audit
            // trail of a refund performed outside the system is never silently discarded.
            migrationBuilder.DropCheckConstraint(
                name: "chk_refunds_manual_fields",
                schema: "payments",
                table: "refunds");

            migrationBuilder.DropCheckConstraint(
                name: "chk_refunds_state",
                schema: "payments",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "manual_note",
                schema: "payments",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "manual_recorded_at_utc",
                schema: "payments",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "manual_recorded_by",
                schema: "payments",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "manual_reference",
                schema: "payments",
                table: "refunds");

            migrationBuilder.AddCheckConstraint(
                name: "chk_refunds_state",
                schema: "payments",
                table: "refunds",
                sql: "state IN ('Requested', 'Unknown', 'Pending', 'Succeeded', 'Failed', 'NeedsReview')");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION payments.guard_refund_update() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'refunds cannot be deleted' USING ERRCODE = '23000';
                    END IF;
                    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.order_id IS DISTINCT FROM OLD.order_id
                       OR NEW.payment_attempt_id IS DISTINCT FROM OLD.payment_attempt_id
                       OR NEW.amount IS DISTINCT FROM OLD.amount OR NEW.currency IS DISTINCT FROM OLD.currency
                       OR NEW.reason IS DISTINCT FROM OLD.reason OR NEW.created_at_utc IS DISTINCT FROM OLD.created_at_utc THEN
                        RAISE EXCEPTION 'refund identity and amount are immutable' USING ERRCODE = '23000';
                    END IF;
                    IF OLD.state = 'Succeeded' AND (NEW.state IS DISTINCT FROM OLD.state OR NEW.provider_refund_id IS DISTINCT FROM OLD.provider_refund_id) THEN
                        RAISE EXCEPTION 'a succeeded refund is final' USING ERRCODE = '23000';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }
    }
}

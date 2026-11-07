using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PulseReserve.IntegrationTests.Catalog;
using PulseReserve.Modules.Payments.Persistence.Postgres;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

/// <summary>
/// The four newest payment migrations against REAL PostgreSQL: existing rows stay valid when they are applied, and each rollback
/// guard refuses to destroy protected financial records yet allows a rollback when there are none. Every test creates its own
/// throwaway database inside the disposable container the fixture starts (never the fixture's main database, never a native
/// server), so migrating down and up is safe. COMPILED, NOT EXECUTED in this working tree (it needs Docker).
/// </summary>
public sealed class PaymentsMigrationPostgreSqlTests : IClassFixture<PostgreSqlDatabaseFixture>, IAsyncLifetime
{
    private const string InitialPayments = "20261021125806_InitialPayments";
    private const string ReconciliationRefundsOutboxTickets = "20261021132335_ReconciliationRefundsOutboxTickets";
    private const string AttemptCheckoutUrl = "20261021204239_AttemptCheckoutUrl";
    private const string ManualRefundRecord = "20261022112035_ManualRefundRecord";
    private const string LateSuccessPaymentExceptions = "20261022140828_LateSuccessPaymentExceptions";
    private const string FinancialTransitionHistory = "20261022151129_FinancialTransitionHistory";
    private const string PaymentAnomalies = "20261022152016_PaymentAnomalies";

    private readonly PostgreSqlDatabaseFixture _fixture;
    private readonly List<string> _databases = [];

    public PaymentsMigrationPostgreSqlTests(PostgreSqlDatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var name in _databases)
            await ExecAsync(_fixture.ConnectionString, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    // ───────────── helpers ─────────────

    private async Task<string> NewDatabaseAtAsync(string? migration)
    {
        var name = "payments_migration_" + Guid.NewGuid().ToString("N");
        _databases.Add(name);
        await ExecAsync(_fixture.ConnectionString, $"CREATE DATABASE \"{name}\"");
        var cs = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name }.ConnectionString;
        await MigrateAsync(cs, migration);
        return cs;
    }

    private static async Task MigrateAsync(string connectionString, string? target)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", PaymentsDbContext.Schema)).Options;
        await using var context = new PaymentsDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> TextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static string OrderSql(Guid id, string status, string key) =>
        "INSERT INTO payments.orders (id, customer_id, reservation_id, event_id, idempotency_key, status, currency, total_amount, payment_deadline_utc, created_at_utc, updated_at_utc) " +
        $"VALUES ('{id}', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), '{key}', '{status}', 'USD', 91.0000, now() + interval '10 minutes', now(), now());";

    private static string AttemptSql(Guid id, Guid order, string status, string provider, string reference) =>
        "INSERT INTO payments.payment_attempts (id, order_id, attempt_number, idempotency_key, status, provider, is_simulated, provider_reference, created_at_utc, completed_at_utc) " +
        $"VALUES ('{id}', '{order}', 1, 'k-{id:N}', '{status}', '{provider}', false, '{reference}', now(), {(status == "Initiated" ? "NULL" : "now()")});";

    private static string RefundSql(Guid order, Guid attempt, string state, string provider) =>
        "INSERT INTO payments.refunds (id, order_id, payment_attempt_id, amount, currency, reason, state, provider, is_simulated, submission_number, attempts, next_attempt_at_utc, created_at_utc, updated_at_utc) " +
        $"VALUES (gen_random_uuid(), '{order}', '{attempt}', 91.0000, 'USD', 'PaymentAfterSaleFailed', '{state}', '{provider}', false, 1, 0, now(), now(), now());";

    private static string SeedRefund(string state, string provider, out Guid order, out Guid attempt)
    {
        order = Guid.NewGuid();
        attempt = Guid.NewGuid();
        return OrderSql(order, "RefundRequired", "o-" + order.ToString("N")) + AttemptSql(attempt, order, "Succeeded", provider, "ref-" + attempt.ToString("N")) + RefundSql(order, attempt, state, provider);
    }

    // ───────────── existing rows stay valid ─────────────

    [Fact]
    public async Task EveryFinancialMigration_AppliesToRepresentativeExistingRows_WithoutChangingOrRejectingThem()
    {
        var cs = await NewDatabaseAtAsync(AttemptCheckoutUrl);
        var seed = new System.Text.StringBuilder();
        var states = new[] { "Requested", "Pending", "Succeeded", "Failed", "NeedsReview" };
        for (var i = 0; i < states.Length; i++)
            seed.Append(SeedRefund(states[i], i % 2 == 0 ? "Stripe" : "Paymob", out _, out _));
        var paid = Guid.NewGuid();
        var paidAttempt = Guid.NewGuid();
        var open = Guid.NewGuid();
        var openAttempt = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var failedAttempt = Guid.NewGuid();
        seed.Append(OrderSql(paid, "Paid", "o-paid") + AttemptSql(paidAttempt, paid, "Succeeded", "Stripe", "ref-paid"));
        seed.Append(OrderSql(open, "PendingPayment", "o-open") + AttemptSql(openAttempt, open, "Initiated", "Stripe", "ref-open"));
        seed.Append(OrderSql(failed, "PendingPayment", "o-failed") + AttemptSql(failedAttempt, failed, "Failed", "Stripe", "ref-failed"));
        await ExecAsync(cs, seed.ToString());
        var before = (Orders: await CountAsync(cs, "SELECT count(*) FROM payments.orders"), Attempts: await CountAsync(cs, "SELECT count(*) FROM payments.payment_attempts"),
            Refunds: await CountAsync(cs, "SELECT count(*) FROM payments.refunds"), Fingerprint: await TextAsync(cs, "SELECT string_agg(state, ',' ORDER BY state) FROM payments.refunds"));

        await MigrateAsync(cs, null); // up to the newest migration

        (await CountAsync(cs, "SELECT count(*) FROM payments.orders")).Should().Be(before.Orders);
        (await CountAsync(cs, "SELECT count(*) FROM payments.payment_attempts")).Should().Be(before.Attempts);
        (await CountAsync(cs, "SELECT count(*) FROM payments.refunds")).Should().Be(before.Refunds);
        (await TextAsync(cs, "SELECT string_agg(state, ',' ORDER BY state) FROM payments.refunds")).Should().Be(before.Fingerprint);
        (await CountAsync(cs, "SELECT count(*) FROM payments.refunds WHERE manual_recorded_by IS NOT NULL OR manual_reference IS NOT NULL")).Should().Be(0);
        (await CountAsync(cs, "SELECT count(*) FROM payments.financial_transitions")).Should().Be(0, "history starts empty: it is not backfilled");
        (await CountAsync(cs, "SELECT count(*) FROM payments.payment_exceptions")).Should().Be(0);
        (await CountAsync(cs, "SELECT count(*) FROM payments.payment_anomalies")).Should().Be(0);

        // The new triggers work on the rows that already existed: a move is recorded, a late success becomes an exception.
        await ExecAsync(cs, $"UPDATE payments.payment_attempts SET status = 'Cancelled', completed_at_utc = now() WHERE id = '{openAttempt}'");
        (await TextAsync(cs, $"SELECT from_state || '>' || to_state || '|' || source FROM payments.financial_transitions WHERE entity_id = '{openAttempt}'")).Should().Be("Initiated>Cancelled|Unspecified");
        await ExecAsync(cs, "INSERT INTO payments.payment_exceptions (id, kind, attempt_id, order_id, provider, is_simulated, amount, currency, original_attempt_status, order_status_at_detection, detected_at_utc, state) " +
            $"VALUES (gen_random_uuid(), 'LateSuccessAfterFailedAttempt', '{failedAttempt}', '{failed}', 'Stripe', false, 91, 'USD', 'Failed', 'PendingPayment', now(), 'Open')");
        (await CountAsync(cs, "SELECT count(*) FROM payments.financial_transitions WHERE entity_type = 'PaymentException'")).Should().Be(1);
    }

    // ───────────── rollback guards ─────────────

    private static async Task AssertRefusesAsync(string connectionString, string target, string migrationThatMustRemain, string table)
    {
        var failure = await Record.ExceptionAsync(() => MigrateAsync(connectionString, target));

        failure.Should().NotBeNull("a rollback must not destroy protected financial records");
        failure!.ToString().Should().Contain("Refusing to roll back").And.Contain(table);
        (await CountAsync(connectionString, $"SELECT count(*) FROM payments.\"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{migrationThatMustRemain}'"))
            .Should().Be(1, "the refused rollback changed nothing");
    }

    [Fact]
    public async Task TheManualRefundRollback_RefusesWhileAManuallyRecordedRefundExists_AndSucceedsWithout()
    {
        var protectedDb = await NewDatabaseAtAsync(ManualRefundRecord);
        await ExecAsync(protectedDb, SeedRefund("NeedsReview", "Paymob", out _, out _));
        await ExecAsync(protectedDb, $"UPDATE payments.refunds SET state = 'ManuallyRecorded', manual_recorded_by = gen_random_uuid(), manual_recorded_at_utc = now(), completed_at_utc = now()");

        await AssertRefusesAsync(protectedDb, AttemptCheckoutUrl, ManualRefundRecord, "payments.refunds");
        (await CountAsync(protectedDb, "SELECT count(*) FROM payments.refunds WHERE state = 'ManuallyRecorded'")).Should().Be(1);

        var unprotected = await NewDatabaseAtAsync(ManualRefundRecord);
        await ExecAsync(unprotected, SeedRefund("NeedsReview", "Paymob", out _, out _));
        await MigrateAsync(unprotected, AttemptCheckoutUrl);
        (await CountAsync(unprotected, "SELECT count(*) FROM payments.refunds WHERE state = 'NeedsReview'")).Should().Be(1, "an unprotected rollback keeps ordinary refunds");
        await MigrateAsync(unprotected, null);
    }

    [Fact]
    public async Task ThePaymentExceptionRollback_RefusesWhileAnExceptionExists_AndSucceedsWithout()
    {
        var protectedDb = await NewDatabaseAtAsync(LateSuccessPaymentExceptions);
        var order = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        await ExecAsync(protectedDb, OrderSql(order, "PendingPayment", "o-x") + AttemptSql(attempt, order, "Failed", "Stripe", "ref-x"));
        await ExecAsync(protectedDb, "INSERT INTO payments.payment_exceptions (id, kind, attempt_id, order_id, provider, is_simulated, amount, currency, original_attempt_status, order_status_at_detection, detected_at_utc, state) " +
            $"VALUES (gen_random_uuid(), 'LateSuccessAfterFailedAttempt', '{attempt}', '{order}', 'Stripe', false, 91, 'USD', 'Failed', 'PendingPayment', now(), 'Open')");

        await AssertRefusesAsync(protectedDb, ManualRefundRecord, LateSuccessPaymentExceptions, "payments.payment_exceptions");

        var unprotected = await NewDatabaseAtAsync(LateSuccessPaymentExceptions);
        await MigrateAsync(unprotected, ManualRefundRecord);
        await MigrateAsync(unprotected, null);
    }

    [Fact]
    public async Task TheTransitionHistoryRollback_RefusesOnceAnyHistoryExists_AndSucceedsWhileEmpty()
    {
        var protectedDb = await NewDatabaseAtAsync(FinancialTransitionHistory);
        await ExecAsync(protectedDb, OrderSql(Guid.NewGuid(), "PendingPayment", "o-h")); // the insert trigger records the order's creation
        (await CountAsync(protectedDb, "SELECT count(*) FROM payments.financial_transitions")).Should().Be(1);

        await AssertRefusesAsync(protectedDb, LateSuccessPaymentExceptions, FinancialTransitionHistory, "payments.financial_transitions");

        var empty = await NewDatabaseAtAsync(FinancialTransitionHistory);
        await MigrateAsync(empty, LateSuccessPaymentExceptions);
        await MigrateAsync(empty, null);
    }

    [Fact]
    public async Task ThePaymentAnomalyRollback_RefusesWhileAnAnomalyExists_AndSucceedsWithout()
    {
        var protectedDb = await NewDatabaseAtAsync(null);
        await ExecAsync(protectedDb, "INSERT INTO payments.payment_anomalies (id, kind, reason_code, state, dedupe_key, provider, claimed_attempt_id, received_amount_minor, received_currency, detected_at_utc) " +
            "VALUES (gen_random_uuid(), 'UnknownPaymentReference', 'unknown_attempt', 'Open', 'Stripe:evt_x', 'Stripe', gen_random_uuid(), 100, 'USD', now())");

        await AssertRefusesAsync(protectedDb, FinancialTransitionHistory, PaymentAnomalies, "payments.payment_anomalies");

        var empty = await NewDatabaseAtAsync(null);
        await MigrateAsync(empty, FinancialTransitionHistory);
        await MigrateAsync(empty, null);
    }

    [Fact]
    public async Task TheReconciliationRollback_RefusesWhileRefundsExist_AndSucceedsWithout()
    {
        var protectedDb = await NewDatabaseAtAsync(ReconciliationRefundsOutboxTickets);
        await ExecAsync(protectedDb, SeedRefund("Requested", "Stripe", out _, out _));

        await AssertRefusesAsync(protectedDb, InitialPayments, ReconciliationRefundsOutboxTickets, "payments.refunds");
        (await CountAsync(protectedDb, "SELECT count(*) FROM payments.refunds")).Should().Be(1, "a refused rollback keeps the refund");

        var empty = await NewDatabaseAtAsync(ReconciliationRefundsOutboxTickets);
        await MigrateAsync(empty, InitialPayments);
        await MigrateAsync(empty, null);
    }

    [Fact]
    public async Task TheInitialRollback_RefusesWhileOrdersExist_AndSucceedsWithout()
    {
        var protectedDb = await NewDatabaseAtAsync(InitialPayments);
        var order = Guid.NewGuid();
        await ExecAsync(protectedDb, OrderSql(order, "PendingPayment", "o-" + order.ToString("N")));

        await AssertRefusesAsync(protectedDb, "0", InitialPayments, "payments.orders");
        (await CountAsync(protectedDb, "SELECT count(*) FROM payments.orders")).Should().Be(1, "a refused rollback keeps the order");

        var empty = await NewDatabaseAtAsync(InitialPayments);
        await MigrateAsync(empty, "0");
        (await CountAsync(empty, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'payments' AND table_name = 'orders'")).Should().Be(0);
        await MigrateAsync(empty, null);
    }

    [Fact]
    public async Task TheFullChain_RollsBackAndForwardOnAnEmptyDatabase_ThroughEveryFinancialMigration()
    {
        var cs = await NewDatabaseAtAsync(null);
        foreach (var target in new[] { FinancialTransitionHistory, LateSuccessPaymentExceptions, ManualRefundRecord, AttemptCheckoutUrl, InitialPayments, "0" })
            await MigrateAsync(cs, target);
        (await CountAsync(cs, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'payments' AND table_name IN ('payment_exceptions', 'payment_anomalies', 'financial_transitions')")).Should().Be(0);

        await MigrateAsync(cs, null);
        (await CountAsync(cs, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'payments' AND table_name IN ('payment_exceptions', 'payment_anomalies', 'financial_transitions')")).Should().Be(3);
    }

    // ───────────── the documented least-privilege application role (docs/architecture/24) ─────────────

    /// <summary>What the application needs per payments table. Kept equal to the unit-tested matrix and to the runbook.</summary>
    private static readonly string[] ExpectedPrivileges =
    [
        "admin_audit_log:INSERT,SELECT", "financial_transitions:INSERT,SELECT", "notification_deliveries:INSERT,SELECT",
        "order_items:INSERT,SELECT", "orders:INSERT,SELECT,UPDATE", "outbox_events:INSERT,SELECT,UPDATE",
        "payment_anomalies:INSERT,SELECT,UPDATE", "payment_attempts:INSERT,SELECT,UPDATE", "payment_exceptions:INSERT,SELECT,UPDATE",
        "reconciliation_tasks:INSERT,SELECT,UPDATE", "refunds:INSERT,SELECT,UPDATE", "tickets:INSERT,SELECT"
    ];

    private static string SolutionFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PulseReserve.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the runbook is read from the repository");
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static async Task AssertDeniedAsync(string connectionString, string sql)
    {
        var failure = await Record.ExceptionAsync(() => ExecAsync(connectionString, sql));

        failure.Should().BeOfType<PostgresException>($"'{sql}' must be refused for the application role");
        ((PostgresException)failure!).SqlState.Should().Be("42501", $"'{sql}' fails with insufficient_privilege, not with a trigger or a typo");
    }

    [Fact]
    public async Task TheDocumentedLeastPrivilegeRole_DoesWhatTheApplicationNeeds_AndCannotErase_OrDisableTheEvidenceProtection()
    {
        // The grants come from the runbook's own SQL block, so the document and this test cannot drift apart. The role is created in the
        // throw-away container only, named uniquely, with a password generated for this run, and removed at the end.
        var document = await File.ReadAllTextAsync(SolutionFile("docs", "architecture", "24-database-roles-and-evidence-protection.md"));
        var section = document[document.IndexOf("## 4.", StringComparison.Ordinal)..document.IndexOf("## 5.", StringComparison.Ordinal)];
        var open = section.IndexOf("```sql", StringComparison.Ordinal) + "```sql".Length;
        var template = section[open..section.IndexOf("```", open, StringComparison.Ordinal)];

        var role = "pulsereserve_app_" + Guid.NewGuid().ToString("N")[..12];
        var password = Guid.NewGuid().ToString("N");
        var admin = await NewDatabaseAtAsync(null); // every financial migration applied, as the container's own administrator
        try
        {
            await ExecAsync(admin, template.Replace("pulsereserve_app", role).Replace(":'app_password'", $"'{password}'"));
            var app = new NpgsqlConnectionStringBuilder(admin) { Username = role, Password = password }.ConnectionString;

            // 1. The role is unprivileged and owns nothing, and no financial trigger is switched off.
            (await TextAsync(admin, $"SELECT rolsuper::text || rolcreatedb::text || rolcreaterole::text || rolreplication::text || rolbypassrls::text FROM pg_roles WHERE rolname = '{role}'"))
                .Should().Be("falsefalsefalsefalsefalse");
            (await CountAsync(admin, $"SELECT count(*) FROM pg_tables WHERE schemaname = 'payments' AND tableowner = '{role}'")).Should().Be(0);
            (await CountAsync(admin, "SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'payments' AND NOT t.tgisinternal AND t.tgenabled <> 'O'")).Should().Be(0);

            // 2. Its effective privileges are exactly the matrix the application needs - nothing more.
            var effective = await TextAsync(admin,
                "SELECT string_agg(x, ';' ORDER BY x COLLATE \"C\") FROM (SELECT c.relname || ':' || string_agg(p.priv, ',' ORDER BY p.priv) AS x " +
                "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER')) AS p(priv) " +
                $"WHERE n.nspname = 'payments' AND c.relkind = 'r' AND has_table_privilege('{role}', c.oid, p.priv) GROUP BY c.relname) t");
            effective.Should().Be(string.Join(';', ExpectedPrivileges.OrderBy(e => e, StringComparer.Ordinal)));
            (await TextAsync(admin, $"SELECT has_schema_privilege('{role}', 'payments', 'CREATE')::text")).Should().Be("false");

            // 3. What the application really does works: an insert and a status change, with the history rows written by the trigger
            //    (the trigger inserts as the invoking role, so this proves the INSERT-only grant on the history table is sufficient).
            var order = Guid.NewGuid();
            await ExecAsync(app, OrderSql(order, "PendingPayment", "o-" + order.ToString("N")));
            await ExecAsync(app, $"UPDATE payments.orders SET status = 'Cancelled', updated_at_utc = now() WHERE id = '{order}'");
            (await TextAsync(app, $"SELECT string_agg(COALESCE(from_state, 'new') || '>' || to_state, ',' ORDER BY occurred_at_utc, to_state) FROM payments.financial_transitions WHERE entity_type = 'Order' AND entity_id = '{order}'"))
                .Should().Be("new>PendingPayment,PendingPayment>Cancelled");

            // 4. What would erase or hide evidence is refused by privilege (not merely by a trigger).
            foreach (var sql in new[]
            {
                "UPDATE payments.financial_transitions SET reason = reason",
                "DELETE FROM payments.financial_transitions",
                "TRUNCATE payments.financial_transitions",
                "TRUNCATE payments.admin_audit_log",
                "DELETE FROM payments.payment_anomalies",
                "DELETE FROM payments.payment_exceptions",
                "UPDATE payments.tickets SET status = status",
                "DELETE FROM payments.orders",
                "ALTER TABLE payments.financial_transitions DISABLE TRIGGER ALL",
                "DROP TRIGGER trg_financial_transitions_append_only ON payments.financial_transitions",
                "DROP TABLE payments.orders",
                "SET session_replication_role = replica",
                "CREATE TABLE payments.not_allowed (id int)"
            })
            {
                await AssertDeniedAsync(app, sql);
            }

            // ... and the evidence the role wrote is still there.
            (await CountAsync(admin, $"SELECT count(*) FROM payments.financial_transitions WHERE entity_id = '{order}'")).Should().Be(2);
        }
        finally
        {
            await ExecAsync(admin, $"DROP OWNED BY {role}");
            await ExecAsync(_fixture.ConnectionString, $"DROP ROLE IF EXISTS {role}");
        }
    }
}

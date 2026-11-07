using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Source-level (static) checks of what the APPLICATION is allowed to do to the financial tables. They read the C# source and the
/// migrations; they prove what the code asks the database to do, NOT what a PostgreSQL role is able to do. The privilege matrix below is
/// the one the deployment runbook (docs/architecture/24-database-roles-and-evidence-protection.md) tells operators to grant, so a code
/// change that needs more privilege fails here until the runbook and this table are changed together.
/// A real role-privilege test needs a PostgreSQL role and is listed as not executed in the runbook.
/// </summary>
public sealed class FinancialDatabaseBoundaryTests
{
    /// <summary>The privileges the application role needs, per payments table. Derived from the SQL; granted by the runbook; nothing else.</summary>
    private static readonly Dictionary<string, string[]> Matrix = new()
    {
        ["admin_audit_log"] = ["INSERT", "SELECT"],
        ["financial_transitions"] = ["INSERT", "SELECT"],
        ["notification_deliveries"] = ["INSERT", "SELECT"],
        ["order_items"] = ["INSERT", "SELECT"],
        ["orders"] = ["INSERT", "SELECT", "UPDATE"],
        ["outbox_events"] = ["INSERT", "SELECT", "UPDATE"],
        ["payment_anomalies"] = ["INSERT", "SELECT", "UPDATE"],
        ["payment_attempts"] = ["INSERT", "SELECT", "UPDATE"],
        ["payment_exceptions"] = ["INSERT", "SELECT", "UPDATE"],
        ["reconciliation_tasks"] = ["INSERT", "SELECT", "UPDATE"],
        ["refunds"] = ["INSERT", "SELECT", "UPDATE"],
        ["tickets"] = ["INSERT", "SELECT"]
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PulseReserve.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the checks read the repository source, so the solution root must be found");
        return dir!.FullName;
    }

    /// <summary>Application source with comments removed; migrations, generated code and tests excluded.</summary>
    private static IEnumerable<(string Path, string Text)> ApplicationSource()
    {
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var normalised = file.Replace('\\', '/');
            if (normalised.Contains("/obj/") || normalised.Contains("/bin/") || normalised.Contains("/Migrations/"))
                continue;
            var lines = File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            yield return (file, string.Join('\n', lines));
        }
    }

    private static IEnumerable<(string Table, string Operation)> Statements(string text)
    {
        foreach (Match m in Regex.Matches(text, @"(?is)\bINSERT\s+INTO\s+payments\.(\w+)"))
            yield return (m.Groups[1].Value, "INSERT");
        foreach (Match m in Regex.Matches(text, @"(?is)\bUPDATE\s+payments\.(\w+)"))
            yield return (m.Groups[1].Value, "UPDATE");
        foreach (Match m in Regex.Matches(text, @"(?is)\bDELETE\s+FROM\s+payments\.(\w+)"))
            yield return (m.Groups[1].Value, "DELETE");
        foreach (Match m in Regex.Matches(text, @"(?is)\b(?:FROM|JOIN)\s+payments\.(\w+)"))
            yield return (m.Groups[1].Value, "SELECT");
    }

    [Fact]
    public void TheApplicationAsksForExactlyTheDocumentedPrivileges_OnEveryPaymentsTable()
    {
        var needed = ApplicationSource()
            .SelectMany(f => Statements(f.Text))
            .GroupBy(s => s.Table)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Operation).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToArray());

        needed.Keys.Should().BeEquivalentTo(Matrix.Keys, "a new or removed table must be added to (or dropped from) the runbook's grants and this matrix together");
        foreach (var (table, privileges) in Matrix)
            needed[table].Should().Equal(privileges, $"payments.{table}: the runbook grants exactly this and the code needs exactly this");
    }

    [Fact]
    public void TheApplicationNeverDeletesTruncatesOrDropsFinancialData_AndNeverChangesGrantsOrTriggers()
    {
        var forbidden = new[]
        {
            (@"(?i)\bDELETE\s+FROM\s+payments\.", "deletes a payments row"),
            (@"(?i)\bTRUNCATE\s+(TABLE\b|payments\.)", "truncates a table"),
            (@"(?i)\bDROP\s+(TABLE|SCHEMA|TRIGGER|FUNCTION)\b", "drops a database object"),
            (@"(?i)\bALTER\s+TABLE\b", "alters a table"),
            (@"(?i)\b(DISABLE|ENABLE)\s+TRIGGER\b", "switches a trigger off or on"),
            (@"(?i)\bsession_replication_role\b", "bypasses triggers for the session"),
            (@"(?i)\b(GRANT|REVOKE)\s+\w+", "changes privileges"),
            (@"(?i)\bCREATE\s+(ROLE|USER)\b", "creates a role")
        };

        var offences = from file in ApplicationSource()
                       from rule in forbidden
                       where Regex.IsMatch(file.Text, rule.Item1)
                       select $"{Path.GetFileName(file.Path)} {rule.Item2}";

        offences.Should().BeEmpty("the append-only and evidence tables are protected by never being the target of such statements, and the grants are the operator's");
    }

    [Fact]
    public void NoStoreEverUpdatesTheAppendOnlyTables()
    {
        var updates = ApplicationSource()
            .SelectMany(f => Statements(f.Text))
            .Where(s => s.Operation is "UPDATE" or "DELETE")
            .Select(s => s.Table)
            .Distinct()
            .ToArray();

        updates.Should().NotContain(["financial_transitions", "admin_audit_log", "tickets", "order_items", "notification_deliveries"],
            "history, the audit trail, tickets, order items and the notification ledger are insert-only for the application");
    }

    // ───────────── the integration test safety net stays in place ─────────────

    [Fact]
    public void TheIntegrationHostKeepsTheWorkerOffAndOptsIntoTheSimulatorExplicitly_AndTheWorkerStillWaitsBeforeItsFirstQuery()
    {
        var factory = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "PulseReserve.IntegrationTests", "Api", "CatalogApiFactory.cs"));
        factory.Should().Contain("builder.UseSetting(\"Payments:Workers:Enabled\", \"false\")", "no background database work may start before the target-database guard has run");
        factory.Should().Contain("builder.UseSetting(\"Payments:Simulator:AllowInTestingEnvironment\", \"true\")", "Testing is not permissive by name alone");
        factory.Should().Contain("TestDatabaseGuard.EnsureTargetsExpectedDatabase", "every module context is checked before a client exists");

        var worker = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PulseReserve.AppHost", "Composition", "PaymentsWorkerAndNotifications.cs"));
        var delay = worker.IndexOf("Random.Shared.Next(200, 2000)", StringComparison.Ordinal);
        var firstPass = worker.IndexOf("RunOnceAsync", StringComparison.Ordinal);
        delay.Should().BeGreaterThan(0, "the worker's start offset is what keeps its first query behind the guard");
        delay.Should().BeLessThan(firstPass, "the offset must come before the first workflow pass");
    }

    [Fact]
    public void TheIntegrationHostKeepsTheLiveWorkersOff_AndBothStillWaitBeforeTheirFirstDatabaseCall()
    {
        var factory = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "PulseReserve.IntegrationTests", "Api", "CatalogApiFactory.cs"));
        factory.Should().Contain("builder.UseSetting(\"Live:ChangeFeed:Enabled\", \"false\")", "the NOTIFY listener opens its own connection and must not run before the guard");
        factory.Should().Contain("builder.UseSetting(\"Reservation:HoldExpiry:Enabled\", \"false\")", "the hold-expiry worker writes to the database and must not run before the guard");

        var live = Path.Combine(RepoRoot(), "src", "PulseReserve.AppHost", "Live");

        var expiry = File.ReadAllText(Path.Combine(live, "HoldExpiryWorker.cs"));
        var expiryDelay = expiry.IndexOf("Random.Shared.Next(200, 2000)", StringComparison.Ordinal);
        var expiryFirstPass = expiry.IndexOf("await DrainAsync(stoppingToken)", StringComparison.Ordinal);
        expiryDelay.Should().BeGreaterThan(0, "the hold-expiry worker's start offset keeps its first query behind the guard");
        expiryDelay.Should().BeLessThan(expiryFirstPass, "the offset must come before the first pass");

        var feed = File.ReadAllText(Path.Combine(live, "ChangeFeed.cs"));
        feed.Should().Contain("TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(2000)", "the listener's default start offset is 200-2000 ms");
        var feedDelay = feed.IndexOf("await Task.Delay(delay, clock, stoppingToken)", StringComparison.Ordinal);
        var feedFirstConnect = feed.IndexOf("await RunAsync(stoppingToken)", StringComparison.Ordinal);
        feedDelay.Should().BeGreaterThan(0);
        feedDelay.Should().BeLessThan(feedFirstConnect, "the listener must wait before it opens its first connection");
    }

    // ───────────── migrations: a rollback may not silently destroy financial evidence ─────────────

    [Fact]
    public void EveryMigrationRollbackThatDropsATable_RefusesFirst_WhileFinancialRowsExist()
    {
        var folder = Path.Combine(RepoRoot(), "src", "Modules", "Payments", "PulseReserve.Modules.Payments", "Persistence", "Postgres", "Migrations");
        var migrations = Directory.EnumerateFiles(folder, "2*.cs").Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal)).ToArray();
        migrations.Length.Should().BeGreaterThanOrEqualTo(7, "every payments migration is checked");

        foreach (var file in migrations)
        {
            var text = File.ReadAllText(file);
            var down = text[text.IndexOf("protected override void Down", StringComparison.Ordinal)..];
            var firstDrop = new[] { down.IndexOf("DropTable(", StringComparison.Ordinal), down.IndexOf("DROP TABLE", StringComparison.OrdinalIgnoreCase) }
                .Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            if (firstDrop < 0)
                continue;

            var guard = down.IndexOf("Refusing to roll back", StringComparison.Ordinal);
            guard.Should().BeGreaterThanOrEqualTo(0, $"{Path.GetFileName(file)} drops a table, so its rollback must refuse while it holds records");
            guard.Should().BeLessThan(firstDrop, $"{Path.GetFileName(file)}: the guard must run before the first DROP");
            down.Should().Contain("RAISE EXCEPTION", Path.GetFileName(file));
        }
    }

    [Fact]
    public void OnlyTheCheckoutLinkColumn_MayBeDroppedByARollbackWithoutAGuard()
    {
        var folder = Path.Combine(RepoRoot(), "src", "Modules", "Payments", "PulseReserve.Modules.Payments", "Persistence", "Postgres", "Migrations");
        var unguarded = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "2*.cs").Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            var down = text[text.IndexOf("protected override void Down", StringComparison.Ordinal)..];
            if (down.Contains("DropColumn(", StringComparison.Ordinal) && !down.Contains("Refusing to roll back", StringComparison.Ordinal))
                unguarded.Add(Path.GetFileName(file));
        }

        // checkout_url is a convenience copy of the provider's hosted-page link, never evidence; every other dropped column is guarded.
        unguarded.Should().Equal("20261021204239_AttemptCheckoutUrl.cs");
    }
}

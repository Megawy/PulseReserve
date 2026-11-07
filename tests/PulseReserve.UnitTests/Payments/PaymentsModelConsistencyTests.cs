using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Persistence.Postgres;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// Cross-checks the domain's state constants against the EF model that generates the database constraints. It reads the
/// MODEL only (no connection is opened): it catches a state that the code can write but the column or CHECK constraint
/// would reject, such as a new state longer than its column. It does not prove the constraint is enforced by PostgreSQL.
/// </summary>
public sealed class PaymentsModelConsistencyTests
{
    private static IModel Model()
    {
        using var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql("Host=localhost;Database=model-only").Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static string[] Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    public static TheoryData<string, string, string, Type> StateColumns => new()
    {
        { "refunds", "state", "chk_refunds_state", typeof(RefundState) },
        { "orders", "status", "chk_orders_status", typeof(OrderStatus) },
        { "payment_attempts", "status", "chk_payment_attempts_status", typeof(PaymentAttemptStatus) },
        { "reconciliation_tasks", "state", "chk_reconciliation_tasks_state", typeof(WorkState) },
        { "outbox_events", "state", "chk_outbox_events_state", typeof(WorkState) },
        { "payment_exceptions", "state", "chk_payment_exceptions_state", typeof(PaymentExceptionState) },
        { "payment_anomalies", "state", "chk_payment_anomalies_state", typeof(PaymentAnomalyState) },
        { "payment_anomalies", "kind", "chk_payment_anomalies_kind", typeof(PaymentAnomalyKind) },
        { "payment_anomalies", "resolution", "chk_payment_anomalies_resolution_value", typeof(PaymentAnomalyResolution) },
        { "payment_exceptions", "kind", "chk_payment_exceptions_kind", typeof(PaymentExceptionKind) },
        { "payment_exceptions", "resolution", "chk_payment_exceptions_resolution_value", typeof(PaymentExceptionResolution) },
    };

    [Theory]
    [MemberData(nameof(StateColumns))]
    public void EveryDeclaredState_FitsItsColumn_AndIsAllowedByItsCheckConstraint(string table, string column, string constraint, Type states)
    {
        var entity = Model().GetEntityTypes().Single(e => e.GetTableName() == table);
        var property = entity.GetProperties().Single(p => p.GetColumnName() == column);
        var check = entity.GetCheckConstraints().Single(c => c.Name == constraint);

        foreach (var state in Constants(states))
        {
            state.Length.Should().BeLessThanOrEqualTo(property.GetMaxLength() ?? int.MaxValue, $"'{state}' must fit {table}.{column}");
            check.Sql.Should().Contain($"'{state}'", $"{constraint} must allow '{state}'");
        }
    }

    [Fact]
    public void AnAnomaly_IsNeverLinkedToAnAttemptOrOrderUnlessTheEventMatchedThemConsistently()
    {
        var entity = Model().GetEntityTypes().Single(e => e.GetTableName() == "payment_anomalies");
        var association = entity.GetCheckConstraints().Single(c => c.Name == "chk_payment_anomalies_association").Sql;

        association.Should().Contain("UnknownPaymentReference").And.Contain("InconsistentAssociation").And.Contain("attempt_id IS NULL AND order_id IS NULL");
        association.Should().Contain("AmountOrCurrencyMismatch").And.Contain("ReferenceMismatch").And.Contain("attempt_id IS NOT NULL AND order_id IS NOT NULL");
        entity.GetIndexes().Should().Contain(i => i.IsUnique && i.GetDatabaseName() == "uq_payment_anomalies_kind_dedupe", "one anomaly per provider event is decided by the database");
        entity.GetProperties().Select(p => p.GetColumnName()).Should().Contain(["received_amount_minor", "received_currency", "expected_amount_minor", "expected_currency"],
            "untrusted received values and trusted expected values live in separate columns");
    }

    [Fact]
    public void TheTransitionHistory_HasNoForeignKeys_SoItOutlivesEveryOtherRow_AndDedupesRejectedEventsByProviderEvent()
    {
        var entity = Model().GetEntityTypes().Single(e => e.GetTableName() == "financial_transitions");

        entity.GetForeignKeys().Should().BeEmpty();
        entity.GetProperties().Select(p => p.GetColumnName()).Should().Contain(
            ["entity_type", "entity_id", "order_id", "from_state", "to_state", "event_code", "kind", "source", "fact_basis", "provider", "provider_event_id", "actor_user_id", "correlation_id", "reason"]);
        entity.GetIndexes().Should().Contain(i => i.IsUnique && i.GetDatabaseName() == "uq_financial_transitions_rejected_event" && (i.GetFilter() ?? "").Contains("Rejected"));
        entity.GetProperties().Select(p => p.GetColumnName()).Should().NotContain(c => c.Contains("payload") || c.Contains("signature") || c.Contains("secret") || c.Contains("reference") || c.Contains("customer"));
    }

    [Fact]
    public void AnExceptionResolution_IsAllOrNothing_AndTheExceptionKeepsTheFailedVerdictAsEvidence()
    {
        var entity = Model().GetEntityTypes().Single(e => e.GetTableName() == "payment_exceptions");
        var sql = entity.GetCheckConstraints().Single(c => c.Name == "chk_payment_exceptions_resolution_fields").Sql;

        foreach (var column in new[] { "resolution", "resolved_by", "resolved_at_utc", "resolution_reference", "resolution_note" })
            sql.Should().Contain(column);
        entity.GetProperties().Select(p => p.GetColumnName()).Should().Contain(["original_attempt_status", "original_failure_code", "original_completed_at_utc", "order_status_at_detection"]);
        entity.GetIndexes().Should().Contain(i => i.IsUnique && i.GetDatabaseName() == "uq_payment_exceptions_attempt_kind", "one exception per attempt is decided by the database");
    }

    [Fact]
    public void AManualRefundRecord_IsAllOrNothing_AndKeptApartFromProviderConfirmation()
    {
        var refunds = Model().GetEntityTypes().Single(e => e.GetTableName() == "refunds");
        var sql = refunds.GetCheckConstraints().Single(c => c.Name == "chk_refunds_manual_fields").Sql;

        foreach (var column in new[] { "manual_recorded_by", "manual_recorded_at_utc", "manual_reference", "manual_note" })
        {
            refunds.GetProperties().Select(p => p.GetColumnName()).Should().Contain(column);
            sql.Should().Contain(column);
        }

        sql.Should().Contain(RefundState.ManuallyRecorded);
        sql.Should().NotContain("provider_refund_id", "a manual record never claims a provider refund id");
        RefundState.ManuallyRecorded.Should().NotBe(RefundState.Succeeded);
    }
}

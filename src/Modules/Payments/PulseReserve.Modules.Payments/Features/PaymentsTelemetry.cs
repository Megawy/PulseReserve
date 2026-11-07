using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace PulseReserve.Modules.Payments.Features;

/// <summary>
/// Stable log event ids for the payment events an operator may want to alert on. The ids and names never change meaning, so a log query
/// or alert rule written today keeps working. Every message behind them carries identifiers and codes only (provider, provider event id,
/// anomaly kind, reason code, our own attempt id): never a payload, signature, secret, provider reference, amount of a customer or any
/// personal data.
/// </summary>
public static class PaymentsLogEvents
{
    /// <summary>A signature-verified success was NOT applied and its evidence is now stored (once per provider event). Warning.</summary>
    public static readonly EventId AnomalyRecorded = new(7101, nameof(AnomalyRecorded));

    /// <summary>The same provider event arrived again; its record already existed, so it was acknowledged without a new row. Information.</summary>
    public static readonly EventId AnomalyDuplicate = new(7102, nameof(AnomalyDuplicate));

    /// <summary>The evidence could NOT be stored, so the webhook was not acknowledged and the provider will deliver it again. Error.</summary>
    public static readonly EventId AnomalyPersistenceFailed = new(7103, nameof(AnomalyPersistenceFailed));

    /// <summary>A verified failure/expiry that matches no payment moved no money; acknowledged without a record. Information.</summary>
    public static readonly EventId UnmatchedEventIgnored = new(7104, nameof(UnmatchedEventIgnored));

    /// <summary>A verified success could not be judged because our own order data is unusable; not acknowledged. Error.</summary>
    public static readonly EventId EventNotJudgeable = new(7105, nameof(EventNotJudgeable));

    /// <summary>Payment items are waiting for an administrator (throttled summary from the worker). Warning.</summary>
    public static readonly EventId UnresolvedBacklog = new(7110, nameof(UnresolvedBacklog));

    /// <summary>Nothing is waiting for an administrator any more. Information.</summary>
    public static readonly EventId UnresolvedBacklogCleared = new(7111, nameof(UnresolvedBacklogCleared));
}

/// <summary>
/// Counters on the .NET <see cref="Meter"/> <c>PulseReserve.Payments</c> (part of the framework: no package, no exporter, free; they cost
/// nothing until something such as <c>dotnet-counters</c> or an OpenTelemetry collector listens). Tags are bounded sets (provider name,
/// outcome, anomaly kind), never ids. The authoritative number of unresolved anomalies is the one counted from the database
/// (<c>GET /api/admin/payments/overview</c>); these counters describe what THIS process saw since it started.
/// </summary>
public static class PaymentsTelemetry
{
    public const string MeterName = "PulseReserve.Payments";

    public const string WebhookEventsCounter = "pulsereserve.payments.webhook.events";
    public const string AnomaliesRecordedCounter = "pulsereserve.payments.anomalies.recorded";
    public const string AnomaliesDeduplicatedCounter = "pulsereserve.payments.anomalies.deduplicated";
    public const string AnomalyPersistenceFailuresCounter = "pulsereserve.payments.anomalies.persistence_failures";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> WebhookEvents =
        Meter.CreateCounter<long>(WebhookEventsCounter, description: "Webhook deliveries answered, by provider and outcome (Applied, Acknowledged, AnomalyRecorded, Rejected, Failed, ProviderUnavailable, Error).");

    private static readonly Counter<long> AnomaliesRecorded =
        Meter.CreateCounter<long>(AnomaliesRecordedCounter, description: "New payment anomalies stored, by provider and kind.");

    private static readonly Counter<long> AnomaliesDeduplicated =
        Meter.CreateCounter<long>(AnomaliesDeduplicatedCounter, description: "Redelivered anomaly events absorbed by an existing record, by provider and kind.");

    private static readonly Counter<long> AnomalyPersistenceFailures =
        Meter.CreateCounter<long>(AnomalyPersistenceFailuresCounter, description: "Anomaly evidence that could not be stored (the webhook was not acknowledged), by provider and kind.");

    internal static void Webhook(string provider, string outcome) =>
        WebhookEvents.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("outcome", outcome));

    internal static void AnomalyRecordedCount(string provider, string kind) =>
        AnomaliesRecorded.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("kind", kind));

    internal static void AnomalyDeduplicatedCount(string provider, string kind) =>
        AnomaliesDeduplicated.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("kind", kind));

    internal static void AnomalyPersistenceFailure(string provider, string kind) =>
        AnomalyPersistenceFailures.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("kind", kind));
}

/// <summary>
/// Turns the durable unresolved counts into a calm operational signal. It never reads memory about anomalies: it is handed the summary
/// that was counted from the database, and it logs
/// <list type="bullet">
/// <item>immediately when the number of open anomalies or exceptions GROWS (a new item needs attention now),</item>
/// <item>again as a reminder at most once per <c>reminderEvery</c> while anything stays open (so a forgotten item keeps surfacing),</item>
/// <item>once, at Information, when everything has been resolved.</item>
/// </list>
/// It stays silent otherwise, so it cannot bury an incident in per-pass noise. State lives in this process: after a restart the first
/// observation of a non-empty backlog logs once more, which is the desired reminder.
/// </summary>
public sealed class UnresolvedPaymentItemsReporter(ILogger logger, TimeProvider clock, TimeSpan reminderEvery)
{
    private int _anomalies;
    private int _exceptions;
    private DateTimeOffset? _lastWarned;

    public void Observe(WorkflowSummary summary)
    {
        var anomalies = summary.PaymentAnomaliesOpen;
        var exceptions = summary.PaymentExceptionsOpen;

        if (anomalies == 0 && exceptions == 0)
        {
            if (_anomalies + _exceptions > 0)
                logger.LogInformation(PaymentsLogEvents.UnresolvedBacklogCleared, "No payment anomalies or late-success exceptions are waiting for an administrator any more.");
            (_anomalies, _exceptions, _lastWarned) = (0, 0, null);
            return;
        }

        var now = clock.GetUtcNow();
        var grew = anomalies > _anomalies || exceptions > _exceptions;
        var reminderDue = _lastWarned is null || now - _lastWarned >= reminderEvery;
        (_anomalies, _exceptions) = (anomalies, exceptions);
        if (!grew && !reminderDue)
            return;

        _lastWarned = now;
        logger.LogWarning(
            PaymentsLogEvents.UnresolvedBacklog,
            "Payment items are waiting for an administrator: {OpenAnomalies} anomalies (oldest {OldestAnomalyAgeHours:F1} h; by kind {AnomalyKinds}) and {OpenExceptions} late-success exceptions (oldest {OldestExceptionAgeHours:F1} h). Review them under Admin > Payments.",
            anomalies,
            (summary.OldestOpenPaymentAnomalyAgeSeconds ?? 0) / 3600.0,
            string.Join(", ", summary.PaymentAnomaliesOpenByKind.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")),
            exceptions,
            (summary.OldestOpenPaymentExceptionAgeSeconds ?? 0) / 3600.0);
    }
}

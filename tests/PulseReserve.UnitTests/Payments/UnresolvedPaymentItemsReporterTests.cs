using FluentAssertions;
using Microsoft.Extensions.Logging;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using Xunit;

namespace PulseReserve.UnitTests.Payments;

/// <summary>
/// The calm operational signal for unresolved payment items. The reporter is handed a summary that the host counted from the database, so
/// these tests prove only the signalling rules (when it speaks, how loudly, what it says); that the numbers really come from durable state
/// is covered by the PostgreSQL integration tests (compiled, not executed).
/// </summary>
public sealed class UnresolvedPaymentItemsReporterTests
{
    private static readonly TimeSpan Reminder = TimeSpan.FromHours(6);

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static WorkflowSummary Summary(int anomalies, int exceptions = 0, long? oldestAnomaly = null, long? oldestException = null,
        Dictionary<string, int>? byKind = null) =>
        new(0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>())
        {
            PaymentAnomaliesOpen = anomalies,
            PaymentExceptionsOpen = exceptions,
            OldestOpenPaymentAnomalyAgeSeconds = oldestAnomaly,
            OldestOpenPaymentExceptionAgeSeconds = oldestException,
            PaymentAnomaliesOpenByKind = byKind ?? (anomalies > 0 ? new Dictionary<string, int> { [PaymentAnomalyKind.AmountOrCurrencyMismatch] = anomalies } : new Dictionary<string, int>())
        };

    private static (UnresolvedPaymentItemsReporter Reporter, RecordingLogger Log, ManualClock Clock) Create()
    {
        var log = new RecordingLogger();
        var clock = new ManualClock();
        return (new UnresolvedPaymentItemsReporter(log, clock, Reminder), log, clock);
    }

    [Fact]
    public void NothingWaiting_IsSilent()
    {
        var (reporter, log, _) = Create();

        reporter.Observe(Summary(0));
        reporter.Observe(Summary(0));

        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public void TheFirstObservationOfABacklog_IsOneWarning_WithCountsAgeAndKinds_AndNothingSensitive()
    {
        var (reporter, log, _) = Create();

        reporter.Observe(Summary(2, 1, oldestAnomaly: 7200, oldestException: 1800, byKind: new() { [PaymentAnomalyKind.AmountOrCurrencyMismatch] = 1, [PaymentAnomalyKind.UnknownPaymentReference] = 1 }));

        var entry = log.With(PaymentsLogEvents.UnresolvedBacklog).Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("2 anomalies").And.Contain($"oldest {2.0:F1} h").And.Contain("1 late-success exceptions").And.Contain($"oldest {0.5:F1} h")
            .And.Contain("AmountOrCurrencyMismatch=1").And.Contain("UnknownPaymentReference=1");
        entry.Message.ToLowerInvariant().Should().NotContain("evt_").And.NotContain("secret").And.NotContain("signature");
    }

    [Fact]
    public void AnUnchangedBacklog_DoesNotRepeatEveryPass_ButRemindsOncePerInterval()
    {
        var (reporter, log, clock) = Create();
        reporter.Observe(Summary(1, oldestAnomaly: 60));

        for (var i = 0; i < 100; i++)
        {
            clock.Now += TimeSpan.FromMinutes(1);
            if (clock.Now - new DateTimeOffset(2030, 1, 1, 9, 0, 0, TimeSpan.Zero) >= Reminder)
                break;
            reporter.Observe(Summary(1, oldestAnomaly: 60 + (i * 60)));
        }

        log.With(PaymentsLogEvents.UnresolvedBacklog).Should().ContainSingle("no reminder is due before the interval has passed");

        clock.Now = new DateTimeOffset(2030, 1, 1, 9, 0, 0, TimeSpan.Zero) + Reminder;
        reporter.Observe(Summary(1, oldestAnomaly: 21600));

        log.With(PaymentsLogEvents.UnresolvedBacklog).Should().HaveCount(2, "a forgotten item surfaces again after the interval");
    }

    [Fact]
    public void ANewItem_IsReportedAtOnce_EvenInsideTheReminderInterval()
    {
        var (reporter, log, clock) = Create();
        reporter.Observe(Summary(1));
        clock.Now += TimeSpan.FromMinutes(1);

        reporter.Observe(Summary(2));
        reporter.Observe(Summary(2));

        log.With(PaymentsLogEvents.UnresolvedBacklog).Should().HaveCount(2).And.OnlyContain(e => e.Level == LogLevel.Warning);

        clock.Now += TimeSpan.FromMinutes(1);
        reporter.Observe(Summary(2, 1));
        log.With(PaymentsLogEvents.UnresolvedBacklog).Should().HaveCount(3, "a new late-success exception counts as growth too");
    }

    [Fact]
    public void AShrinkingButNonEmptyBacklog_IsQuiet_UntilTheReminderIsDue()
    {
        var (reporter, log, clock) = Create();
        reporter.Observe(Summary(3));
        clock.Now += TimeSpan.FromMinutes(5);

        reporter.Observe(Summary(2));

        log.With(PaymentsLogEvents.UnresolvedBacklog).Should().ContainSingle("progress is not an incident");
        log.With(PaymentsLogEvents.UnresolvedBacklogCleared).Should().BeEmpty();
    }

    [Fact]
    public void WhenEverythingIsResolved_ItSaysSoOnce_AndTheNextItemStartsAFreshWarning()
    {
        var (reporter, log, clock) = Create();
        reporter.Observe(Summary(1));
        clock.Now += TimeSpan.FromMinutes(2);

        reporter.Observe(Summary(0));
        reporter.Observe(Summary(0));

        var cleared = log.With(PaymentsLogEvents.UnresolvedBacklogCleared).Should().ContainSingle().Subject;
        cleared.Level.Should().Be(LogLevel.Information);

        clock.Now += TimeSpan.FromMinutes(2);
        reporter.Observe(Summary(1));
        log.With(PaymentsLogEvents.UnresolvedBacklog).Should().HaveCount(2, "after a clean state the next unresolved item is reported immediately");
    }

    [Fact]
    public void TheEventIdsAreStableAndDistinct_SoAlertRulesWrittenAgainstThemKeepWorking()
    {
        var ids = new[]
        {
            PaymentsLogEvents.AnomalyRecorded, PaymentsLogEvents.AnomalyDuplicate, PaymentsLogEvents.AnomalyPersistenceFailed,
            PaymentsLogEvents.UnmatchedEventIgnored, PaymentsLogEvents.EventNotJudgeable, PaymentsLogEvents.UnresolvedBacklog,
            PaymentsLogEvents.UnresolvedBacklogCleared
        };

        ids.Select(i => i.Id).Should().Equal(7101, 7102, 7103, 7104, 7105, 7110, 7111);
        ids.Select(i => i.Name).Should().OnlyHaveUniqueItems().And.OnlyContain(n => !string.IsNullOrWhiteSpace(n));
    }
}

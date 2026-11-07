using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.UnitTests.Payments;

/// <summary>Records every log call with its level, event id and rendered message (and whether an exception object was attached).</summary>
internal class RecordingLogger : ILogger
{
    public List<(LogLevel Level, EventId Event, string Message, bool HasException)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
            Entries.Add((logLevel, eventId, formatter(state, exception), exception is not null));
    }

    public IReadOnlyList<(LogLevel Level, EventId Event, string Message, bool HasException)> With(EventId id)
    {
        lock (Entries)
            return Entries.Where(e => e.Event.Id == id.Id).ToList();
    }
}

internal sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>
{
}

/// <summary>
/// Collects the payment counters (<see cref="PaymentsTelemetry"/>) emitted by THIS test only. The counters are process-wide, so other
/// tests running in parallel also increment them; each measurement is attributed through an <see cref="AsyncLocal{T}"/> that flows with
/// the test's own async calls (and not into other tests), which makes exact assertions safe. Only valid for code that runs inside the
/// test's async flow (service-level tests), not across a TestServer boundary.
/// </summary>
internal sealed class MeterCapture : IDisposable
{
    private static readonly AsyncLocal<MeterCapture?> Owner = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value, Dictionary<string, string?> Tags)> _measurements = [];

    public MeterCapture()
    {
        Owner.Value = this;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PaymentsTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!ReferenceEquals(Owner.Value, this))
                return;
            var map = new Dictionary<string, string?>();
            foreach (var tag in tags)
                map[tag.Key] = tag.Value?.ToString();
            lock (_measurements)
                _measurements.Add((instrument.Name, value, map));
        });
        _listener.Start();
    }

    /// <summary>Sum of an instrument's measurements whose tags include all the given tag values.</summary>
    public long Sum(string instrument, params (string Key, string Value)[] tags)
    {
        lock (_measurements)
        {
            return _measurements
                .Where(m => m.Instrument == instrument && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && v == t.Value))
                .Sum(m => m.Value);
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        Owner.Value = null;
    }
}

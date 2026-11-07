using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.SignalR;

namespace PulseReserve.AppHost.Live;

/// <summary>
/// The connections this process currently holds, with what each one is allowed to hear. It exists to enforce two limits that SignalR does
/// not: a cap on event subscriptions per connection, and a maximum connection age (which is how an ended session or a revoked
/// administrator stops receiving messages: the connection is closed and the reconnect is authenticated again).
/// </summary>
public sealed class LiveConnectionRegistry
{
    private sealed class Entry(HubCallerContext context, DateTime connectedAtUtc, Guid? userId, bool admin)
    {
        public HubCallerContext Context { get; } = context;
        public DateTime ConnectedAtUtc { get; } = connectedAtUtc;
        public Guid? UserId { get; } = userId;
        public bool Admin { get; } = admin;
        public HashSet<Guid> Events { get; } = [];
    }

    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    public int Count => _connections.Count;

    public void Add(HubCallerContext context, Guid? userId, bool admin, DateTime nowUtc) =>
        _connections[context.ConnectionId] = new Entry(context, nowUtc, userId, admin);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Adds an event subscription unless the connection is unknown or already at <paramref name="max"/>. Re-subscribing is idempotent.</summary>
    public bool TryAddSubscription(string connectionId, Guid eventId, int max)
    {
        if (!_connections.TryGetValue(connectionId, out var entry))
            return false;

        lock (entry.Events)
        {
            if (entry.Events.Contains(eventId))
                return true;
            if (entry.Events.Count >= max)
                return false;
            entry.Events.Add(eventId);
            return true;
        }
    }

    public void RemoveSubscription(string connectionId, Guid eventId)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
        {
            lock (entry.Events)
                entry.Events.Remove(eventId);
        }
    }

    public int SubscriptionCount(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var entry))
            return 0;
        lock (entry.Events)
            return entry.Events.Count;
    }

    /// <summary>Aborts every connection older than <paramref name="maxAge"/>; the client reconnects and is authenticated again. Returns how many.</summary>
    public int AbortOlderThan(TimeSpan maxAge, DateTime nowUtc)
    {
        var aborted = 0;
        foreach (var (id, entry) in _connections)
        {
            if (nowUtc - entry.ConnectedAtUtc < maxAge)
                continue;

            if (_connections.TryRemove(id, out _))
            {
                entry.Context.Abort();
                aborted++;
            }
        }

        return aborted;
    }
}

/// <summary>Enforces <c>Live:MaxConnectionMinutes</c>.</summary>
public sealed class LiveConnectionReaper(
    LiveConnectionRegistry registry, LiveOptions options, TimeProvider clock, ILogger<LiveConnectionReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromSeconds(Math.Clamp(options.MaxConnectionAge.TotalSeconds / 4, 1, 30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(every, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var aborted = registry.AbortOlderThan(options.MaxConnectionAge, clock.GetUtcNow().UtcDateTime);
            if (aborted > 0)
                logger.LogDebug("Closed {Count} live connection(s) that reached the maximum age; the clients reconnect and re-authenticate.", aborted);
        }
    }
}

/// <summary>
/// What an operator can see about the real-time layer without a secret: counts and timestamps, and the same numbers as framework
/// counters on the .NET <see cref="Meter"/> <c>PulseReserve.Live</c> (no package, no exporter). Per-process, reset on restart.
/// </summary>
public sealed class LiveStatus : IDisposable
{
    private readonly Meter _meter = new("PulseReserve.Live", "1.0.0");
    private readonly Counter<long> _signals;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _sent;
    private readonly Counter<long> _resyncs;
    private readonly Counter<long> _reconnects;
    private long _signalsReceived;
    private long _signalsDropped;
    private long _messagesSent;
    private long _resyncCount;
    private long _listenerReconnects;
    private long _lastNotificationTicks;
    private int _listenerConnected;

    public LiveStatus(LiveConnectionRegistry registry)
    {
        _signals = _meter.CreateCounter<long>("pulsereserve.live.signals", description: "Database change signals taken in, by kind.");
        _dropped = _meter.CreateCounter<long>("pulsereserve.live.signals.dropped", description: "Signals dropped because the dispatch queue was full (every client was told to re-read).");
        _sent = _meter.CreateCounter<long>("pulsereserve.live.messages", description: "Messages pushed to clients, by target.");
        _resyncs = _meter.CreateCounter<long>("pulsereserve.live.resyncs", description: "Times every client was told to re-read, by reason.");
        _reconnects = _meter.CreateCounter<long>("pulsereserve.live.listener.reconnects", description: "Times the database change feed had to be re-established.");
        _meter.CreateObservableGauge("pulsereserve.live.connections", () => registry.Count, description: "Currently open client connections in this process.");
    }

    public long SignalsReceived => Interlocked.Read(ref _signalsReceived);
    public long SignalsDropped => Interlocked.Read(ref _signalsDropped);
    public long MessagesSent => Interlocked.Read(ref _messagesSent);
    public long Resyncs => Interlocked.Read(ref _resyncCount);
    public long ListenerReconnects => Interlocked.Read(ref _listenerReconnects);
    public bool ListenerConnected => Volatile.Read(ref _listenerConnected) == 1;

    public DateTime? LastNotificationUtc =>
        Interlocked.Read(ref _lastNotificationTicks) is var ticks && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

    public void SignalReceived(LiveSignalKind kind)
    {
        Interlocked.Increment(ref _signalsReceived);
        _signals.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString()));
    }

    public void NotificationSeen(DateTime utcNow) => Interlocked.Exchange(ref _lastNotificationTicks, utcNow.Ticks);

    public void SignalDropped()
    {
        Interlocked.Increment(ref _signalsDropped);
        _dropped.Add(1);
    }

    public void MessageSent(string target)
    {
        Interlocked.Increment(ref _messagesSent);
        _sent.Add(1, new KeyValuePair<string, object?>("target", target));
    }

    public void Resynced(string reason)
    {
        Interlocked.Increment(ref _resyncCount);
        _resyncs.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public void ListenerUp() => Volatile.Write(ref _listenerConnected, 1);

    /// <summary>The feed was lost (or could not be opened): it will be re-established.</summary>
    public void ListenerDown()
    {
        Volatile.Write(ref _listenerConnected, 0);
        Interlocked.Increment(ref _listenerReconnects);
        _reconnects.Add(1);
    }

    /// <summary>The listener ended because the host is stopping; that is not a reconnect.</summary>
    public void ListenerStopped() => Volatile.Write(ref _listenerConnected, 0);

    public void Dispose() => _meter.Dispose();
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PulseReserve.AppHost.Live;
using PulseReserve.Modules.Reservation.Features.Live;
using PulseReserve.Modules.Reservation.Persistence;
using PulseReserve.UnitTests.Payments;
using Xunit;

namespace PulseReserve.UnitTests.Live;

internal sealed class RecordingSink : ILiveSignalSink
{
    public ConcurrentQueue<LiveSignal> Signals { get; } = new();

    public void Enqueue(LiveSignal signal) => Signals.Enqueue(signal);

    public List<LiveSignal> Snapshot() => [.. Signals];
}

/// <summary>A scripted LISTEN connection: tests push payloads, or break the connection, on demand.</summary>
internal sealed class FakeFeedConnection : IChangeFeedConnection
{
    private readonly Channel<string> _payloads = Channel.CreateUnbounded<string>();

    public string? ListenedChannel { get; private set; }
    public bool Disposed { get; private set; }

    public void Push(string payload) => _payloads.Writer.TryWrite(payload);

    /// <summary>The server went away: the next read throws, like a lost connection.</summary>
    public void Break(Exception? reason = null) => _payloads.Writer.TryComplete(reason ?? new IOException("connection lost"));

    public Task ListenAsync(string channel, CancellationToken cancellationToken)
    {
        ListenedChannel = channel;
        return Task.CompletedTask;
    }

    public async ValueTask<string> ReadAsync(CancellationToken cancellationToken) => await _payloads.Reader.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeFeedFactory(Func<int, FakeFeedConnection> open) : IChangeFeedConnectionFactory
{
    private int _opened;

    public int Attempts => Volatile.Read(ref _opened);

    public ConcurrentQueue<FakeFeedConnection> Connections { get; } = new();

    public Task<IChangeFeedConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _opened);
        var connection = open(attempt); // throws to simulate an unreachable database
        Connections.Enqueue(connection);
        return Task.FromResult<IChangeFeedConnection>(connection);
    }
}

/// <summary>Records every timer's due time and then fires it at once: backoff and startup delays are observable without waiting.</summary>
internal sealed class InstantTimeProvider : TimeProvider
{
    public ConcurrentQueue<TimeSpan> DueTimes { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
            DueTimes.Enqueue(dueTime);
        return base.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero, period);
    }
}

public sealed class ChangeFeedListenerTests
{
    private static readonly Guid Reservation = Guid.Parse("12e50000-0000-4000-8000-000000000001");
    private static readonly Guid Order = Guid.Parse("0dd00000-0000-4000-8000-000000000001");

    private static readonly ChangeFeedTimings Fast = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private Task? _run;

        public RecordingSink Sink { get; } = new();
        public LiveStatus Status { get; } = new(new LiveConnectionRegistry());
        public InstantTimeProvider Clock { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();
        public FakeFeedFactory Factory { get; }
        public PostgresChangeListener Listener { get; }

        public Rig(Func<int, FakeFeedConnection> open, ChangeFeedTimings? timings = null)
        {
            Factory = new FakeFeedFactory(open);
            Listener = new PostgresChangeListener(
                Factory, Sink, Status, Clock, timings ?? Fast, LoggerFactory.Create(b => b.AddProvider(Logs)).CreateLogger<PostgresChangeListener>());
        }

        public Rig Start()
        {
            _run = Listener.RunAsync(_stop.Token);
            return this;
        }

        public async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;
                await Task.Delay(5);
            }

            return condition();
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            if (_run is not null)
                await _run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Status.Dispose();
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task OnConnect_ItListensOnTheAgreedChannel_AndAsksEveryClientToReRead()
    {
        var connection = new FakeFeedConnection();
        await using var rig = new Rig(_ => connection).Start();

        (await rig.UntilAsync(() => rig.Sink.Signals.Count == 1)).Should().BeTrue();

        connection.ListenedChannel.Should().Be("pulsereserve_live");
        rig.Status.ListenerConnected.Should().BeTrue();
        rig.Sink.Snapshot().Should().Equal(LiveSignal.ResyncAll("change-feed-connected"));
    }

    [Fact]
    public async Task ANotification_BecomesSignals_InArrivalOrder()
    {
        var connection = new FakeFeedConnection();
        await using var rig = new Rig(_ => connection).Start();

        connection.Push($$"""{"k":"res","id":"{{Reservation}}"}""");
        connection.Push($$"""{"k":"tkt","o":"{{Order}}"}""");
        connection.Push("""{"k":"wrk","t":"outbox_events"}""");

        (await rig.UntilAsync(() => rig.Sink.Signals.Count == 1 + 1 + 1 + 2)).Should().BeTrue();
        rig.Sink.Snapshot().Skip(1).Should().Equal(
            LiveSignal.ForReservation(Reservation), LiveSignal.ForOrder(Order), LiveSignal.ForAdmin(LiveAreas.Outbox), LiveSignal.ForAdmin(LiveAreas.Overview));
        rig.Status.LastNotificationUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task AnythingElseOnTheChannel_IsIgnored_AndTheListenerKeepsWorking()
    {
        var connection = new FakeFeedConnection();
        await using var rig = new Rig(_ => connection).Start();

        foreach (var junk in new[] { "", "not json", "[]", "{\"k\":\"drop table\"}", "{\"k\":\"res\",\"id\":\"x\"}", new string('x', 9000) })
            connection.Push(junk);
        connection.Push($$"""{"k":"res","id":"{{Reservation}}"}""");

        (await rig.UntilAsync(() => rig.Sink.Signals.Count == 2)).Should().BeTrue();
        rig.Sink.Snapshot()[1].Should().Be(LiveSignal.ForReservation(Reservation));
        rig.Factory.Attempts.Should().Be(1); // it never reconnected because of the junk
    }

    [Fact]
    public async Task ALostConnection_IsReestablishedAfterBackoff_AndEveryClientIsToldToReRead()
    {
        var first = new FakeFeedConnection();
        var second = new FakeFeedConnection();
        await using var rig = new Rig(attempt => attempt == 1 ? first : second).Start();
        (await rig.UntilAsync(() => rig.Status.ListenerConnected)).Should().BeTrue();

        first.Break();

        (await rig.UntilAsync(() => rig.Factory.Attempts == 2 && rig.Sink.Signals.Count == 2)).Should().BeTrue();
        rig.Sink.Snapshot().Should().Equal(LiveSignal.ResyncAll("change-feed-connected"), LiveSignal.ResyncAll("change-feed-connected"));
        first.Disposed.Should().BeTrue();
        rig.Status.ListenerReconnects.Should().Be(1);

        second.Push($$"""{"k":"res","id":"{{Reservation}}"}""");
        (await rig.UntilAsync(() => rig.Sink.Signals.Count == 3)).Should().BeTrue();
    }

    [Fact]
    public async Task WhileTheDatabaseIsUnreachable_ItRetriesWithDoublingBackoffUpToTheCap()
    {
        var connection = new FakeFeedConnection();
        await using var rig = new Rig(attempt => attempt <= 5 ? throw new InvalidOperationException("db down") : connection).Start();

        (await rig.UntilAsync(() => rig.Status.ListenerConnected)).Should().BeTrue();

        rig.Clock.DueTimes.Take(5).Should().Equal(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
        rig.Status.ListenerReconnects.Should().Be(5);
    }

    [Fact]
    public async Task ASuccessfulConnect_ResetsTheBackoff()
    {
        var first = new FakeFeedConnection();
        var second = new FakeFeedConnection();
        await using var rig = new Rig(attempt => attempt switch
        {
            1 or 2 => throw new InvalidOperationException("down"),
            3 => first,
            4 => throw new InvalidOperationException("down again"),
            _ => second
        }).Start();
        (await rig.UntilAsync(() => rig.Status.ListenerConnected && rig.Factory.Attempts == 3)).Should().BeTrue();

        first.Break();

        (await rig.UntilAsync(() => rig.Factory.Attempts == 5 && rig.Status.ListenerConnected)).Should().BeTrue();
        // 1 s, 2 s (two failures), then the connect at attempt 3 resets: the loss afterwards waits 1 s again, then 2 s.
        rig.Clock.DueTimes.Take(4).Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Failures_AreLoggedByTypeOnly_NeverWithTheConnectionDetailsInTheException()
    {
        const string Secret = "Host=db.internal;Username=app;Password=hunter2-SECRET";
        var connection = new FakeFeedConnection();
        await using var rig = new Rig(attempt => attempt == 1 ? throw new InvalidOperationException(Secret) : connection).Start();
        (await rig.UntilAsync(() => rig.Status.ListenerConnected)).Should().BeTrue();

        var logged = string.Join('\n', rig.Logs.Lines);
        logged.Should().Contain("InvalidOperationException").And.NotContain("hunter2").And.NotContain("db.internal").And.NotContain("Password");
    }

    [Fact]
    public async Task OnShutdown_ItStopsPromptly_ReleasesTheConnection_AndReportsItselfDisconnected()
    {
        var connection = new FakeFeedConnection();
        await using var rig = new Rig(_ => connection).Start();
        (await rig.UntilAsync(() => rig.Status.ListenerConnected)).Should().BeTrue();

        await rig.StopAsync();

        connection.Disposed.Should().BeTrue();
        rig.Status.ListenerConnected.Should().BeFalse();
    }

    [Fact]
    public async Task AsAHostedService_ItWaitsAShortRandomTimeBeforeTheFirstDatabaseCall()
    {
        // The delay is what lets a host verify its target database before anything connects (see the integration test safety net).
        var connection = new FakeFeedConnection();
        var clock = new InstantTimeProvider();
        using var status = new LiveStatus(new LiveConnectionRegistry());
        var sink = new RecordingSink();
        var factory = new FakeFeedFactory(_ => connection);
        var listener = new PostgresChangeListener(factory, sink, status, clock, ChangeFeedTimings.Default, NullLogger<PostgresChangeListener>.Instance);

        await listener.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (factory.Attempts == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        await listener.StopAsync(CancellationToken.None);

        factory.Attempts.Should().BeGreaterThan(0);
        clock.DueTimes.TryPeek(out var startupDelay).Should().BeTrue();
        startupDelay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200)).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(2000));
    }
}

public sealed class HoldExpiryTests
{
    private sealed class FakeLiveStore(int due) : IReservationLiveStore
    {
        private int _due = due;

        public List<int> Limits { get; } = [];
        public bool FailNext { get; set; }

        public Task<ReservationLiveSnapshot?> ReadLiveSnapshotAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReservationLiveSnapshot?>(null);

        public Task<int> ExpireDueHeldReservationsAsync(int limit, CancellationToken cancellationToken = default)
        {
            lock (Limits)
                Limits.Add(limit);
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Host=db.internal;Password=hunter2-SECRET");
            }

            var closed = Math.Min(limit, _due);
            _due -= closed;
            return Task.FromResult(closed);
        }
    }

    private static (HoldExpiryWorker Worker, FakeLiveStore Store, CapturingLoggerProvider Logs) Build(int due, int batch = 100, int intervalSeconds = 1)
    {
        var store = new FakeLiveStore(due);
        var services = new ServiceCollection();
        services.AddSingleton<IReservationLiveStore>(store);
        services.AddScoped<HoldExpiryService>();
        var provider = services.BuildServiceProvider();
        var options = LiveOptions.Resolve(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reservation:HoldExpiry:BatchSize"] = batch.ToString(),
            ["Reservation:HoldExpiry:IntervalSeconds"] = intervalSeconds.ToString()
        }).Build());
        var logs = new CapturingLoggerProvider();
        var worker = new HoldExpiryWorker(provider.GetRequiredService<IServiceScopeFactory>(), options, LoggerFactory.Create(b => b.AddProvider(logs)).CreateLogger<HoldExpiryWorker>());
        return (worker, store, logs);
    }

    [Theory]
    [InlineData(0, 100, 1)]
    [InlineData(40, 100, 1)]
    [InlineData(250, 100, 3)]
    [InlineData(100, 100, 2)] // an exactly full batch means "maybe more": one more look, which finds none
    [InlineData(3, 1, 4)]
    public async Task APass_ClosesDueHoldsInBatchesUntilABatchComesBackShort(int due, int batch, int expectedCalls)
    {
        var (worker, store, _) = Build(due, batch);

        var closed = await worker.DrainAsync(CancellationToken.None);

        closed.Should().Be(due);
        store.Limits.Should().HaveCount(expectedCalls).And.OnlyContain(limit => limit == batch);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task TheService_RefusesABatchSizeOutsideItsBounds(int batch)
    {
        var service = new HoldExpiryService(new FakeLiveStore(0));

        var act = () => service.ExpireDueAsync(batch);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AFailingPass_IsLoggedByTypeOnly_AndTheLoopCarriesOn()
    {
        var (worker, store, logs) = Build(due: 5, batch: 100, intervalSeconds: 1);
        store.FailNext = true;

        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (store.Limits)
            {
                if (store.Limits.Count >= 2)
                    break;
            }

            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        store.Limits.Count.Should().BeGreaterThanOrEqualTo(2, "the pass after the failure must still run");
        var logged = string.Join('\n', logs.Lines);
        logged.Should().Contain("InvalidOperationException").And.NotContain("hunter2").And.NotContain("db.internal");
    }
}

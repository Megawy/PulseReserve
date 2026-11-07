using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using PulseReserve.Modules.Payments.Features.Live;
using PulseReserve.Modules.Reservation.Features.Live;

namespace PulseReserve.AppHost.Live;

/// <summary>Where change signals go. Enqueuing never blocks, never throws, and never touches the database.</summary>
public interface ILiveSignalSink
{
    void Enqueue(LiveSignal signal);
}

/// <summary>Reads the CURRENT state of the entities a signal names. The dispatcher trusts nothing but this.</summary>
public interface ILiveStateReader
{
    Task<ReservationLiveSnapshot?> ReadReservationAsync(Guid reservationId, CancellationToken cancellationToken);

    Task<OrderLiveSnapshot?> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <summary>The production reader: the two modules' own read surfaces, composed here because modules never reference each other.</summary>
public sealed class ModuleLiveStateReader(ReservationLiveReader reservations, PaymentsLiveReader payments) : ILiveStateReader
{
    public Task<ReservationLiveSnapshot?> ReadReservationAsync(Guid reservationId, CancellationToken cancellationToken) =>
        reservations.ReadAsync(reservationId, cancellationToken);

    public Task<OrderLiveSnapshot?> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        payments.ReadOrderAsync(orderId, cancellationToken);
}

/// <summary>
/// Turns signals into messages for exactly the clients allowed to hear them.
/// <list type="bullet">
/// <item><b>Authoritative content.</b> A signal only names an entity. Its current state is read from the database (database clock
/// included) at dispatch time, so a reordered, duplicated or late signal can never push an older state than the database has.</item>
/// <item><b>Routing.</b> Seat availability goes to <c>event:{id}</c> (public data), the reservation and the order to
/// <c>user:{owner}</c> only, administrator areas to <c>admins</c> only. The owner comes from the database, never from the signal.</item>
/// <item><b>Coalescing.</b> Signals are collected for <see cref="LiveOptions.Coalesce"/> and de-duplicated per entity, so a burst
/// (a seat map being fought over, a webhook storm) costs one read and one message per entity.</item>
/// <item><b>Overflow.</b> The queue is bounded. If it ever overflows the oldest signals are dropped and EVERY client is told to
/// re-read, so loss is visible and recoverable, never silent.</item>
/// <item><b>Delivery is best effort and in-process.</b> There is no replay: a client that was disconnected re-reads when it reconnects.
/// This is not a durable queue and is not described as one.</item>
/// </list>
/// </summary>
public sealed class LiveDispatcher : BackgroundService, ILiveSignalSink
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHubContext<LiveHub> _hub;
    private readonly LiveOptions _options;
    private readonly LiveEpoch _epoch;
    private readonly LiveStatus _status;
    private readonly TimeProvider _clock;
    private readonly ILogger<LiveDispatcher> _logger;
    private readonly Channel<LiveSignal> _queue;
    private int _overflowed;

    public LiveDispatcher(
        IServiceScopeFactory scopes, IHubContext<LiveHub> hub, LiveOptions options, LiveEpoch epoch, LiveStatus status, TimeProvider clock,
        ILogger<LiveDispatcher> logger)
    {
        _scopes = scopes;
        _hub = hub;
        _options = options;
        _epoch = epoch;
        _status = status;
        _clock = clock;
        _logger = logger;
        _queue = Channel.CreateBounded<LiveSignal>(
            new BoundedChannelOptions(options.ChannelCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ =>
            {
                Interlocked.Exchange(ref _overflowed, 1);
                _status.SignalDropped();
            });
    }

    public void Enqueue(LiveSignal signal)
    {
        _status.SignalReceived(signal.Kind);
        _queue.Writer.TryWrite(signal);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                // Let a burst accumulate so it is read and sent once.
                await Task.Delay(_options.Coalesce, _clock, stoppingToken).ConfigureAwait(false);

                var batch = new List<LiveSignal>();
                while (_queue.Reader.TryRead(out var signal))
                    batch.Add(signal);

                try
                {
                    await DispatchAsync(batch, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Nothing in a batch may stop the loop; clients recover by re-reading.
                    _logger.LogWarning("A live update batch failed ({ErrorType}); clients will re-read when they next refresh.", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Processes one batch. Public so that tests can drive it without timing.</summary>
    public async Task DispatchAsync(IReadOnlyCollection<LiveSignal> batch, CancellationToken cancellationToken)
    {
        var reservations = new HashSet<Guid>();
        var orders = new HashSet<Guid>();
        var areas = new HashSet<string>(StringComparer.Ordinal);
        string? resync = null;

        foreach (var signal in batch)
        {
            switch (signal.Kind)
            {
                case LiveSignalKind.Reservation when signal.Id != Guid.Empty:
                    reservations.Add(signal.Id);
                    break;
                case LiveSignalKind.Order when signal.Id != Guid.Empty:
                    orders.Add(signal.Id);
                    break;
                case LiveSignalKind.Admin when signal.Area is { Length: > 0 } area && LiveAreas.All.Contains(area):
                    areas.Add(area);
                    break;
                case LiveSignalKind.Resync:
                    resync = signal.Reason is { Length: > 0 } reason ? reason : "resync";
                    break;
            }
        }

        if (Interlocked.Exchange(ref _overflowed, 0) == 1)
            resync ??= "queue-overflow";

        if (resync is not null)
        {
            await SendAsync(_hub.Clients.All, LiveTargets.Resync, new ResyncMessage(resync, _epoch.Value, _epoch.Next()), cancellationToken);
            _status.Resynced(resync);
        }

        if (reservations.Count + orders.Count > 0)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetRequiredService<ILiveStateReader>();

            foreach (var id in reservations)
                await Guarded(() => PushReservationAsync(reader, id, cancellationToken), cancellationToken);
            foreach (var id in orders)
                await Guarded(() => PushOrderAsync(reader, id, cancellationToken), cancellationToken);
        }

        foreach (var area in areas)
            await SendAsync(_hub.Clients.Group(LiveGroups.Admins), LiveTargets.Admin, new AdminMessage(area, _epoch.Value, _epoch.Next()), cancellationToken);
    }

    private async Task PushReservationAsync(ILiveStateReader reader, Guid reservationId, CancellationToken cancellationToken)
    {
        var snapshot = await reader.ReadReservationAsync(reservationId, cancellationToken);
        if (snapshot is null)
            return;

        // Public: which seats are now Available / Held / Sold. Their EFFECTIVE status, read just now.
        if (snapshot.Seats.Count > 0)
        {
            var seats = snapshot.Seats.Select(s => new SeatDelta(s.SeatId, s.Status)).ToList();
            await SendAsync(
                _hub.Clients.Group(LiveGroups.Event(snapshot.EventId)), LiveTargets.Availability,
                new AvailabilityMessage(snapshot.EventId, seats, _epoch.Value, _epoch.Next()), cancellationToken);
        }

        // Private: the owner's reservation, with the server's status and expiry.
        await SendAsync(
            _hub.Clients.Group(LiveGroups.User(snapshot.CustomerId)), LiveTargets.Reservation,
            new ReservationMessage(snapshot.ReservationId, snapshot.EventId, snapshot.Status, DateTime.SpecifyKind(snapshot.ExpiresAtUtc, DateTimeKind.Utc), _epoch.Value, _epoch.Next()),
            cancellationToken);
    }

    private async Task PushOrderAsync(ILiveStateReader reader, Guid orderId, CancellationToken cancellationToken)
    {
        var snapshot = await reader.ReadOrderAsync(orderId, cancellationToken);
        if (snapshot is null)
            return;

        await SendAsync(
            _hub.Clients.Group(LiveGroups.User(snapshot.CustomerId)), LiveTargets.Order,
            new OrderMessage(snapshot.OrderId, snapshot.ReservationId, snapshot.Status, snapshot.PaymentStatus, snapshot.TicketCount, _epoch.Value, _epoch.Next()),
            cancellationToken);
    }

    private async Task SendAsync(IClientProxy proxy, string target, object message, CancellationToken cancellationToken)
    {
        await proxy.SendAsync(target, message, cancellationToken);
        _status.MessageSent(target);
    }

    private async Task Guarded(Func<Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // One unreadable entity must not stop the rest of the batch; the client re-reads on its next refresh.
            _logger.LogWarning("A live update could not be prepared ({ErrorType}); it is skipped.", ex.GetType().Name);
        }
    }
}

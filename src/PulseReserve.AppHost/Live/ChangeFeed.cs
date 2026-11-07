using System.Threading.Channels;
using Npgsql;

namespace PulseReserve.AppHost.Live;

/// <summary>The database channel the triggers notify (see the <c>LiveChangeNotifications</c> migrations).</summary>
public static class ChangeFeedChannel
{
    public const string Name = "pulsereserve_live";
}

/// <summary>One dedicated, long-lived database connection that is LISTENing. Disposing it ends the subscription.</summary>
public interface IChangeFeedConnection : IAsyncDisposable
{
    /// <summary>Starts listening. Notifications for commits that happen after this call returns are delivered by <see cref="ReadAsync"/>.</summary>
    Task ListenAsync(string channel, CancellationToken cancellationToken);

    /// <summary>The next notification payload. Throws when the connection is lost: the caller must reconnect and assume it missed some.</summary>
    ValueTask<string> ReadAsync(CancellationToken cancellationToken);
}

public interface IChangeFeedConnectionFactory
{
    Task<IChangeFeedConnection> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// PostgreSQL <c>LISTEN</c>. PostgreSQL delivers a notification only when the transaction that sent it COMMITS, so nothing that is
/// rolled back is ever announced. The connection is not pooled (a pooled connection is reset and returned, which ends LISTEN), and it
/// sends a keepalive so that a connection that died silently (a failed-over server, a dropped network) is noticed.
/// </summary>
public sealed class NpgsqlChangeFeedConnectionFactory(string connectionString) : IChangeFeedConnectionFactory
{
    public async Task<IChangeFeedConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            KeepAlive = 30,
            TcpKeepAlive = true,
            ApplicationName = "pulsereserve-live"
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return new NpgsqlChangeFeedConnection(connection);
    }
}

internal sealed class NpgsqlChangeFeedConnection : IChangeFeedConnection
{
    private readonly NpgsqlConnection _connection;
    private readonly Channel<string> _payloads = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;

    public NpgsqlChangeFeedConnection(NpgsqlConnection connection)
    {
        _connection = connection;
        _connection.Notification += (_, e) => _payloads.Writer.TryWrite(e.Payload);
    }

    public async Task ListenAsync(string channel, CancellationToken cancellationToken)
    {
        // The channel name is one of our own constants, never user input; it is validated anyway because LISTEN cannot be parameterised.
        if (channel.Length is 0 or > 63 || !channel.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("The change feed channel must be a simple identifier.", nameof(channel));

        await using (var cmd = new NpgsqlCommand($"LISTEN {channel}", _connection))
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        _pump = PumpAsync();
    }

    // WaitAsync raises Notification for each notification received and throws when the connection breaks.
    private async Task PumpAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                await _connection.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            _payloads.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _payloads.Writer.TryComplete(ex);
        }
    }

    public async ValueTask<string> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _payloads.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new IOException("The change feed connection was closed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Closing the connection below ends a pump that did not stop in time.
            }
        }

        await _connection.DisposeAsync();
        _stop.Dispose();
    }
}

/// <summary>Timing of the listener; tests use tiny values.</summary>
public sealed record ChangeFeedTimings(TimeSpan StartupDelayMin, TimeSpan StartupDelayMax, TimeSpan BackoffMin, TimeSpan BackoffMax)
{
    public static ChangeFeedTimings Default { get; } =
        new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
}

/// <summary>
/// Keeps one LISTEN connection alive and feeds the dispatcher. The database is the source of truth and the feed is only a hint, so its
/// failure modes are all "tell everyone to re-read":
/// <list type="bullet">
/// <item>Whenever the feed (re)connects, a resync is signalled: anything committed while it was not listening was not announced.</item>
/// <item>A payload it cannot understand is ignored; nothing that arrives on the channel can stop the listener.</item>
/// <item>A lost connection is retried with exponential backoff (capped); failures are logged by type only, never with connection details.</item>
/// <item>Starting is delayed (200-2000 ms by default) so that a host which verifies its database target at startup finishes doing so first.</item>
/// </list>
/// </summary>
public sealed class PostgresChangeListener(
    IChangeFeedConnectionFactory factory, ILiveSignalSink sink, LiveStatus status, TimeProvider clock, ChangeFeedTimings timings,
    ILogger<PostgresChangeListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var delay = timings.StartupDelayMin + (timings.StartupDelayMax - timings.StartupDelayMin) * Random.Shared.NextDouble();
            await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(false);
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>The reconnect loop. Public so that tests can drive it with a fake connection.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var backoff = timings.BackoffMin;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await factory.OpenAsync(stoppingToken).ConfigureAwait(false);
                await connection.ListenAsync(ChangeFeedChannel.Name, stoppingToken).ConfigureAwait(false);

                status.ListenerUp();
                backoff = timings.BackoffMin;
                logger.LogInformation("The live change feed is listening.");
                // Whatever committed before this point was not announced to us: clients must re-read once.
                sink.Enqueue(LiveSignal.ResyncAll("change-feed-connected"));

                while (!stoppingToken.IsCancellationRequested)
                {
                    var payload = await connection.ReadAsync(stoppingToken).ConfigureAwait(false);
                    status.NotificationSeen(clock.GetUtcNow().UtcDateTime);
                    foreach (var signal in LiveSignalParser.Parse(payload))
                        sink.Enqueue(signal);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                status.ListenerDown();
                logger.LogWarning("The live change feed was lost ({ErrorType}); retrying in {Seconds:F0} s. Clients re-read when it is back.", ex.GetType().Name, backoff.TotalSeconds);
                try
                {
                    await Task.Delay(backoff, clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, timings.BackoffMax.Ticks));
            }
        }

        status.ListenerStopped();
    }
}

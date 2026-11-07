using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PulseReserve.AppHost.Live;
using PulseReserve.AppHost.Security;
using PulseReserve.Modules.Payments.Features.Live;
using PulseReserve.Modules.Reservation.Features.Live;

namespace PulseReserve.UnitTests.Live;

/// <summary>Answers the dispatcher's reads from memory, standing in for the database.</summary>
internal sealed class FakeLiveStateReader : ILiveStateReader
{
    public ConcurrentDictionary<Guid, ReservationLiveSnapshot> Reservations { get; } = new();
    public ConcurrentDictionary<Guid, OrderLiveSnapshot> Orders { get; } = new();

    /// <summary>When set, every read fails (as an unreachable database would).</summary>
    public bool Fail { get; set; }

    public Task<ReservationLiveSnapshot?> ReadReservationAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        if (Fail)
            throw new InvalidOperationException("database unreachable (simulated)");
        return Task.FromResult(Reservations.TryGetValue(reservationId, out var snapshot) ? snapshot : null);
    }

    public Task<OrderLiveSnapshot?> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (Fail)
            throw new InvalidOperationException("database unreachable (simulated)");
        return Task.FromResult(Orders.TryGetValue(orderId, out var snapshot) ? snapshot : null);
    }
}

/// <summary>Only the clock reading is controlled; timers stay real so the dispatcher's short coalescing delay still elapses.</summary>
internal sealed class SettableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The REAL live layer - hub, groups, dispatcher loop, connection registry, origin guard and connect rate limit - in an in-memory
/// server, reached by real SignalR client connections (long polling, as the in-memory server has no sockets). Identity comes from the
/// same header scheme the other test hosts use. The database is replaced by <see cref="Reader"/>; nothing here proves SQL or NOTIFY.
/// </summary>
internal sealed class LiveTestHost : IAsyncDisposable
{
    public WebApplication App { get; private set; } = null!;
    public TestServer Server { get; private set; } = null!;
    public FakeLiveStateReader Reader { get; } = new();
    public SettableTimeProvider Clock { get; } = new();
    public LiveOptions Options { get; private set; } = null!;

    public LiveDispatcher Dispatcher => App.Services.GetRequiredService<LiveDispatcher>();
    public LiveStatus Status => App.Services.GetRequiredService<LiveStatus>();
    public LiveConnectionRegistry Registry => App.Services.GetRequiredService<LiveConnectionRegistry>();
    public LiveEpoch Epoch => App.Services.GetRequiredService<LiveEpoch>();
    public ILiveSignalSink Sink => App.Services.GetRequiredService<ILiveSignalSink>();

    public static async Task<LiveTestHost> StartAsync(Dictionary<string, string?>? settings = null)
    {
        var host = new LiveTestHost();
        await host.BuildAsync(settings ?? []);
        return host;
    }

    private async Task BuildAsync(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // No database at all: the change feed and the hold-expiry worker stay off, the dispatcher is fed by the test.
        var defaults = new Dictionary<string, string?>
        {
            ["Live:ChangeFeed:Enabled"] = "false",
            ["Reservation:HoldExpiry:Enabled"] = "false",
            ["Live:CoalesceMilliseconds"] = "10"
        };
        foreach (var (key, value) in settings)
            defaults[key] = value;
        builder.Configuration.AddInMemoryCollection(defaults);

        Options = builder.AddPulseReserveLive("Host=unused;Database=unused");
        var services = builder.Services;
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<ILiveStateReader>(_ => Reader);
        services.AddAuthentication(PulseReserve.UnitTests.Payments.HeaderAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PulseReserve.UnitTests.Payments.HeaderAuthHandler>(PulseReserve.UnitTests.Payments.HeaderAuthHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Admin, p => p.RequireAuthenticatedUser().RequireRole(AuthPolicies.AdminRole));
        services.AddRateLimiter(limiter => limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests);

        App = builder.Build();
        App.UseAuthentication();
        App.UseRateLimiter();
        App.UseAuthorization();
        App.UsePulseReserveLive(Options);
        App.MapPulseReserveLive(Options);
        await App.StartAsync();
        Server = (TestServer)App.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
    }

    /// <summary>A client connection with the given identity. Not started: call <see cref="LiveClient.StartAsync"/>.</summary>
    public LiveClient Connect(Guid? user = null, bool admin = false, string? origin = null, Action<HttpConnectionOptions>? configure = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, LivePolicies.HubPath), options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (user is { } id)
                    options.Headers["X-Test-User"] = id.ToString();
                if (admin)
                    options.Headers["X-Test-Admin"] = "1";
                if (origin is not null)
                    options.Headers["Origin"] = origin;
                configure?.Invoke(options);
            })
            .Build();
        return new LiveClient(connection);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>A hub connection that records every message it receives, in arrival order.</summary>
internal sealed class LiveClient(HubConnection connection) : IAsyncDisposable
{
    public static readonly string[] Targets = [LiveTargets.Availability, LiveTargets.Reservation, LiveTargets.Order, LiveTargets.Admin, LiveTargets.Resync];

    private bool _registered;

    public HubConnection Connection { get; } = connection;
    public ConcurrentQueue<(string Target, JsonElement Body)> Received { get; } = new();

    /// <summary>What the server said about this connection right after it was established.</summary>
    public LiveHello Hello { get; private set; } = null!;

    /// <summary>
    /// Connects and waits until the hub has finished its connect handler (a hub call is only processed after it), so that group
    /// membership is in place before the test dispatches anything.
    /// </summary>
    public async Task<LiveClient> StartAsync()
    {
        if (!_registered)
        {
            foreach (var target in Targets)
            {
                var name = target;
                Connection.On<JsonElement>(name, body => Received.Enqueue((name, body.Clone())));
            }

            _registered = true;
        }

        await Connection.StartAsync();
        Hello = await Connection.InvokeAsync<LiveHello>("Hello");
        return this;
    }

    public Task SubscribeAsync(Guid eventId) => Connection.InvokeAsync("SubscribeEvent", eventId);

    public IReadOnlyList<(string Target, JsonElement Body)> Messages(string? target = null) =>
        Received.Where(m => target is null || m.Target == target).ToList();

    /// <summary>Waits for a message that matches; fails loudly on timeout instead of passing silently.</summary>
    public async Task<(string Target, JsonElement Body)> WaitForAsync(string target, Func<JsonElement, bool>? predicate = null, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var message in Received)
            {
                if (message.Target == target && (predicate is null || predicate(message.Body)))
                    return message;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"No '{target}' message arrived within {timeoutMs} ms. Received: {string.Join(", ", Received.Select(m => m.Target))}");
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

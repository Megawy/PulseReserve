using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PulseReserve.AppHost.Api;
using PulseReserve.IntegrationTests.Catalog;
using Microsoft.Extensions.DependencyInjection;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Reservation.Features.Holds;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

/// <summary>Carries the session cookie between requests of one signed-in user, for HTTP calls and for the hub connection alike.</summary>
internal sealed class CookieJarHandler(System.Net.CookieContainer cookies) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var header = cookies.GetCookieHeader(request.RequestUri!);
        if (header.Length > 0)
            request.Headers.TryAddWithoutValidation("Cookie", header);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var set))
        {
            foreach (var cookie in set)
                cookies.SetCookies(request.RequestUri!, cookie);
        }

        return response;
    }
}

internal sealed class LiveUser(HttpClient client, System.Net.CookieContainer cookies, Guid id)
{
    public HttpClient Client { get; } = client;
    public System.Net.CookieContainer Cookies { get; } = cookies;
    public Guid Id { get; } = id;
}

/// <summary>A hub connection that records every message it receives.</summary>
internal sealed class LiveRecorder : IAsyncDisposable
{
    private static readonly string[] Targets = ["availability", "reservation", "order", "admin", "resync"];
    private readonly ConcurrentQueue<(string Target, JsonElement Body)> _received = new();

    private LiveRecorder(HubConnection connection)
    {
        Connection = connection;
        foreach (var target in Targets)
        {
            var name = target;
            connection.On<JsonElement>(name, body => _received.Enqueue((name, body.Clone())));
        }
    }

    public HubConnection Connection { get; }

    public static async Task<LiveRecorder> StartAsync(HubConnection connection)
    {
        var recorder = new LiveRecorder(connection);
        await connection.StartAsync();
        await connection.InvokeAsync<JsonElement>("Hello"); // processed only after the hub's connect handler has finished
        return recorder;
    }

    public IReadOnlyList<(string Target, JsonElement Body)> Messages(string? target = null) =>
        _received.Where(m => target is null || m.Target == target).ToList();

    public async Task<JsonElement> WaitForAsync(string target, Func<JsonElement, bool>? predicate = null, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var message in _received)
            {
                if (message.Target == target && (predicate is null || predicate(message.Body)))
                    return message.Body;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"No '{target}' message arrived within {timeoutMs} ms. Received: {string.Join(", ", _received.Select(m => m.Target))}");
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

/// <summary>
/// The real-time layer against REAL PostgreSQL: the NOTIFY triggers, LISTEN, the authoritative snapshot SQL, the hold-expiry
/// statement and the whole path to a SignalR client, through the real pipeline (cookie sign-in included). These are the tests that
/// prove what the unit tests cannot: that a message exists only after COMMIT, and that SQL and triggers behave on the real engine.
/// STATUS: COMPILED, NOT EXECUTED. They need Docker (Testcontainers), which was unavailable when they were written; the
/// authorization and routing rules themselves are exercised without a database in the unit suite (<c>LiveHubTests</c>).
/// </summary>
public class LivePostgreSqlTests : IClassFixture<PostgreSqlDatabaseFixture>, IAsyncLifetime
{
    private readonly PostgreSqlDatabaseFixture _database;
    private readonly List<IAsyncDisposable> _recorders = [];
    private readonly List<IDisposable> _disposables = [];
    private CatalogApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public LivePostgreSqlTests(PostgreSqlDatabaseFixture database) => _database = database;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var recorder in _recorders)
            await recorder.DisposeAsync();
        foreach (var disposable in _disposables)
            disposable.Dispose();
        _admin?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>A host with the change feed on (the factory turns it off by default) and, optionally, the hold-expiry worker.</summary>
    private void StartHost(bool holdExpiry = false)
    {
        _factory = new CatalogApiFactory(_database, new Dictionary<string, string?>
        {
            ["Live:ChangeFeed:Enabled"] = "true",
            ["Reservation:HoldExpiry:Enabled"] = holdExpiry ? "true" : "false",
            ["Reservation:HoldExpiry:IntervalSeconds"] = "1",
            ["Live:CoalesceMilliseconds"] = "10"
        });
        _admin = AdminTestSupport.RegisterAdmin(_factory).Client;
    }

    private async Task<LiveUser> NewUserAsync(bool admin = false)
    {
        var cookies = new System.Net.CookieContainer();
        var client = _factory.CreateDefaultClient(new CookieJarHandler(cookies));
        _disposables.Add(client);
        await AuthTestSupport.RefreshCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/identity/register", new { email = $"live-{Guid.NewGuid():N}@example.com", password = AuthTestSupport.Password, displayName = "Live User" });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        AuthTestSupport.ApplyToken(client, session);

        var id = session.GetProperty("user").GetProperty("id").GetGuid();
        if (admin)
            AdminTestSupport.Grant(_factory, id);
        return new LiveUser(client, cookies, id);
    }

    private async Task<LiveRecorder> ConnectAsync(LiveUser? user = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/live"), options =>
            {
                options.Transports = HttpTransportType.LongPolling; // the in-memory server has no sockets
                options.HttpMessageHandlerFactory = _ => user is null
                    ? _factory.Server.CreateHandler()
                    : new CookieJarHandler(user.Cookies) { InnerHandler = _factory.Server.CreateHandler() };
            })
            .Build();
        var recorder = await LiveRecorder.StartAsync(connection);
        _recorders.Add(recorder);
        return recorder;
    }

    private static async Task<Guid> HoldAsync(LiveUser user, Guid eventId, Guid seatId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/reservations") { Content = JsonContent.Create(new { eventId, seatIds = new[] { seatId } }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await user.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReservationResponse>())!.ReservationId;
    }

    private static async Task<Guid> CheckoutAsync(LiveUser user, Guid reservationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new { reservationId }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await user.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!.OrderId;
    }

    private static async Task PaySimulatedAsync(LiveUser user, Guid orderId)
    {
        using var start = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId}/payments") { Content = JsonContent.Create(new { provider = "Simulator" }) };
        start.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var started = await user.Client.SendAsync(start);
        (new[] { HttpStatusCode.OK, HttpStatusCode.Created }).Should().Contain(started.StatusCode, await started.Content.ReadAsStringAsync());
        var order = (await started.Content.ReadFromJsonAsync<OrderResponse>())!;
        var done = await user.Client.PostAsJsonAsync("/api/dev/payment-simulator/complete", new
        {
            orderId,
            attemptId = order.Payment!.AttemptId,
            simulatorToken = order.Payment.SimulatorToken,
            outcome = "succeed"
        });
        done.StatusCode.Should().Be(HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
    }

    private async Task WaitForListenerAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await _admin.GetFromJsonAsync<JsonElement>("/api/admin/live/status");
            if (status.GetProperty("listenerConnected").GetBoolean())
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException("The change feed did not connect within 30 s.");
    }

    private async Task WaitForStatusAsync(Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition(await _admin.GetFromJsonAsync<JsonElement>("/api/admin/live/status")))
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException("The live status did not reach the expected state within 30 s.");
    }

    private async Task<string?> StringAsync(string sql)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecAsync(string sql)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    // ───────────── the change feed ─────────────

    [Fact]
    public async Task TheListenerConnectsAfterStartup_AndAnnouncesAResyncToEveryone()
    {
        // Whether a given client is already connected when the feed comes up depends on timing, so the announcement is read from the
        // server's own counters here; delivery of a resync to a connected client is covered by the outage test below.
        StartHost();

        await WaitForListenerAsync();
        await WaitForStatusAsync(status => status.GetProperty("resyncs").GetInt64() >= 1);
    }

    [Fact]
    public async Task ALostListenerConnection_IsReestablished_AndClientsAreToldToReRead()
    {
        StartHost();
        await WaitForListenerAsync();
        // The resync that announces the first connect has been dispatched once the counter says so; a client that connects after
        // that moment can only receive the one that follows the outage.
        await WaitForStatusAsync(status => status.GetProperty("resyncs").GetInt64() >= 1);
        await using var visitor = await ConnectAsync();

        // The database side drops the listener's session (as a failover or a network cut would).
        await ExecAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'pulsereserve-live' AND pid <> pg_backend_pid()");

        await WaitForStatusAsync(status =>
            status.GetProperty("listenerReconnects").GetInt64() >= 1
            && status.GetProperty("listenerConnected").GetBoolean()
            && status.GetProperty("resyncs").GetInt64() >= 2);
        await visitor.WaitForAsync("resync", body => body.GetProperty("reason").GetString() == "change-feed-connected");
    }

    // ───────────── a committed change reaches exactly the people allowed to hear it ─────────────

    [Fact]
    public async Task ACommittedHold_IsAnnouncedToSubscribersAndItsOwner_AndNotToAnyoneElseSPrivateChannel()
    {
        StartHost();
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 3);
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        await WaitForListenerAsync();

        await using var visitor = await ConnectAsync();
        await using var aliceHub = await ConnectAsync(alice);
        await using var bobHub = await ConnectAsync(bob);
        await visitor.Connection.InvokeAsync("SubscribeEvent", sale.EventId);

        var reservationId = await HoldAsync(alice, sale.EventId, sale.SeatIds[0]);

        var availability = await visitor.WaitForAsync("availability", body => body.GetProperty("seats").EnumerateArray()
            .Any(s => s.GetProperty("seatId").GetGuid() == sale.SeatIds[0] && s.GetProperty("status").GetString() == "Held"));
        availability.GetProperty("eventId").GetGuid().Should().Be(sale.EventId);
        availability.GetRawText().Should().NotContain(alice.Id.ToString(), "availability never says who holds a seat");

        var owned = await aliceHub.WaitForAsync("reservation", body => body.GetProperty("reservationId").GetGuid() == reservationId);
        owned.GetProperty("status").GetString().Should().Be("Held");

        // Bob does something of his own; once his message arrives, anything of Alice's that was going to reach him already would have.
        var bobsReservation = await HoldAsync(bob, sale.EventId, sale.SeatIds[1]);
        await bobHub.WaitForAsync("reservation", body => body.GetProperty("reservationId").GetGuid() == bobsReservation);
        bobHub.Messages("reservation").Should().NotContain(m => m.Body.GetProperty("reservationId").GetGuid() == reservationId);
        visitor.Messages("reservation").Should().BeEmpty();
    }

    [Fact]
    public async Task ACheckout_IsAnnouncedToTheOwnerAndTheAdministrators_AsHintsWithoutAmounts()
    {
        StartHost();
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 2, price: 45.5m);
        var alice = await NewUserAsync();
        var carol = await NewUserAsync(admin: true);
        await WaitForListenerAsync();

        await using var aliceHub = await ConnectAsync(alice);
        await using var adminHub = await ConnectAsync(carol);
        adminHub.Connection.State.Should().Be(HubConnectionState.Connected);
        var reservationId = await HoldAsync(alice, sale.EventId, sale.SeatIds[0]);
        var orderId = await CheckoutAsync(alice, reservationId);

        var reservation = await aliceHub.WaitForAsync("reservation", body => body.GetProperty("reservationId").GetGuid() == reservationId && body.GetProperty("status").GetString() == "Checkout");
        reservation.GetProperty("eventId").GetGuid().Should().Be(sale.EventId);

        var order = await aliceHub.WaitForAsync("order", body => body.GetProperty("orderId").GetGuid() == orderId);
        order.GetProperty("status").GetString().Should().Be("PendingPayment");
        order.GetRawText().Should().NotContain("45.5").And.NotContainEquivalentOf("amount").And.NotContainEquivalentOf("provider");

        await adminHub.WaitForAsync("admin", body => body.GetProperty("area").GetString() == "orders");
        await adminHub.WaitForAsync("admin", body => body.GetProperty("area").GetString() == "overview");
        adminHub.Messages("order").Should().BeEmpty("an administrator hears about other people's orders only as area hints");
    }

    [Fact]
    public async Task AChangeThatIsRolledBack_IsNeverAnnounced()
    {
        StartHost();
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var alice = await NewUserAsync();
        await WaitForListenerAsync();
        await using var aliceHub = await ConnectAsync(alice);
        var reservationId = await HoldAsync(alice, sale.EventId, sale.SeatIds[0]);
        await aliceHub.WaitForAsync("reservation", body => body.GetProperty("reservationId").GetGuid() == reservationId && body.GetProperty("status").GetString() == "Held");

        await using (var connection = new NpgsqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var update = new NpgsqlCommand("UPDATE reservation.reservations SET status = 'Expired', closed_at_utc = now() WHERE id = @id AND status = 'Held'", connection, transaction);
            update.Parameters.AddWithValue("id", reservationId);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
            await Task.Delay(500); // even while the transaction is open nothing may be announced
            await transaction.RollbackAsync();
        }

        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{reservationId}'")).Should().Be("Held");

        // The marker: a real, committed release. When it arrives, an announcement of the rolled-back change would have arrived first.
        (await alice.Client.PostAsync($"/api/reservations/{reservationId}/release", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await aliceHub.WaitForAsync("reservation", body => body.GetProperty("reservationId").GetGuid() == reservationId && body.GetProperty("status").GetString() == "Released");

        aliceHub.Messages("reservation").Select(m => m.Body.GetProperty("status").GetString()).Should().NotContain("Expired");
    }

    [Fact]
    public async Task TheWorkQueues_AnnounceEveryStateChange_NotOnlyTheOnesThatNeedAPerson()
    {
        // Found in a real browser: the administrator overview shows a count per state, and it went stale while the worker moved outbox
        // events from queued to done, because only NeedsReview was announced. The announcement is read here from a plain LISTEN
        // connection of the test (not the application's), so that nothing but the trigger is being tested.
        StartHost();
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var alice = await NewUserAsync();
        var reservationId = await HoldAsync(alice, sale.EventId, sale.SeatIds[0]);
        var orderId = await CheckoutAsync(alice, reservationId);
        await PaySimulatedAsync(alice, orderId); // inserts the paid order's outbox events (the announcements of that insert are already gone)

        var announced = new ConcurrentQueue<string>();
        await using var listener = new NpgsqlConnection(_database.ConnectionString);
        await listener.OpenAsync();
        listener.Notification += (_, e) => announced.Enqueue(e.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN pulsereserve_live", listener))
            await listen.ExecuteNonQueryAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PaymentsWorkflow>().RunOnceAsync(); // delivers the events: Pending -> Processing -> Done

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !announced.Any(p => p.Contains("outbox_events")))
            await listener.WaitAsync(TimeSpan.FromMilliseconds(200));

        announced.Select(p => JsonDocument.Parse(p).RootElement)
            .Should().Contain(e => e.GetProperty("k").GetString() == "wrk" && e.GetProperty("t").GetString() == "outbox_events");
        (await StringAsync("SELECT count(*) FROM payments.outbox_events WHERE state = 'Done'")).Should().NotBe("0");
    }

    // ───────────── the hold-expiry worker ─────────────

    [Fact]
    public async Task TheWorker_ClosesAnExpiredHold_FreesItsSeat_AndAnnouncesItWithoutAnyRequest()
    {
        StartHost(holdExpiry: true);
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var alice = await NewUserAsync();
        await WaitForListenerAsync();
        await using var visitor = await ConnectAsync();
        await using var aliceHub = await ConnectAsync(alice);
        await visitor.Connection.InvokeAsync("SubscribeEvent", sale.EventId);
        var reservationId = await HoldAsync(alice, sale.EventId, sale.SeatIds[0]);
        await aliceHub.WaitForAsync("reservation", body => body.GetProperty("status").GetString() == "Held");

        await ExecAsync($"UPDATE reservation.reservations SET created_at_utc = now() - interval '20 minutes', expires_at_utc = now() - interval '10 minutes' WHERE id = '{reservationId}'");
        await ExecAsync($"UPDATE reservation.event_seats SET hold_expires_at_utc = now() - interval '10 minutes' WHERE reservation_id = '{reservationId}'");

        // No request is made from here on: the worker alone closes the hold.
        await aliceHub.WaitForAsync("reservation", body => body.GetProperty("reservationId").GetGuid() == reservationId && body.GetProperty("status").GetString() == "Expired");
        await visitor.WaitForAsync("availability", body => body.GetProperty("seats").EnumerateArray()
            .Any(s => s.GetProperty("seatId").GetGuid() == sale.SeatIds[0] && s.GetProperty("status").GetString() == "Available"));

        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{reservationId}'")).Should().Be("Expired");
        (await StringAsync($"SELECT status FROM reservation.event_seats WHERE event_id = '{sale.EventId}'")).Should().Be("Available");
        (await StringAsync($"SELECT count(*) FROM reservation.event_seats WHERE reservation_id = '{reservationId}'")).Should().Be("0");
    }

    [Fact]
    public async Task TheWorker_LeavesAReservationInCheckoutToThePaymentWorkflow()
    {
        StartHost(holdExpiry: true);
        var sale = await OnSaleEventBuilder.CreateAsync(_admin, seatCount: 1);
        var alice = await NewUserAsync();
        await WaitForListenerAsync();
        var reservationId = await HoldAsync(alice, sale.EventId, sale.SeatIds[0]);
        var orderId = await CheckoutAsync(alice, reservationId);
        await ExecAsync($"UPDATE reservation.reservations SET created_at_utc = now() - interval '20 minutes', expires_at_utc = now() - interval '10 minutes' WHERE id = '{reservationId}'");

        await Task.Delay(TimeSpan.FromSeconds(4)); // several worker passes (interval 1 s)

        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{reservationId}'")).Should().Be("Checkout", "its order belongs to the payment workflow");
        (await StringAsync($"SELECT count(*) FROM payments.orders WHERE id = '{orderId}'")).Should().Be("1");
    }
}

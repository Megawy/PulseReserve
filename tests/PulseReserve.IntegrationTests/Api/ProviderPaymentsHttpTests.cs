using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using PulseReserve.AppHost.Api;
using PulseReserve.IntegrationTests.Catalog;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Providers.Paymob;
using PulseReserve.Modules.Payments.Providers.Stripe;
using PulseReserve.Modules.Reservation.Features.Holds;
using Xunit;

namespace PulseReserve.IntegrationTests.Api;

/// <summary>
/// The REAL Stripe and Paymob adapters behind the real HTTP pipeline and a disposable PostgreSQL database, with the providers'
/// HTTP endpoints replaced by fake handlers. Nothing here contacts Stripe or Paymob, so these prove PulseReserve's handling of
/// signed callbacks, provider selection and the shared payment lifecycle; they are NOT live or sandbox verification.
/// </summary>
public sealed partial class ProviderPaymentsHttpTests : IClassFixture<PostgreSqlDatabaseFixture>, IDisposable
{
    private const string StripeSecret = "sk_test_integration_only_0000";
    private const string StripeWebhookSecret = "whsec_integration_only_0000";
    private const string PaymobSecret = "egy_sk_test_integration_only_0000";
    private const string PaymobPublic = "egy_pk_test_integration_only_0000";
    private const string PaymobHmacSecret = "paymob_hmac_integration_only_0000";
    private const long PaymobIntegration = 4097558;

    private readonly PostgreSqlDatabaseFixture _database;
    private readonly List<IDisposable> _disposables = [];

    public ProviderPaymentsHttpTests(PostgreSqlDatabaseFixture database)
    {
        _database = database;
        // The tests of this class share one container and assert global row counts ("no anomaly was stored"), so each starts empty.
        database.ResetAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
    }

    // ───────────── fake provider HTTP ─────────────

    private sealed class FakeProviderHttp : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];
        public Func<HttpMethod, string, string, HttpResponseMessage> Respond { get; set; } = (_, _, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            return Respond(request.Method, request.RequestUri.PathAndQuery, body);
        }

        public static Dictionary<string, string> Form(string body) =>
            body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty));

        public static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
    }

    private sealed class World
    {
        public required CatalogApiFactory Host { get; init; }
        public required TestUser Admin { get; init; }
        public required FakeProviderHttp StripeHttp { get; init; }
        public required FakeProviderHttp PaymobHttp { get; init; }
    }

    private World NewWorld(bool stripe = true, bool paymob = false, string currency = "USD")
    {
        var stripeHttp = new FakeProviderHttp();
        var paymobHttp = new FakeProviderHttp();
        var host = new CatalogApiFactory(_database, null, services =>
        {
            // Real adapters REPLACE the simulator; payments must then name a provider when several are enabled.
            services.RemoveAll<IPaymentProvider>();
            if (stripe)
            {
                var options = new StripeOptions(StripeSecret, StripeWebhookSecret, false, [currency], new Uri("https://shop.example.test"), new Uri(StripeOptions.DefaultApiBaseUrl));
                services.AddSingleton<IPaymentProvider>(new StripePaymentProvider(options, new HttpClient(stripeHttp) { BaseAddress = new Uri(StripeOptions.DefaultApiBaseUrl) }, TimeProvider.System));
            }

            if (paymob)
            {
                var options = new PaymobOptions(PaymobSecret, PaymobPublic, PaymobHmacSecret, [PaymobIntegration], [currency], false, new Uri(PaymobOptions.DefaultApiBaseUrl),
                    new Uri(PaymobOptions.DefaultCheckoutBaseUrl), new Uri("https://shop.example.test"), new Uri("https://api.example.test/api/webhooks/paymob"));
                services.AddSingleton<IPaymentProvider>(new PaymobPaymentProvider(options, new HttpClient(paymobHttp) { BaseAddress = new Uri(PaymobOptions.DefaultApiBaseUrl) }));
            }
        });
        _disposables.Add(host);
        return new World { Host = host, Admin = AdminTestSupport.RegisterAdmin(host), StripeHttp = stripeHttp, PaymobHttp = paymobHttp };
    }

    // ───────────── helpers ─────────────

    private static HttpRequestMessage Post(string path, object? body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private async Task<OrderResponse> CheckoutAsync(World w, TestUser user, OnSaleEventBuilder.SaleSetup sale, int seats = 1)
    {
        var hold = await user.Client.SendAsync(Post("/api/reservations", new { eventId = sale.EventId, seatIds = sale.SeatIds.Take(seats).ToArray() }, Guid.NewGuid().ToString()));
        hold.StatusCode.Should().Be(HttpStatusCode.Created, await hold.Content.ReadAsStringAsync());
        var reservation = (await hold.Content.ReadFromJsonAsync<ReservationResponse>())!;
        var order = await user.Client.SendAsync(Post("/api/orders", new { reservationId = reservation.ReservationId }, Guid.NewGuid().ToString()));
        order.StatusCode.Should().Be(HttpStatusCode.Created, await order.Content.ReadAsStringAsync());
        return (await order.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private async Task<(TestUser User, OnSaleEventBuilder.SaleSetup Sale, OrderResponse Order)> OrderAsync(World w, int seats = 1)
    {
        var sale = await OnSaleEventBuilder.CreateAsync(w.Admin.Client, seatCount: seats);
        var user = await AuthTestSupport.RegisterAsync(w.Host);
        return (user, sale, await CheckoutAsync(w, user, sale, seats));
    }

    /// <summary>
    /// Releases all callers at the same instant (a deterministic start gate, no sleeps), runs them concurrently and returns
    /// every result. Which interleaving the database sees is not controlled, so the tests assert INVARIANTS that must hold
    /// for every interleaving, and some repeat the scenario on fresh orders to cover more of them.
    /// </summary>
    private static async Task<T[]> RaceAsync<T>(int callers, Func<int, Task<T>> call)
    {
        var gate = new TaskCompletionSource();
        var running = Enumerable.Range(0, callers).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await call(i);
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(running);
    }

    private static Task<HttpResponseMessage> StartPaymentAsync(TestUser user, Guid orderId, string key, string? provider) =>
        user.Client.SendAsync(Post($"/api/orders/{orderId}/payments", provider is null ? null : new { provider }, key));

    private async Task<OrderResponse> StartOkAsync(TestUser user, Guid orderId, string key, string? provider)
    {
        var response = await StartPaymentAsync(user, orderId, key, provider);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private static string StripeSession(string id, string attempt, long amount, string currency, string status = "open", string payment = "unpaid", string? paymentIntent = null) =>
        JsonSerializer.Serialize(new
        {
            id, status, payment_status = payment, url = "https://checkout.stripe.com/c/pay/" + id, amount_total = amount, currency = currency.ToLowerInvariant(),
            client_reference_id = attempt, payment_intent = paymentIntent
        });

    private static void StripeBehaves(World w, Func<string, string>? sessionFor = null)
    {
        var sessions = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Attempt, long Amount, string Currency)>();
        w.StripeHttp.Respond = (method, path, body) =>
        {
            if (method == HttpMethod.Post && path == "/v1/checkout/sessions")
            {
                var form = FakeProviderHttp.Form(body);
                var id = "cs_test_" + form["client_reference_id"][..8];
                sessions[id] = (form["client_reference_id"], long.Parse(form["line_items[0][price_data][unit_amount]"]), form["line_items[0][price_data][currency]"]);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StripeSession(id, form["client_reference_id"], sessions[id].Amount, sessions[id].Currency), Encoding.UTF8, "application/json") };
            }

            if (method == HttpMethod.Get && path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal))
            {
                var id = path["/v1/checkout/sessions/".Length..];
                var s = sessions[id];
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StripeSession(id, s.Attempt, s.Amount, s.Currency, "complete", "paid", "pi_integration"), Encoding.UTF8, "application/json") };
            }

            if (method == HttpMethod.Post && path == "/v1/refunds")
                return FakeProviderHttp.Json(HttpStatusCode.OK, new { id = "re_integration", status = "succeeded" });

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static HttpRequestMessage StripeWebhook(string attemptId, string orderId, string sessionId, long amount, string currency, string type = "checkout.session.completed", string payment = "paid", string eventId = "evt_it_1", string? secret = null)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = eventId, ["type"] = type, ["livemode"] = false,
            ["data"] = new { @object = new Dictionary<string, object?> { ["id"] = sessionId, ["payment_status"] = payment, ["amount_total"] = amount, ["currency"] = currency.ToLowerInvariant(), ["client_reference_id"] = attemptId, ["metadata"] = new { attempt_id = attemptId, order_id = orderId } } }
        });
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", $"t={t},v1={StripeSignature.Compute(body, t, secret ?? StripeWebhookSecret)}");
        return request;
    }

    private static HttpRequestMessage PaymobWebhook(string attemptId, string paymobOrderId, long cents, string currency, long integration = PaymobIntegration, string? secret = null)
    {
        var obj = new JsonObject
        {
            ["id"] = 192036465, ["pending"] = false, ["amount_cents"] = cents, ["success"] = true, ["is_auth"] = false, ["is_capture"] = false, ["is_standalone_payment"] = true,
            ["is_voided"] = false, ["is_refunded"] = false, ["is_3d_secure"] = true, ["integration_id"] = integration, ["has_parent_transaction"] = false,
            ["created_at"] = "2030-01-01T11:33:44.592345", ["currency"] = currency, ["error_occured"] = false, ["owner"] = 302852,
            ["order"] = new JsonObject { ["id"] = long.Parse(paymobOrderId), ["merchant_order_id"] = attemptId },
            ["source_data"] = new JsonObject { ["type"] = "card", ["pan"] = "2346", ["sub_type"] = "MasterCard" }
        };
        var hmac = PaymobHmac.Compute(JsonDocument.Parse(obj.ToJsonString()).RootElement, secret ?? PaymobHmacSecret);
        var body = new JsonObject { ["type"] = "TRANSACTION", ["obj"] = obj }.ToJsonString();
        return new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/paymob?hmac={hmac}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private async Task<string?> StringAsync(string sql)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value?.ToString();
    }

    private async Task ExecAsync(string sql)
    {
        await using var db = _database.CreateReservationDbContext();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task RunWorkflowAsync(World w)
    {
        await using var scope = w.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PaymentsWorkflow>().RunOnceAsync();
    }

    private static long Minor(OrderResponse order) => CurrencyAmounts.ToMinorUnits(order.TotalAmount, order.Currency);

    // ───────────── tests ─────────────

    [Fact]
    public async Task Stripe_AVerifiedWebhook_PaysTheOrder_AndDuplicatesNeverDuplicateTicketsOrOutboxEvents()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attempt = started.Payment!;
        attempt.Provider.Should().Be("Stripe");
        attempt.IsSimulated.Should().BeFalse();
        attempt.SimulatorToken.Should().BeNull();
        attempt.CheckoutUrl.Should().StartWith("https://checkout.stripe.com/");
        (await StringAsync($"SELECT provider || '|' || provider_reference || '|' || (checkout_url IS NOT NULL) FROM payments.payment_attempts WHERE id = '{attempt.AttemptId}'"))
            .Should().Be($"Stripe|cs_test_{attempt.AttemptId.ToString()[..8]}|true");

        var anonymous = w.Host.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            var response = await anonymous.SendAsync(StripeWebhook(attempt.AttemptId.ToString(), order.OrderId.ToString(), $"cs_test_{attempt.AttemptId.ToString()[..8]}", Minor(started), started.Currency));
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            await RunWorkflowAsync(w);
        }

        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Paid");
        (await StringAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be("1");
        (await StringAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'")).Should().Be("2");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{order.ReservationId}'")).Should().Be("Confirmed");
        (await StringAsync($"SELECT (checkout_url IS NULL)::text FROM payments.payment_attempts WHERE id = '{attempt.AttemptId}'"))
            .Should().Be("true", "a finished attempt keeps no hosted-page link (it can embed a provider client secret)");
        (await StringAsync("SELECT count(*) FROM payments.payment_exceptions")).Should().Be("0", "the normal success path raises no exception");
    }

    [Fact]
    public async Task ACancelledOrder_NoLongerExposesTheHostedCheckoutLink_AndTheStoredLinkIsCleared()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        started.Payment!.CheckoutUrl.Should().NotBeNull();

        (await user.Client.SendAsync(Post($"/api/orders/{order.OrderId}/cancel", null, null))).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await (await user.Client.GetAsync($"/api/orders/{order.OrderId}")).Content.ReadFromJsonAsync<OrderResponse>();
        after!.Payment!.CheckoutUrl.Should().BeNull();
        (await StringAsync($"SELECT (checkout_url IS NULL)::text FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be("true");
    }

    [Fact]
    public async Task Stripe_ABadSignatureOrWrongAmount_ChangesNothing()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";
        var anonymous = w.Host.CreateClient();

        (await anonymous.SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, secret: "whsec_not_ours"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Authentic but wrong amount / currency: kept as evidence (one anomaly for this provider event) and acknowledged, never applied.
        (await anonymous.SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, 1, started.Currency))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), "EUR"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StringContent("{}") })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(1, "both mismatches carry the same provider event id");

        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Initiated");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
    }

    [Fact]
    public async Task Webhooks_AreAnonymousByNature_ButUnknownProvidersAndOversizedBodiesAreRefused()
    {
        var w = NewWorld();
        var client = w.Host.CreateClient();

        (await client.PostAsync("/api/webhooks/adyen", new StringContent("{}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync("/api/webhooks/paymob", new StringContent("{}"))).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "Paymob is not enabled in this host");
        (await client.PostAsync("/api/webhooks/stripe", new StringContent(new string('x', PaymentProviderEndpoints.MaxWebhookBytes + 10)))).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Stripe_ASuccessAfterTheReservationExpired_RequiresARefund_WhichIsCompleteOnlyAfterStripeConfirms()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        await ExecAsync($"UPDATE reservation.reservations SET created_at_utc = now() - interval '20 minutes', expires_at_utc = now() - interval '10 minutes' WHERE id = '{order.ReservationId}'");
        await ExecAsync($"UPDATE reservation.event_seats SET hold_expires_at_utc = now() - interval '10 minutes' WHERE reservation_id = '{order.ReservationId}'");

        var response = await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), $"cs_test_{attemptId[..8]}", Minor(started), started.Currency));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("RefundRequired");
        (await StringAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be("0");
        (await StringAsync($"SELECT provider || '|' || state FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("Stripe|Requested");

        await RunWorkflowAsync(w);

        w.StripeHttp.Requests.Should().Contain(r => r.Path == "/v1/refunds");
        var refund = FakeProviderHttp.Form(w.StripeHttp.Requests.Single(r => r.Path == "/v1/refunds").Body);
        refund["payment_intent"].Should().Be("pi_integration");
        refund["amount"].Should().Be(Minor(started).ToString());
        (await StringAsync($"SELECT state FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("Succeeded");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Refunded");
    }

    [Fact]
    public async Task Paymob_AVerifiedCallback_PaysTheOrder_AndRejectsForgedOrMisdirectedOnes()
    {
        var w = NewWorld(stripe: false, paymob: true);
        w.PaymobHttp.Respond = (_, _, _) => FakeProviderHttp.Json(HttpStatusCode.Created, new { id = "pi_it", client_secret = "egy_csk_test_it", intention_order_id = 217503754 });
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", "Paymob");
        started.Payment!.Provider.Should().Be("Paymob");
        started.Payment.CheckoutUrl.Should().StartWith("https://eg.checkout.paymob.com/?publicKey=");
        var attemptId = started.Payment.AttemptId.ToString();
        var anonymous = w.Host.CreateClient();

        (await anonymous.SendAsync(PaymobWebhook(attemptId, "217503754", Minor(started), started.Currency, secret: "wrong"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await anonymous.SendAsync(PaymobWebhook(attemptId, "217503754", Minor(started), started.Currency, integration: 1))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Authentic but wrong amount / wrong reference: kept as evidence (two kinds) and acknowledged, never applied.
        (await anonymous.SendAsync(PaymobWebhook(attemptId, "217503754", 1, started.Currency))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.SendAsync(PaymobWebhook(attemptId, "999", Minor(started), started.Currency))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(2);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");

        for (var i = 0; i < 2; i++)
            (await anonymous.SendAsync(PaymobWebhook(attemptId, "217503754", Minor(started), started.Currency))).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunWorkflowAsync(w);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Paid");
        (await StringAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be("1");
        (await StringAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'")).Should().Be("2");
    }

    [Fact]
    public async Task Paymob_ALateSuccess_RequiresARefund_ThatWaitsForAnOperator_NeverMarkedCompleteByGuessing()
    {
        var w = NewWorld(stripe: false, paymob: true);
        w.PaymobHttp.Respond = (_, _, _) => FakeProviderHttp.Json(HttpStatusCode.Created, new { id = "pi_it", client_secret = "egy_csk_test_it", intention_order_id = 217503754 });
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", "Paymob");
        var attemptId = started.Payment!.AttemptId.ToString();
        await ExecAsync($"UPDATE reservation.reservations SET created_at_utc = now() - interval '20 minutes', expires_at_utc = now() - interval '10 minutes' WHERE id = '{order.ReservationId}'");
        await ExecAsync($"UPDATE reservation.event_seats SET hold_expires_at_utc = now() - interval '10 minutes' WHERE reservation_id = '{order.ReservationId}'");

        (await w.Host.CreateClient().SendAsync(PaymobWebhook(attemptId, "217503754", Minor(started), started.Currency))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("RefundRequired");
        (await StringAsync($"SELECT provider || '|' || state FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("Paymob|Requested");

        await RunWorkflowAsync(w);

        // A refund of a Paymob payment is never attempted through an invented API: it waits for a person.
        (await StringAsync($"SELECT state FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("NeedsReview");
        (await StringAsync($"SELECT last_error FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Contain("provider dashboard");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("RefundRequired");
        w.PaymobHttp.Requests.Should().OnlyContain(r => r.Path == "/v1/intention/");
    }

    [Fact]
    public async Task ProviderChoice_IsServerControlled_ListedForTheOrder_AndNeverSwitchedSilently()
    {
        var w = NewWorld(stripe: true, paymob: true);
        StripeBehaves(w);
        w.PaymobHttp.Respond = (_, _, _) => FakeProviderHttp.Json(HttpStatusCode.Created, new { id = "pi_it", client_secret = "egy_csk_test_it", intention_order_id = 5 });
        var (user, _, order) = await OrderAsync(w);

        var options = await (await user.Client.GetAsync($"/api/orders/{order.OrderId}/payment-options")).Content.ReadFromJsonAsync<PaymentOptionsResponse>();
        options!.Providers.Select(p => p.Provider).Should().BeEquivalentTo("Stripe", "Paymob");
        options.Providers.Should().OnlyContain(p => !p.IsSimulated);

        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k0", null), HttpStatusCode.BadRequest, "Payment.ProviderRequired");
        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k0", "Adyen"), HttpStatusCode.BadRequest, "Payment.ProviderNotEnabled");
        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k0", "https://evil.example/pay"), HttpStatusCode.BadRequest, "Payment.ProviderNotEnabled");
        (await StringAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be("0");

        var first = await StartOkAsync(user, order.OrderId, "k1", "Stripe");
        first.Payment!.Provider.Should().Be("Stripe");

        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k1", "Paymob"), HttpStatusCode.Conflict, "Payment.ProviderMismatch");
        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k2", "Paymob"), HttpStatusCode.Conflict, "Payment.AttemptInProgress");
        w.PaymobHttp.Requests.Should().BeEmpty("the second provider was never called");
        (await StartOkAsync(user, order.OrderId, "k1", null)).Payment!.CheckoutUrl.Should().Be(first.Payment.CheckoutUrl, "a replay returns the same hosted page");
    }

    [Fact]
    public async Task AnAmbiguousProviderTimeout_LeavesTheAttemptInPlace_AndTheSameKeyResumesIt()
    {
        var w = NewWorld(stripe: true, paymob: true);
        StripeBehaves(w);
        var happy = w.StripeHttp.Respond;
        w.StripeHttp.Respond = (_, _, _) => throw new TaskCanceledException("timed out");
        w.PaymobHttp.Respond = (_, _, _) => FakeProviderHttp.Json(HttpStatusCode.Created, new { id = "pi_it", client_secret = "egy_csk_test_it", intention_order_id = 5 });
        var (user, _, order) = await OrderAsync(w);

        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k1", "Stripe"), HttpStatusCode.ServiceUnavailable, "Payment.ProviderTemporarilyUnavailable");
        (await StringAsync($"SELECT status || '|' || provider || '|' || (provider_reference IS NULL) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be("Initiated|Stripe|true");

        // No failover: another provider is refused while the first attempt is unresolved.
        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k2", "Paymob"), HttpStatusCode.Conflict, "Payment.AttemptInProgress");
        w.PaymobHttp.Requests.Should().BeEmpty();

        w.StripeHttp.Respond = happy;
        var resumed = await StartOkAsync(user, order.OrderId, "k1", "Stripe");
        resumed.Payment!.CheckoutUrl.Should().NotBeNull();
        (await StringAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be("1");
    }

    [Fact]
    public async Task ADefinitiveProviderRefusal_FreesTheOrderForAnotherProvider()
    {
        var w = NewWorld(stripe: true, paymob: true);
        w.StripeHttp.Respond = (_, _, _) => FakeProviderHttp.Json(HttpStatusCode.Unauthorized, new { error = new { type = "invalid_request_error", message = "Invalid API Key provided" } });
        w.PaymobHttp.Respond = (_, _, _) => FakeProviderHttp.Json(HttpStatusCode.Created, new { id = "pi_it", client_secret = "egy_csk_test_it", intention_order_id = 5 });
        var (user, _, order) = await OrderAsync(w);

        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k1", "Stripe"), HttpStatusCode.ServiceUnavailable, "Payment.ProviderRejected");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be("Cancelled");

        (await StartOkAsync(user, order.OrderId, "k2", "Paymob")).Payment!.Provider.Should().Be("Paymob");
    }

    [Fact]
    public async Task WithNoProviderEnabled_PaymentIsUnavailable_AndTheOptionsListIsEmpty()
    {
        var w = NewWorld(stripe: false, paymob: false);
        var (user, _, order) = await OrderAsync(w);

        var options = await (await user.Client.GetAsync($"/api/orders/{order.OrderId}/payment-options")).Content.ReadFromJsonAsync<PaymentOptionsResponse>();
        options!.Providers.Should().BeEmpty();
        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k1", null), HttpStatusCode.ServiceUnavailable, "Payment.ProviderUnavailable");
    }

    [Fact]
    public async Task InvalidStartPaymentRequests_LeaveNoAttemptRowsAndTheOrderUntouched()
    {
        var w = NewWorld(stripe: true, paymob: true);
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);

        async Task<HttpStatusCode> SendAsync(string body, string contentType, string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{order.OrderId}/payments") { Content = new StringContent(body, Encoding.UTF8, contentType) };
            request.Headers.Add("Idempotency-Key", key);
            return (await user.Client.SendAsync(request)).StatusCode;
        }

        (await SendAsync("{ not json", "application/json", "b1")).Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync("{\"provider\":5}", "application/json", "b2")).Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync("{\"provider\":\"Stripe\"}", "text/plain", "b3")).Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await SendAsync("{\"provider\":\"Adyen\"}", "application/json", "b4")).Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync("{}", "application/json", "b5")).Should().Be(HttpStatusCode.BadRequest, "two providers are enabled, so one must be named");

        (await StringAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be("0");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
        w.StripeHttp.Requests.Should().BeEmpty();
        w.PaymobHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TheAdminProviderList_ExposesCapabilitiesOnly_ToAdministrators()
    {
        var w = NewWorld(stripe: true, paymob: true);
        var (user, _, _) = await OrderAsync(w);

        var response = await w.Admin.Client.GetAsync("/api/admin/payments/providers");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var providers = JsonDocument.Parse(body).RootElement.EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        providers["Paymob"].GetProperty("supportsRefunds").GetBoolean().Should().BeFalse();
        providers["Paymob"].GetProperty("supportsPaymentLookup").GetBoolean().Should().BeFalse();
        providers["Stripe"].GetProperty("supportsRefunds").GetBoolean().Should().BeTrue();
        body.Should().NotContain(StripeSecret).And.NotContain(StripeWebhookSecret).And.NotContain(PaymobSecret).And.NotContain(PaymobHmacSecret);
        (await user.Client.GetAsync("/api/admin/payments/providers")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ───────────── concurrency (PostgreSQL row locks, unique indexes; NOT executable without Docker) ─────────────

    [Fact]
    public async Task ConcurrentIdenticalSuccessWebhooks_ProduceOneTransition_OneTicketPerSeat_AndTwoOutboxRows()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w, seats: 2);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";

        // The same event delivered 8 times at once (a provider retry storm), each on its own connection.
        var statuses = await RaceAsync(8, async _ => (await w.Host.CreateClient().SendAsync(
            StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, eventId: "evt_storm"))).StatusCode);
        await RunWorkflowAsync(w);
        await RunWorkflowAsync(w);

        statuses.Should().OnlyContain(c => c == HttpStatusCode.OK, "every delivery is acknowledged, none is an error");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Paid");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Succeeded");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{order.ReservationId}'")).Should().Be("Confirmed");
        (await StringAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be("2", "one ticket per purchased seat, however many deliveries");
        (await StringAsync($"SELECT count(DISTINCT seat_id) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be("2");
        (await StringAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'")).Should().Be("2");
        (await StringAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}' AND state = 'Done' AND type = 'IssueTickets'")).Should().Be("1");
        (await StringAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("0", "a paid order owes no refund");
    }

    [Fact]
    public async Task ASuccessRacingASessionExpired_LeavesOneConsistentVerdict_AndNoDuplicateSideEffects()
    {
        var w = NewWorld();
        StripeBehaves(w);

        // Repeated on fresh orders so more interleavings are covered (repetition, not sleeping).
        for (var round = 0; round < 6; round++)
        {
            var (user, _, order) = await OrderAsync(w);
            var started = await StartOkAsync(user, order.OrderId, $"pay-{round}", null);
            var attemptId = started.Payment!.AttemptId.ToString();
            var session = $"cs_test_{attemptId[..8]}";

            var statuses = await RaceAsync(2, async i => (await w.Host.CreateClient().SendAsync(i == 0
                ? StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, eventId: $"evt_paid_{round}")
                : StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, "checkout.session.expired", "unpaid", $"evt_expired_{round}"))).StatusCode);
            await RunWorkflowAsync(w);

            statuses.Should().OnlyContain(c => c == HttpStatusCode.OK, "the losing event is acknowledged as a conflict, never an error");
            var attempt = await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'");
            attempt.Should().BeOneOf("Succeeded", "Failed");
            var orderStatus = await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'");
            var tickets = await StringAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'");
            var outbox = await StringAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'");
            (await StringAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be("0");

            var exceptions = await StringAsync($"SELECT count(*) FROM payments.payment_exceptions WHERE order_id = '{order.OrderId}'");
            if (attempt == "Succeeded")
            {
                (orderStatus, tickets, outbox).Should().Be(("Paid", "1", "2"), $"round {round}: a recorded success is fully applied exactly once");
                exceptions.Should().Be("0", $"round {round}: the success won, so there is nothing to review");
            }
            else
            {
                (orderStatus, tickets, outbox).Should().Be(("PendingPayment", "0", "0"), $"round {round}: a recorded failure leaves no paid-order side effects");
                exceptions.Should().Be("1", $"round {round}: the failure won, so the verified success must be waiting for review, not lost");
            }
        }
    }

    [Fact]
    public async Task ConcurrentStartsWithTheSameKey_ReturnOneAttempt_AndOneConsistentCheckoutUrl()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);

        var responses = await RaceAsync(6, _ => StartPaymentAsync(user, order.OrderId, "same-key", null));
        var bodies = new List<OrderResponse>();
        foreach (var response in responses)
        {
            response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
            bodies.Add((await response.Content.ReadFromJsonAsync<OrderResponse>())!);
        }

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1, "exactly one request created the attempt, the rest replayed it");
        (await StringAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be("1");
        bodies.Select(b => b.Payment!.AttemptId).Distinct().Should().ContainSingle();
        var stored = await StringAsync($"SELECT checkout_url FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'");
        bodies.Select(b => b.Payment!.CheckoutUrl).Should().OnlyContain(u => u == stored, "every caller is shown the one persisted URL");
        w.StripeHttp.Requests.Where(r => r.Path == "/v1/checkout/sessions").Select(r => FakeProviderHttp.Form(r.Body)["client_reference_id"])
            .Distinct().Should().ContainSingle("only one attempt ever reached the provider");
    }

    [Fact]
    public async Task ConcurrentStartsAgainstAProviderWithoutIdempotency_CallItAtMostOnce_AndNeverCreateASecondSession()
    {
        var w = NewWorld(stripe: false, paymob: true);
        var intention = 5000;
        w.PaymobHttp.Respond = (_, _, _) => FakeProviderHttp.Json(HttpStatusCode.Created,
            new { id = "pi", client_secret = $"egy_csk_test_{Interlocked.Increment(ref intention)}", intention_order_id = intention });
        var (user, _, order) = await OrderAsync(w);

        var responses = await RaceAsync(6, _ => StartPaymentAsync(user, order.OrderId, "same-key", "Paymob"));

        // Paymob cannot deduplicate an initiation, so a repeat of the same key is never sent to it: one request creates the
        // session, the others either replay the stored result or are told the first call is still in progress. (Which of the
        // two a given duplicate gets depends on timing; the invariants below hold for every interleaving.)
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        w.PaymobHttp.Requests.Count(r => r.Path.Contains("intention", StringComparison.OrdinalIgnoreCase)).Should().Be(1, "the provider that cannot deduplicate was asked exactly once");
        (await StringAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be("1");
        var stored = await StringAsync($"SELECT checkout_url FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'");
        stored.Should().NotBeNull();
        foreach (var ok in responses.Where(r => r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK))
            (await ok.Content.ReadFromJsonAsync<OrderResponse>())!.Payment!.CheckoutUrl.Should().Be(stored);
    }

    [Fact]
    public async Task AnAmbiguousPaymobInitiation_ClosesTheAttempt_AndANewKeyStartsAFreshOne()
    {
        var w = NewWorld(stripe: false, paymob: true);
        var calls = 0;
        w.PaymobHttp.Respond = (_, _, _) => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            : FakeProviderHttp.Json(HttpStatusCode.Created, new { id = "pi", client_secret = "egy_csk_test_2", intention_order_id = 9001 });
        var (user, _, order) = await OrderAsync(w);

        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k1", "Paymob"), HttpStatusCode.Conflict, "Payment.InitiationUncertain");
        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "k1", "Paymob"), HttpStatusCode.Conflict, "Payment.InitiationUncertain");
        (await StringAsync($"SELECT status || '|' || COALESCE(failure_code, '') FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'"))
            .Should().Be("Cancelled|initiation_uncertain");
        w.PaymobHttp.Requests.Should().HaveCount(1, "the replay of the closed attempt did not reach the provider");

        (await StartPaymentAsync(user, order.OrderId, "k2", "Paymob")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await CountAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be(2);
    }

    // ───────────── late verified success after a recorded failure (needs PostgreSQL) ─────────────

    /// <summary>An order with a Stripe attempt that was recorded as Failed by a verified checkout.session.expired event.</summary>
    private async Task<(World W, TestUser User, OrderResponse Order, OrderResponse Started, string AttemptId, string Session)> FailedAttemptAsync()
    {
        var w = NewWorld();
        StripeBehaves(w);
        var (user, _, order) = await OrderAsync(w);
        var started = await StartOkAsync(user, order.OrderId, "pay-1", null);
        var attemptId = started.Payment!.AttemptId.ToString();
        var session = $"cs_test_{attemptId[..8]}";
        var expired = await w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, Minor(started), started.Currency, "checkout.session.expired", "unpaid", "evt_it_expired"));
        expired.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Failed");
        return (w, user, order, started, attemptId, session);
    }

    private static Task<HttpResponseMessage> LateSuccessAsync(World w, string attemptId, OrderResponse order, OrderResponse started, string session, string eventId = "evt_it_late", string? secret = null, long? amount = null) =>
        w.Host.CreateClient().SendAsync(StripeWebhook(attemptId, order.OrderId.ToString(), session, amount ?? Minor(started), started.Currency, eventId: eventId, secret: secret));

    private async Task<long> CountAsync(string sql) => long.Parse((await StringAsync(sql))!);

    [Fact]
    public async Task LateSuccess_AfterARecordedFailure_IsKeptForReview_NeverAppliedToTheOrder_AndTheFailedVerdictSurvives()
    {
        var (w, user, order, started, attemptId, session) = await FailedAttemptAsync();

        var response = await LateSuccessAsync(w, attemptId, order, started, session);
        await RunWorkflowAsync(w);
        await RunWorkflowAsync(w);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("late_success_recorded_for_review");
        (await StringAsync($"SELECT status || '|' || failure_code FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Failed|checkout_session_expired", "the original verdict is evidence and is not rewritten");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{order.ReservationId}'")).Should().NotBe("Confirmed", "no seat is sold on a contradicted payment");
        (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be(0, "no refund is created or claimed by the system");
        (await CountAsync($"SELECT count(*) FROM payments.reconciliation_tasks WHERE subject_id = '{attemptId}'")).Should().Be(0, "workers never touch an exception");
        (await StringAsync($"SELECT state || '|' || provider || '|' || original_attempt_status || '|' || order_status_at_detection || '|' || amount::text || '|' || currency || '|' || provider_event_id FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'"))
            .Should().Be($"Open|Stripe|Failed|PendingPayment|{started.TotalAmount.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}|{started.Currency}|evt_it_late");

        // The customer cannot be charged a second time while the exception is open (and nothing falls back to another provider).
        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "pay-2", null), HttpStatusCode.Conflict, "Payment.OrderHeldForReview");
        (await CountAsync($"SELECT count(*) FROM payments.payment_attempts WHERE order_id = '{order.OrderId}'")).Should().Be(1);

        // The operator sees both groups of facts and the one supported next step.
        var list = await w.Admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/exceptions?state=Open");
        var item = list.EnumerateArray().Single(e => e.GetProperty("attemptId").GetString() == attemptId);
        item.GetProperty("originalAttemptStatus").GetString().Should().Be("Failed");
        item.GetProperty("orderStatus").GetString().Should().Be("PendingPayment");
        item.GetProperty("nextAction").GetString().Should().Be("RefundExternallyThenRecord");
        item.GetProperty("orderRefundState").ValueKind.Should().Be(JsonValueKind.Null);
        (await w.Admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/overview")).GetProperty("paymentExceptionsOpen").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task LateSuccess_WithdrawsAnAttemptAlreadyInFlight_AndALaterSuccessOfThatAttemptIsStillRecorded()
    {
        var (w, user, order, started, attemptId, session) = await FailedAttemptAsync();
        var second = await StartOkAsync(user, order.OrderId, "pay-2", null); // the customer started again after the failure
        var secondId = second.Payment!.AttemptId.ToString();

        (await LateSuccessAsync(w, attemptId, order, started, session)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StringAsync($"SELECT status || '|' || COALESCE(failure_code, '') || '|' || (checkout_url IS NULL) FROM payments.payment_attempts WHERE id = '{secondId}'"))
            .Should().Be("Cancelled|order_held_for_review|true", "the in-flight payment link is withdrawn so the customer is not charged twice");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Failed");

        // If the customer had already paid the withdrawn attempt, that verified success is honoured by the existing rules, and
        // the exception still shows the earlier charge to an administrator.
        var paid = await w.Host.CreateClient().SendAsync(StripeWebhook(secondId, order.OrderId.ToString(), $"cs_test_{secondId[..8]}", Minor(second), second.Currency, eventId: "evt_it_second"));
        paid.StatusCode.Should().Be(HttpStatusCode.OK);
        await RunWorkflowAsync(w);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Paid");
        (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(1);
        (await StringAsync($"SELECT state FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'")).Should().Be("Open");
        var list = await w.Admin.Client.GetFromJsonAsync<JsonElement>("/api/admin/payments/exceptions?state=Open");
        list.EnumerateArray().Single(e => e.GetProperty("attemptId").GetString() == attemptId).GetProperty("orderStatus").GetString().Should().Be("Paid");
    }

    [Fact]
    public async Task LateSuccess_RepeatedAndRacing_CreatesExactlyOneException()
    {
        var (w, _, order, started, attemptId, session) = await FailedAttemptAsync();

        var statuses = await RaceAsync(8, i => LateSuccessAsync(w, attemptId, order, started, session, eventId: $"evt_it_late_{i % 3}").ContinueWith(t => t.Result.StatusCode));
        await RunWorkflowAsync(w);

        statuses.Should().OnlyContain(c => c == HttpStatusCode.OK, "every delivery is acknowledged, none is an error");
        (await CountAsync($"SELECT count(*) FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'")).Should().Be(1);
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Failed");
        (await CountAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{order.OrderId}'")).Should().Be(0);
    }

    [Fact]
    public async Task LateSuccess_ThatIsForgedOrDoesNotMatchTheOrder_CreatesNoException()
    {
        var (w, _, order, started, attemptId, session) = await FailedAttemptAsync();

        // Not authentic: refused, nothing stored.
        (await LateSuccessAsync(w, attemptId, order, started, session, secret: "whsec_not_ours")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountAsync("SELECT count(*) FROM payments.payment_anomalies")).Should().Be(0, "an unauthenticated event is never evidence");

        // Authentic but wrong amount, and authentic but a session reference that is not the stored one: neither may become an exception
        // (which would be treated as 'money was taken for this order'); each is kept as an anomaly and acknowledged.
        (await LateSuccessAsync(w, attemptId, order, started, session, amount: 100)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LateSuccessAsync(w, attemptId, order, started, "cs_test_someone_else")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CountAsync("SELECT count(*) FROM payments.payment_exceptions")).Should().Be(0);
        (await StringAsync("SELECT string_agg(kind, ',' ORDER BY kind) FROM payments.payment_anomalies")).Should().Be("AmountOrCurrencyMismatch,ReferenceMismatch");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Failed");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
        (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{order.OrderId}'")).Should().Be(0);
    }

    [Fact]
    public async Task LateSuccess_ForACancelledOrAnExpiredOrder_IsRecorded_ButNeverSoldTicketedOrRefundedBySystem()
    {
        // Cancelled by the customer after the failure.
        var (w, user, order, started, attemptId, session) = await FailedAttemptAsync();
        (await user.Client.SendAsync(Post($"/api/orders/{order.OrderId}/cancel", null, null))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LateSuccessAsync(w, attemptId, order, started, session)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("Cancelled");
        (await StringAsync($"SELECT order_status_at_detection FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'")).Should().Be("Cancelled");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{order.ReservationId}'")).Should().NotBe("Confirmed");

        // The hold ran out after the failure.
        var second = await FailedAttemptAsync();
        await ExecAsync($"UPDATE reservation.reservations SET created_at_utc = now() - interval '20 minutes', expires_at_utc = now() - interval '10 minutes' WHERE id = '{second.Order.ReservationId}'");
        await ExecAsync($"UPDATE reservation.event_seats SET hold_expires_at_utc = now() - interval '10 minutes' WHERE reservation_id = '{second.Order.ReservationId}'");
        (await LateSuccessAsync(second.W, second.AttemptId, second.Order, second.Started, second.Session)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunWorkflowAsync(second.W);
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{second.Order.OrderId}'")).Should().BeOneOf("PendingPayment", "Expired");
        (await StringAsync($"SELECT status FROM reservation.reservations WHERE id = '{second.Order.ReservationId}'")).Should().NotBe("Confirmed");

        foreach (var id in new[] { order.OrderId, second.Order.OrderId })
        {
            (await CountAsync($"SELECT count(*) FROM payments.tickets WHERE order_id = '{id}'")).Should().Be(0);
            (await CountAsync($"SELECT count(*) FROM payments.outbox_events WHERE aggregate_id = '{id}'")).Should().Be(0);
            (await CountAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{id}'")).Should().Be(0);
        }
    }

    [Fact]
    public async Task ResolvingALateSuccessException_IsAnAuditedAdministratorStatement_Idempotent_AndChangesNothingElse()
    {
        var (w, user, order, started, attemptId, session) = await FailedAttemptAsync();
        (await LateSuccessAsync(w, attemptId, order, started, session)).StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await StringAsync($"SELECT id FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'"))!;
        object Body(string? reference = "re_dashboard_1", bool confirmed = true, decimal? amount = null, string currency = "USD") =>
            new { refundPerformedExternally = confirmed, amount = amount ?? started.TotalAmount, currency, externalReference = reference, note = "SECRET-NOTE-TEXT" };
        Task<HttpResponseMessage> Resolve(object body) => w.Admin.Client.PostAsJsonAsync($"/api/admin/payments/exceptions/{id}/resolve", body);

        await AssertProblemAsync(await Resolve(Body(confirmed: false)), HttpStatusCode.BadRequest, "Operations.ExceptionNotConfirmed");
        await AssertProblemAsync(await Resolve(Body(amount: 1m)), HttpStatusCode.Conflict, "Operations.ExceptionAmountMismatch");
        await AssertProblemAsync(await Resolve(Body(currency: "EUR")), HttpStatusCode.Conflict, "Operations.ExceptionAmountMismatch");
        (await StringAsync($"SELECT state FROM payments.payment_exceptions WHERE id = '{id}'")).Should().Be("Open");

        var first = await Resolve(Body());
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        (await Resolve(Body())).StatusCode.Should().Be(HttpStatusCode.OK, "an identical repeat is an idempotent replay");
        await AssertProblemAsync(await Resolve(Body(reference: "OTHER")), HttpStatusCode.Conflict, "Operations.ExceptionResolutionConflict");

        (await StringAsync($"SELECT state || '|' || resolution || '|' || resolved_by::text || '|' || resolution_reference FROM payments.payment_exceptions WHERE id = '{id}'"))
            .Should().Be($"Resolved|RefundedExternally|{w.Admin.Id}|re_dashboard_1");
        (await CountAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{id}' AND action = 'ResolvePaymentException' AND actor_user_id = '{w.Admin.Id}'")).Should().Be(1, "a replay audits nothing new");
        (await StringAsync($"SELECT detail FROM payments.admin_audit_log WHERE subject_id = '{id}'")).Should().NotContain("SECRET-NOTE-TEXT").And.Contain("NOT provider-verified");
        (await StringAsync($"SELECT status FROM payments.payment_attempts WHERE id = '{attemptId}'")).Should().Be("Failed", "resolving never rewrites the verdict");
        (await StringAsync($"SELECT status FROM payments.orders WHERE id = '{order.OrderId}'")).Should().Be("PendingPayment");
        (await CountAsync($"SELECT count(*) FROM payments.refunds WHERE order_id = '{order.OrderId}'")).Should().Be(0);
        w.StripeHttp.Requests.Should().NotContain(r => r.Path == "/v1/refunds", "no provider is called to record a statement");

        // Recording the return of the money does not reopen the order for payment: two applied payments for one order is the
        // thing that must never happen, so the customer starts a new order instead.
        await AssertProblemAsync(await StartPaymentAsync(user, order.OrderId, "pay-2", null), HttpStatusCode.Conflict, "Payment.OrderHeldForReview");
    }

    [Fact]
    public async Task ResolvingALateSuccessException_ConcurrentlyRecordsExactlyOneStatement()
    {
        var (w, _, order, started, attemptId, session) = await FailedAttemptAsync();
        await LateSuccessAsync(w, attemptId, order, started, session);
        var id = (await StringAsync($"SELECT id FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'"))!;

        var responses = await RaceAsync(8, i => w.Admin.Client.PostAsJsonAsync($"/api/admin/payments/exceptions/{id}/resolve",
            new { refundPerformedExternally = true, amount = started.TotalAmount, currency = started.Currency, externalReference = $"REF-{i % 2}" }));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeGreaterThanOrEqualTo(1);
        (await CountAsync($"SELECT count(*) FROM payments.admin_audit_log WHERE subject_id = '{id}' AND action = 'ResolvePaymentException'")).Should().Be(1);
        (await StringAsync($"SELECT state FROM payments.payment_exceptions WHERE id = '{id}'")).Should().Be("Resolved");
    }

    [Fact]
    public async Task PaymentExceptionRoutes_NeedAnAdministratorAndAnAntiforgeryToken()
    {
        var (w, _, order, started, attemptId, session) = await FailedAttemptAsync();
        await LateSuccessAsync(w, attemptId, order, started, session);
        var id = (await StringAsync($"SELECT id FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'"))!;
        var body = new { refundPerformedExternally = true, amount = started.TotalAmount, currency = started.Currency };

        var anonymous = w.Host.CreateClient();
        await AuthTestSupport.RefreshCsrfAsync(anonymous);
        await AssertProblemAsync(await anonymous.GetAsync("/api/admin/payments/exceptions"), HttpStatusCode.Unauthorized, "Auth.Unauthorized");
        await AssertProblemAsync(await anonymous.PostAsJsonAsync($"/api/admin/payments/exceptions/{id}/resolve", body), HttpStatusCode.Unauthorized, "Auth.Unauthorized");
        var customer = await AuthTestSupport.RegisterAsync(w.Host);
        await AssertProblemAsync(await customer.Client.GetAsync("/api/admin/payments/exceptions"), HttpStatusCode.Forbidden, "Auth.Forbidden");
        await AssertProblemAsync(await customer.Client.PostAsJsonAsync($"/api/admin/payments/exceptions/{id}/resolve", body), HttpStatusCode.Forbidden, "Auth.Forbidden");

        var admin = await AdminTestSupport.RegisterAdminAsync(w.Host);
        admin.Client.DefaultRequestHeaders.Remove(AuthTestSupport.CsrfHeader);
        await AssertProblemAsync(await admin.Client.PostAsJsonAsync($"/api/admin/payments/exceptions/{id}/resolve", body), HttpStatusCode.Forbidden, "Csrf.Invalid");
        (await w.Admin.Client.GetAsync("/api/admin/payments/exceptions?state=Bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertProblemAsync(await w.Admin.Client.PostAsJsonAsync($"/api/admin/payments/exceptions/{Guid.NewGuid()}/resolve", body), HttpStatusCode.NotFound, "Operations.NotFound");

        (await StringAsync($"SELECT state FROM payments.payment_exceptions WHERE id = '{id}'")).Should().Be("Open");
    }

    [Fact]
    public async Task PaymentExceptions_AreProtectedByTheDatabase()
    {
        var (w, _, order, started, attemptId, session) = await FailedAttemptAsync();
        await LateSuccessAsync(w, attemptId, order, started, session);
        var id = (await StringAsync($"SELECT id FROM payments.payment_exceptions WHERE attempt_id = '{attemptId}'"))!;

        // The Failed verdict of an attempt with an exception can never be rewritten, and the facts never change.
        var rewrite = () => ExecAsync($"UPDATE payments.payment_attempts SET status = 'Succeeded' WHERE id = '{attemptId}'");
        await rewrite.Should().ThrowAsync<PostgresException>().WithMessage("*cannot change*");
        var facts = () => ExecAsync($"UPDATE payments.payment_exceptions SET amount = 1 WHERE id = '{id}'");
        await facts.Should().ThrowAsync<PostgresException>().WithMessage("*immutable*");
        var delete = () => ExecAsync($"DELETE FROM payments.payment_exceptions WHERE id = '{id}'");
        await delete.Should().ThrowAsync<PostgresException>().WithMessage("*cannot be deleted*");

        // A resolution is all or nothing, and a resolved exception is final.
        var half = () => ExecAsync($"UPDATE payments.payment_exceptions SET state = 'Resolved' WHERE id = '{id}'");
        await half.Should().ThrowAsync<PostgresException>().WithMessage("*chk_payment_exceptions_resolution_fields*");
        var stray = () => ExecAsync($"UPDATE payments.payment_exceptions SET resolution_note = 'x' WHERE id = '{id}'");
        await stray.Should().ThrowAsync<PostgresException>().WithMessage("*chk_payment_exceptions_resolution_fields*");
        await ExecAsync($"UPDATE payments.payment_exceptions SET state = 'Resolved', resolution = 'RefundedExternally', resolved_by = '{w.Admin.Id}', resolved_at_utc = now() WHERE id = '{id}'");
        var reopen = () => ExecAsync($"UPDATE payments.payment_exceptions SET state = 'Open', resolution = NULL, resolved_by = NULL, resolved_at_utc = NULL WHERE id = '{id}'");
        await reopen.Should().ThrowAsync<PostgresException>().WithMessage("*final*");

        // An exception must name a Failed attempt of its own order, and there is one per attempt.
        var duplicate = () => ExecAsync(
            "INSERT INTO payments.payment_exceptions (id, kind, attempt_id, order_id, provider, is_simulated, amount, currency, original_attempt_status, order_status_at_detection, detected_at_utc, state) " +
            $"VALUES (gen_random_uuid(), 'LateSuccessAfterFailedAttempt', '{attemptId}', '{order.OrderId}', 'Stripe', false, 1, 'USD', 'Failed', 'PendingPayment', now(), 'Open')");
        await duplicate.Should().ThrowAsync<PostgresException>("one exception per attempt");

        var (inFlightUser, _, inFlightOrder) = await OrderAsync(w);
        var inFlight = await StartOkAsync(inFlightUser, inFlightOrder.OrderId, "pay-x", null);
        var notFailed = () => ExecAsync(
            "INSERT INTO payments.payment_exceptions (id, kind, attempt_id, order_id, provider, is_simulated, amount, currency, original_attempt_status, order_status_at_detection, detected_at_utc, state) " +
            $"VALUES (gen_random_uuid(), 'LateSuccessAfterFailedAttempt', '{inFlight.Payment!.AttemptId}', '{inFlightOrder.OrderId}', 'Stripe', false, 1, 'USD', 'Initiated', 'PendingPayment', now(), 'Open')");
        await notFailed.Should().ThrowAsync<PostgresException>().WithMessage("*must name a Failed attempt*");
    }

    [Fact]
    public async Task PaymentOptions_BelongToTheOrderOwner_Only()
    {
        var w = NewWorld();
        var (_, _, order) = await OrderAsync(w);
        var stranger = await AuthTestSupport.RegisterAsync(w.Host);

        (await stranger.Client.GetAsync($"/api/orders/{order.OrderId}/payment-options")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await w.Host.CreateClient().GetAsync($"/api/orders/{order.OrderId}/payment-options")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expectedStatus, body);
        JsonDocument.Parse(body).RootElement.GetProperty("title").GetString().Should().Be(expectedCode);
        // No exception text or stack trace may leak. The expected code itself can legitimately contain the word (for example
        // "Operations.ExceptionNotConfirmed"), so it is removed before looking for the word anywhere else.
        body.Replace(expectedCode, string.Empty, StringComparison.Ordinal).Should().NotContainAny("Exception", "   at ", StripeSecret, PaymobSecret);
    }
}

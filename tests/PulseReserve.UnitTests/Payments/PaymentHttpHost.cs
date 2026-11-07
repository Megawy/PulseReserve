using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PulseReserve.AppHost.Api;
using PulseReserve.AppHost.Configuration;
using PulseReserve.AppHost.Security;
using PulseReserve.Modules.Payments.Domain;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Persistence;
using PulseReserve.Modules.Payments.Providers.Paymob;
using PulseReserve.Modules.Payments.Providers.Stripe;

namespace PulseReserve.UnitTests.Payments;

/// <summary>A notification adapter that sends nothing (the host only needs the dependency to exist).</summary>
internal sealed class SilentNotificationSender : INotificationSender
{
    public string Channel => "Silent";
    public bool IsAvailable => false;
    public Task SendAsync(NotificationRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Lines);

    public void Dispose()
    {
    }

    private sealed class Capture(string category, List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = $"[{category}] {formatter(state, exception)}";
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                text += " | " + string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"));
            if (exception is not null)
                text += " | " + exception;
            lock (lines)
                lines.Add(text);
        }
    }
}

/// <summary>Authenticates a request from the <c>X-Test-User</c> header (a customer id), so no identity database is needed.</summary>
internal sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestHeader";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var id) || !Guid.TryParse(id, out var userId))
            return Task.FromResult(AuthenticateResult.NoResult());
        var role = Request.Headers.ContainsKey("X-Test-Admin") ? AuthPolicies.AdminRole : AuthPolicies.CustomerRole;
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>
/// The real payment endpoints (<c>MapOrderApi</c>, <c>MapPaymentProviderApi</c>), the real policies, the real antiforgery
/// filter and the real webhook service in an in-memory ASP.NET Core test server, with an in-memory order store and
/// fake provider HTTP. It needs no database and no Docker, but it is NOT the production database: it proves HTTP binding,
/// status codes, logging and authorization behaviour, not SQL, locking or isolation.
/// </summary>
internal sealed class PaymentHttpHost : IAsyncDisposable
{
    public const string FakeSignature = "SIGNATURE_MUST_NOT_BE_LOGGED_1234567890";

    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public CapturingLoggerProvider Logs { get; } = new();
    public InMemoryOrderStore Store { get; } = new("USD", 91m);
    public InMemoryAnomalyRecorder Anomalies { get; } = new();
    /// <summary>The scripted workflow store behind the admin endpoints (unscripted calls fail loudly).</summary>
    public DispatchFake Workflow { get; private set; } = null!;
    public ConfigurableGateway Gateway { get; } = new();
    public StubHandler StripeHttp { get; } = new();
    public StubHandler PaymobHttp { get; } = new();
    public FixedClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public static async Task<PaymentHttpHost> StartAsync(
        bool redaction = true, string environment = "Development", bool stripe = true, bool paymob = false, bool verboseFramework = false,
        int financialWritesPerMinute = 100_000)
    {
        var host = new PaymentHttpHost();
        await host.BuildAsync(redaction, environment, stripe, paymob, verboseFramework, financialWritesPerMinute);
        return host;
    }

    private async Task BuildAsync(bool redaction, string environment, bool stripe, bool paymob, bool verboseFramework, int financialWritesPerMinute)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(Logs);
        // Same level the real host uses per environment (appsettings.Development.json: Information; appsettings.json: Warning),
        // raised to Information for production too: the redaction must hold even when someone turns request logging up.
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        if (verboseFramework)
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Trace);

        var services = builder.Services;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), "pulsereserve-test-keys"), ["Security:ForwardedHeaders:Mode"] = "Direct" }).Build();
        services.AddSingleton(SecurityOptions.Resolve(config, builder.Environment));
        // Only needed so the unrelated tickets route can be built; it is never called here.
        var workflowStore = DispatchFake.Create<IWorkflowStore>(out var workflow);
        Workflow = workflow;
        services.AddSingleton(workflowStore);
        services.AddDataProtection();
        services.AddAntiforgery(a => a.HeaderName = SecurityOptions.CsrfHeaderName);
        services.AddAuthentication(HeaderAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Customer, p => p.RequireAuthenticatedUser().RequireRole(AuthPolicies.CustomerRole))
            .AddPolicy(AuthPolicies.Admin, p => p.RequireAuthenticatedUser().RequireRole(AuthPolicies.AdminRole));
        services.AddSingleton<CsrfValidationFilter>();
        // The same policy the real host registers; the limit is per signed-in user.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddFinancialWriteRateLimit(financialWritesPerMinute);
        });

        var providers = new List<IPaymentProvider>();
        if (stripe)
            providers.Add(new StripePaymentProvider(TestProviders.StripeOptionsFor("USD"), new HttpClient(StripeHttp) { BaseAddress = new Uri("https://api.stripe.com/") }, Clock));
        if (paymob)
            providers.Add(new PaymobPaymentProvider(TestProviders.PaymobOptionsFor("USD"), new HttpClient(PaymobHttp) { BaseAddress = new Uri("https://accept.paymob.com/") }));
        var registry = new PaymentProviderRegistry(providers);
        services.AddSingleton(Store);
        services.AddSingleton<IOrderStore>(Store);
        services.AddSingleton<IPaymentAttemptLookup>(new InMemoryAttemptLookup(Store));
        services.AddSingleton<IReservationCheckoutGateway>(Gateway);
        services.AddSingleton<IPaymentProviderRegistry>(registry);
        services.AddSingleton<PaymentService>();
        services.AddSingleton<CheckoutService>();
        services.AddSingleton(Anomalies);
        services.AddSingleton<IPaymentAnomalyRecorder>(Anomalies);
        services.AddSingleton<PaymentWebhookService>();
        services.AddSingleton<PaymentOperationsService>();
        services.AddSingleton(new WorkflowOptions());
        services.AddSingleton<INotificationSender, SilentNotificationSender>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<RefundService>();
        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<PaymentsWorkflow>();
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        if (redaction)
            services.AddWebhookQueryRedaction();

        App = builder.Build();
        if (!builder.Environment.IsDevelopment())
            App.UseProblemOnUnhandledException(); // the same hardening the real host applies outside Development
        App.UseAuthentication();
        App.UseRateLimiter();
        App.UseAuthorization();
        App.MapOrderApi(new PaymentsOptions(false, false, false, TimeSpan.FromSeconds(15), new WorkflowOptions()));
        App.MapPaymentProviderApi();
        App.MapPaymentOperationsApi();
        App.MapGet("/test/csrf", (HttpContext http) => Results.Ok(new { token = http.IssueCsrfToken() })).RequireAuthorization(AuthPolicies.Customer);
        App.MapGet("/test/csrf-admin", (HttpContext http) => Results.Ok(new { token = http.IssueCsrfToken() })).RequireAuthorization(AuthPolicies.Admin);
        await App.StartAsync();
        Client = ((TestServer)App.Services.GetRequiredService<IServer>()).CreateClient();
    }

    public Guid CustomerId => Store.CustomerId;

    /// <summary>A valid antiforgery cookie + header pair for the customer (the same mechanism the SPA uses).</summary>
    public async Task<(string Cookie, string Token)> CsrfAsync(Guid? user = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test/csrf");
        request.Headers.Add("X-Test-User", (user ?? CustomerId).ToString());
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        return (cookie, token);
    }

    /// <summary>POST an admin payments route as the given user; admin = signed in with the Admin role.</summary>
    public async Task<HttpResponseMessage> AdminPostAsync(
        string path, string? body, bool signedIn = true, bool admin = true, bool csrf = true, string? contentType = "application/json", Guid? asUser = null)
    {
        var user = asUser ?? Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (signedIn)
        {
            request.Headers.Add("X-Test-User", user.ToString());
            if (admin)
                request.Headers.Add("X-Test-Admin", "1");
        }

        if (csrf)
        {
            using var issue = new HttpRequestMessage(HttpMethod.Get, "/test/csrf-admin");
            issue.Headers.Add("X-Test-User", user.ToString());
            issue.Headers.Add("X-Test-Admin", "1");
            using var response = await Client.SendAsync(issue);
            response.EnsureSuccessStatusCode();
            request.Headers.Add("Cookie", string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
            request.Headers.Add(SecurityOptions.CsrfHeaderName, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = contentType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>POST /api/orders/{id}/payments as the signed-in customer with a valid CSRF pair; the body is sent exactly as given.</summary>
    public async Task<HttpResponseMessage> StartPaymentAsync(
        string? body, string? contentType = "application/json", string? key = "key-1", Guid? orderId = null, bool csrf = true, Guid? user = null, bool signedIn = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId ?? Store.Order.OrderId}/payments");
        if (signedIn)
            request.Headers.Add("X-Test-User", (user ?? CustomerId).ToString());
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        if (csrf)
        {
            var (cookie, token) = await CsrfAsync(user);
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add(SecurityOptions.CsrfHeaderName, token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = contentType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        return await Client.SendAsync(request);
    }

    public void StripeBehaves()
    {
        StripeHttp.Respond = r => StubHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            id = "cs_test_http", status = "open", payment_status = "unpaid", url = "https://checkout.stripe.com/c/pay/cs_test_http",
            amount_total = long.Parse(r.Form()["line_items[0][price_data][unit_amount]"]), currency = r.Form()["line_items[0][price_data][currency]"],
            client_reference_id = r.Form()["client_reference_id"]
        }));
    }

    public WebhookRequestBuilder Webhook => new(this);

    public sealed class WebhookRequestBuilder(PaymentHttpHost host)
    {
        /// <summary>A correctly signed Stripe success event for the attempt.</summary>
        public HttpRequestMessage Stripe(
            InMemoryOrderStore.AttemptRow attempt, long amount = 9100, string? signature = null, string contentType = "application/json",
            string eventId = "evt_http_1", string type = "checkout.session.completed", bool livemode = false, string paymentStatus = "paid")
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = eventId, ["type"] = type, ["livemode"] = livemode,
                ["data"] = new { @object = new Dictionary<string, object?> { ["id"] = attempt.Reference, ["payment_status"] = paymentStatus, ["amount_total"] = amount, ["currency"] = "usd", ["client_reference_id"] = attempt.Id.ToString(), ["metadata"] = new { attempt_id = attempt.Id.ToString(), order_id = host.Store.Order.OrderId.ToString() } } }
            });
            return StripeBody(body, signature, contentType);
        }

        /// <summary>A Stripe request carrying exactly this body, correctly signed (unless a signature is given).</summary>
        public HttpRequestMessage StripeBody(string body, string? signature = null, string contentType = "application/json")
        {
            var t = host.Clock.Now.ToUnixTimeSeconds();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StringContent(body, Encoding.UTF8, contentType) };
            request.Headers.Add("Stripe-Signature", signature ?? $"t={t},v1={StripeSignature.Compute(body, t, TestProviders.StripeWebhookSecret)}");
            return request;
        }

        /// <summary>An attempt this system never created (another environment on the same provider account, a deleted test).</summary>
        public InMemoryOrderStore.AttemptRow Phantom() =>
            new() { Id = Guid.NewGuid(), OrderId = host.Store.Order.OrderId, Number = 1, Key = "phantom", Provider = "Stripe", Reference = "cs_phantom" };

        /// <summary>A Paymob transaction callback whose HMAC is delivered, as documented, in the query string.</summary>
        public HttpRequestMessage Paymob(InMemoryOrderStore.AttemptRow attempt, long cents = 9100, string? hmac = null)
        {
            var obj = new JsonObject
            {
                ["id"] = 192036465, ["pending"] = false, ["amount_cents"] = cents, ["success"] = true, ["is_auth"] = false, ["is_capture"] = false,
                ["is_standalone_payment"] = true, ["is_voided"] = false, ["is_refunded"] = false, ["is_3d_secure"] = true, ["integration_id"] = 4097558,
                ["has_parent_transaction"] = false, ["created_at"] = "2030-01-01T11:33:44.592345", ["currency"] = "USD", ["error_occured"] = false, ["owner"] = 302852,
                ["order"] = new JsonObject { ["id"] = long.Parse(attempt.Reference!), ["merchant_order_id"] = attempt.Id.ToString() },
                ["source_data"] = new JsonObject { ["type"] = "card", ["pan"] = "2346", ["sub_type"] = "MasterCard" }
            };
            var computed = PaymobHmac.Compute(JsonDocument.Parse(obj.ToJsonString()).RootElement, TestProviders.PaymobHmacSecret);
            var body = new JsonObject { ["type"] = "TRANSACTION", ["obj"] = obj }.ToJsonString();
            return new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/paymob?hmac={hmac ?? computed}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

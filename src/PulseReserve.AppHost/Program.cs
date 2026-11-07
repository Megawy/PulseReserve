using PulseReserve.AppHost.Api;
using PulseReserve.AppHost.Composition;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PulseReserve.AppHost.Configuration;
using PulseReserve.AppHost.Health;
using PulseReserve.AppHost.Live;
using PulseReserve.AppHost.Security;
using PulseReserve.Modules.Identity;
using PulseReserve.Modules.Payments;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Infrastructure.Persistence.Catalog;
using PulseReserve.Modules.Reservation;
using PulseReserve.Modules.Reservation.Features.Inventory;

var builder = WebApplication.CreateBuilder(args);

// Explicit composition-root registration anchors for modules and infrastructure
_ = PulseReserve.Modules.Identity.IdentityModule.Name;
_ = PulseReserve.Modules.Catalog.CatalogModule.Name;
_ = PulseReserve.Modules.Reservation.ReservationModule.Name;
_ = PulseReserve.Modules.Payments.PaymentsModule.Name;
_ = PulseReserve.Modules.Notifications.NotificationsModule.Name;
_ = PulseReserve.Infrastructure.InfrastructureModule.Name;
_ = PulseReserve.SharedKernel.SharedKernelModule.Name;

// Register Catalog persistence (DbContext, repositories, handlers)
// Configuration wins over the legacy PULSERESERVE_TEST_DATABASE fallback (see CatalogConnectionString).
var catalogConnection = CatalogConnectionString.Resolve(builder.Configuration, Environment.GetEnvironmentVariable);
builder.Services.AddCatalogPersistence(catalogConnection);

// Register the Reservation module (own "reservation" schema in the same database) and its Catalog adapter.
var holdDurationSeconds = builder.Configuration.GetValue<int?>("Reservation:HoldDurationSeconds");
builder.Services.AddReservationModule(catalogConnection, options =>
{
    if (holdDurationSeconds is { } seconds)
        options.HoldDuration = TimeSpan.FromSeconds(seconds);
});
builder.Services.AddScoped<IEventSalesCatalog, CatalogEventSalesAdapter>();

// Payments module (orders, payment attempts; own "payments" schema). The simulator is development/test only and
// startup fails if it is enabled anywhere else (see PaymentsOptions).
var paymentsOptions = PaymentsOptions.Resolve(builder.Configuration, builder.Environment);
builder.Services.AddPaymentsModule(catalogConnection, paymentsOptions.SimulatorEnabled, paymentsOptions.Workflow);
builder.Services.AddSingleton(paymentsOptions);
// Real providers (Stripe, Paymob): off unless enabled; an invalid configuration makes only that provider unavailable.
var providerStatuses = builder.Services.AddRealPaymentProviders(builder.Configuration, builder.Environment);
// Webhook query strings (Paymob's signature) must never reach logs or traces.
builder.Services.AddWebhookQueryRedaction();
if (paymentsOptions.DevNotificationsEnabled)
    builder.Services.AddSingleton<INotificationSender, DevelopmentNotificationSender>();
else
    builder.Services.AddSingleton<INotificationSender, PulseReserve.Modules.Payments.Providers.UnavailableNotificationSender>();
if (paymentsOptions.WorkersEnabled)
    builder.Services.AddHostedService<PaymentsWorker>();
builder.Services.AddScoped<IReservationCheckoutGateway, ReservationCheckoutAdapter>();

// Identity module (own "identity" schema) and same-origin cookie authentication. Fails at startup if required
// production settings (see SecurityOptions) are missing.
builder.Services.AddIdentityModule(catalogConnection);
var security = builder.AddPulseReserveSecurity();

// Real-time updates (SignalR hub fed by the database's commit notifications) and proactive hold expiry. Single-instance in-process
// fan-out: see docs/architecture/28-real-time-architecture.md for the guarantees, the recovery behaviour and the scale-out path.
var live = builder.AddPulseReserveLive(catalogConnection);

// Liveness (process only) and readiness (database) probes
builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"]);

// OpenAPI / Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// First in the pipeline: honour X-Forwarded-For/-Proto only from the explicitly configured proxies (see ForwardedHeadersSettings).
app.UsePulseReserveForwardedHeaders(security.ForwardedHeaders);
app.Logger.LogStatuses(providerStatuses);
if (paymentsOptions.SimulatorEnabled)
    app.Logger.LogWarning("The payment SIMULATOR is enabled in the {Environment} environment: payments are fake and no real money moves.", app.Environment.EnvironmentName);

await PulseReserve.AppHost.Composition.DevelopmentMigrations.ApplyIfRequestedAsync(app);

// Swagger UI (development only)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// An unexpected failure answers with a minimal problem document and never framework detail (Development keeps the developer
// page). A webhook that fails here is answered with a 5xx, which makes the provider redeliver it.
if (!app.Environment.IsDevelopment())
    app.UseProblemOnUnhandledException();

// Authentication first: the financial write limit is per signed-in user.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UsePulseReserveLive(live);

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// Health check root
app.MapGet("/", () => Results.Ok(new
{
    Application = "PulseReserve",
    Status = "Healthy",
    Version = "1.0.0-phase2d"
}));

// Identity API boundary (register, login, logout, session)
app.MapIdentityApi();

// Catalog API boundary
app.MapCatalogApi();

// Reservation API boundary (seat availability and holds)
app.MapReservationApi();

// Orders, checkout and payments (customer-only; the simulator route exists only when explicitly enabled)
app.MapOrderApi(paymentsOptions);

// Provider webhooks (signature-verified, anonymous by nature) and the customer payment options
app.MapPaymentProviderApi();

// Trusted payment operations (Admin only)
app.MapPaymentOperationsApi();

// Real-time hub (/hubs/live) and the administrator-only status of the real-time layer
app.MapPulseReserveLive(live);

app.Run();

public partial class Program
{
    protected Program() { }
}

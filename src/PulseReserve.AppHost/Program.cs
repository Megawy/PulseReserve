var builder = WebApplication.CreateBuilder(args);

// Explicit composition-root registration anchors for modules and infrastructure
_ = PulseReserve.Modules.Identity.IdentityModule.Name;
_ = PulseReserve.Modules.Catalog.CatalogModule.Name;
_ = PulseReserve.Modules.Reservation.ReservationModule.Name;
_ = PulseReserve.Modules.Payments.PaymentsModule.Name;
_ = PulseReserve.Modules.Notifications.NotificationsModule.Name;
_ = PulseReserve.Infrastructure.InfrastructureModule.Name;
_ = PulseReserve.SharedKernel.SharedKernelModule.Name;

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    Application = "PulseReserve",
    Status = "Healthy",
    Version = "1.0.0-phase0"
}));

app.Run();

public partial class Program
{
    protected Program() { }
}

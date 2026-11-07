using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PulseReserve.Modules.Reservation.Features.Checkout;
using PulseReserve.Modules.Reservation.Features.Holds.HoldSeats;
using PulseReserve.Modules.Reservation.Features.Holds.ManageReservation;
using PulseReserve.Modules.Reservation.Features.Inventory;
using PulseReserve.Modules.Reservation.Features.Live;
using PulseReserve.Modules.Reservation.Persistence;
using PulseReserve.Modules.Reservation.Persistence.Postgres;

namespace PulseReserve.Modules.Reservation;

public static class ReservationModuleExtensions
{
    /// <summary>
    /// Registers the Reservation module. The host must also register an <see cref="IEventSalesCatalog"/> adapter.
    /// </summary>
    public static IServiceCollection AddReservationModule(
        this IServiceCollection services,
        string connectionString,
        Action<ReservationOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new ReservationOptions();
        configure?.Invoke(options);
        if (options.HoldDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("Reservation hold duration must be positive.");

        services.AddSingleton(options);
        services.AddDbContext<ReservationDbContext>(db => db.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ReservationDbContext.Schema)));

        // One store instance per scope serves both the booking path and the real-time/expiry operations.
        services.AddScoped<ReservationStore>();
        services.AddScoped<IReservationStore>(sp => sp.GetRequiredService<ReservationStore>());
        services.AddScoped<IReservationLiveStore>(sp => sp.GetRequiredService<ReservationStore>());
        services.AddScoped<ReservationLiveReader>();
        services.AddScoped<HoldExpiryService>();
        services.AddScoped<EventInventoryProvisioner>();
        services.AddScoped<HoldSeatsHandler>();
        services.AddScoped<ReservationQueriesHandler>();
        services.AddScoped<ReservationCheckoutService>();
        services.AddScoped<GetEventAvailabilityHandler>();
        return services;
    }
}

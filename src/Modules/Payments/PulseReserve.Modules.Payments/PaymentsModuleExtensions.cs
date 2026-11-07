using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PulseReserve.Modules.Payments.Features;
using PulseReserve.Modules.Payments.Persistence;
using PulseReserve.Modules.Payments.Persistence.Postgres;
using PulseReserve.Modules.Payments.Providers;

namespace PulseReserve.Modules.Payments;

public static class PaymentsModuleExtensions
{
    /// <summary>
    /// Registers orders and payments (own "payments" schema). The host must also register an
    /// <see cref="IReservationCheckoutGateway"/> adapter. With <paramref name="useSimulator"/> false (the default) no
    /// provider can take payments.
    /// </summary>
    public static IServiceCollection AddPaymentsModule(
        this IServiceCollection services, string connectionString, bool useSimulator, WorkflowOptions? workflowOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<PaymentsDbContext>(db => db.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", PaymentsDbContext.Schema)));

        services.AddSingleton(workflowOptions ?? new WorkflowOptions());
        services.AddScoped<IOrderStore, OrderStore>();
        services.AddScoped<IWorkflowStore, WorkflowStore>();
        services.AddScoped<IPaymentsLiveStore, PaymentsLiveStore>();
        services.AddScoped<PulseReserve.Modules.Payments.Features.Live.PaymentsLiveReader>();
        services.AddScoped<IPaymentAttemptLookup>(sp => sp.GetRequiredService<IWorkflowStore>());
        services.AddScoped<IPaymentAnomalyRecorder>(sp => sp.GetRequiredService<IWorkflowStore>());
        services.AddScoped<CheckoutService>();
        services.AddSingleton<IPaymentProviderRegistry>(sp => new PaymentProviderRegistry(sp.GetServices<IPaymentProvider>()));
        services.AddScoped(sp => new PaymentService(
            sp.GetRequiredService<IOrderStore>(), sp.GetRequiredService<IPaymentProviderRegistry>(), sp.GetRequiredService<IReservationCheckoutGateway>()));
        services.AddScoped<PaymentWebhookService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<RefundService>();
        services.AddScoped<OutboxProcessor>();
        services.AddScoped<PaymentsWorkflow>();
        services.AddScoped<PaymentOperationsService>();

        if (useSimulator)
            services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();
        else
            services.AddSingleton<IPaymentProvider, UnavailablePaymentProvider>();

        return services;
    }
}

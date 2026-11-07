using PulseReserve.AppHost.Configuration;
using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.AppHost.Composition;

/// <summary>
/// Runs the durable payment workflows (reconciliation, refunds, outbox) inside the host. All coordination lives in
/// PostgreSQL (SKIP LOCKED claims with leases), so several hosts may run it at once and a restart loses nothing; this
/// loop only decides how often to look. A failing pass is logged (type only) and never stops the loop.
/// </summary>
public sealed class PaymentsWorker(IServiceScopeFactory scopes, PaymentsOptions options, ILogger<PaymentsWorker> logger) : BackgroundService
{
    /// <summary>How often the durable count of unresolved payment items is looked at (cheap indexed counts; not every pass).</summary>
    internal static readonly TimeSpan BacklogCheckEvery = TimeSpan.FromMinutes(1);

    /// <summary>While anything stays unresolved, it is repeated in the log at most this often (a new item is logged at once).</summary>
    internal static readonly TimeSpan BacklogReminderEvery = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short random start offset keeps several hosts from polling in lockstep. It is also load-bearing for the integration test
        // safety net: the test host guard verifies which database every module targets right after the host starts, so the first
        // database call of this worker must not happen earlier than that (see CatalogApiFactory). Do not remove or shorten it.
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(200, 2000)), stoppingToken).ConfigureAwait(false);

        var backlog = new UnresolvedPaymentItemsReporter(logger, TimeProvider.System, BacklogReminderEvery);
        var lastBacklogCheck = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<PaymentsWorkflow>().RunOnceAsync(stoppingToken);
                if (result.TasksProcessed + result.RefundsProcessed + result.OutboxProcessed > 0)
                    logger.LogInformation("Payments workflow pass: {Result}", result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("Payments workflow pass failed: {ErrorType}", ex.GetType().Name);
            }

            if (DateTimeOffset.UtcNow - lastBacklogCheck >= BacklogCheckEvery)
            {
                lastBacklogCheck = DateTimeOffset.UtcNow;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    backlog.Observe(await scope.ServiceProvider.GetRequiredService<PaymentOperationsService>().SummaryAsync(stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Payments backlog check failed: {ErrorType}", ex.GetType().Name);
                }
            }

            await Task.Delay(options.WorkerInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// DEVELOPMENT/TEST ONLY notification adapter. It delivers nothing; it logs the order id and the number of tickets
/// (no customer data). Enabled only with <c>Notifications:DevAdapter:Enabled</c>, which production refuses.
/// </summary>
public sealed class DevelopmentNotificationSender(ILogger<DevelopmentNotificationSender> logger) : INotificationSender
{
    public string Channel => "DevelopmentLog";
    public bool IsAvailable => true;

    public Task SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[dev notification, nothing delivered] {Kind} for order {OrderId} ({Tickets} tickets)", request.Kind, request.OrderId, request.TicketCount);
        return Task.CompletedTask;
    }
}

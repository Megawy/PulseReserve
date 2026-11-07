using PulseReserve.Modules.Reservation.Features.Live;

namespace PulseReserve.AppHost.Live;

/// <summary>
/// Closes holds that ran out, so that the end of a hold is a committed fact (and therefore announced) at the moment it happens, not only
/// the next time somebody reads it. The booking path never depends on it: an expired hold is already reported as expired and its seats are
/// already reclaimable (lazy expiry). Several hosts may run it at once: the work is claimed with <c>FOR UPDATE SKIP LOCKED</c>.
/// Only <c>Held</c> reservations are closed; one in <c>Checkout</c> belongs to the payment workflow, which owns its order.
/// A failing pass is logged by type only and never stops the loop.
/// </summary>
public sealed class HoldExpiryWorker(IServiceScopeFactory scopes, LiveOptions options, ILogger<HoldExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short random start offset keeps several hosts from polling in lockstep. It is also load-bearing for the integration test
        // safety net (the first database call must not precede the target-database guard); do not remove or shorten it.
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(200, 2000)), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("Hold expiry pass failed: {ErrorType}", ex.GetType().Name);
            }

            await Task.Delay(options.HoldExpiryInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Closes due holds in batches until a batch comes back short. Public so that tests can drive one pass.</summary>
    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var closed = await scope.ServiceProvider.GetRequiredService<HoldExpiryService>().ExpireDueAsync(options.HoldExpiryBatch, cancellationToken);
            total += closed;
            if (closed < options.HoldExpiryBatch)
                break;
        }

        if (total > 0)
            logger.LogInformation("Closed {Count} expired hold(s); their seats are available again.", total);
        return total;
    }
}

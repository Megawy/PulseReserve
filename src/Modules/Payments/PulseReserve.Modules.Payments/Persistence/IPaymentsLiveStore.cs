using PulseReserve.Modules.Payments.Features.Live;

namespace PulseReserve.Modules.Payments.Persistence;

/// <summary>
/// The read behind real-time order updates. It returns the CURRENT state of an order from the database at call time, with the same
/// effective-status rule as the order API (a pending order past its deadline reads as <c>Expired</c>), so what is pushed can never
/// be richer or newer than what the customer's own GET would return.
/// </summary>
public interface IPaymentsLiveStore
{
    /// <summary><c>null</c> when the order does not exist.</summary>
    Task<OrderLiveSnapshot?> ReadOrderLiveSnapshotAsync(Guid orderId, CancellationToken cancellationToken = default);
}

using PulseReserve.Modules.Payments.Persistence;

namespace PulseReserve.Modules.Payments.Features.Live;

/// <summary>
/// What a customer is already allowed to see about their own order: its effective status (including <c>RefundRequired</c> and
/// <c>Refunded</c>), the status of the latest payment attempt, and how many tickets exist. No amounts, providers, references, refund
/// records or evidence: those stay behind the authenticated GETs and the administrator console.
/// </summary>
public sealed record OrderLiveSnapshot(
    Guid OrderId,
    Guid CustomerId,
    Guid ReservationId,
    string Status,
    string? PaymentStatus,
    int TicketCount);

/// <summary>Public read surface for the composition root's real-time dispatcher.</summary>
public sealed class PaymentsLiveReader(IPaymentsLiveStore store)
{
    public Task<OrderLiveSnapshot?> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        store.ReadOrderLiveSnapshotAsync(orderId, cancellationToken);
}

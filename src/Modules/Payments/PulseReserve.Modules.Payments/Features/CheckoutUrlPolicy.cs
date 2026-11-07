namespace PulseReserve.Modules.Payments.Features;

/// <summary>
/// When the provider-hosted checkout URL may be shown to the customer. It can embed a provider client secret, so it is
/// returned only to the order's owner (the callers are owner-only endpoints) and only while it can still lead to a
/// payment: a REAL provider's attempt that is still in flight on an order that is still awaiting payment. A failed,
/// cancelled or succeeded attempt, an expired or cancelled order, and the simulator never get a link.
/// </summary>
public static class CheckoutUrlPolicy
{
    public static string? Visible(string orderStatus, PaymentAttempt? attempt) =>
        attempt is { IsSimulated: false, Status: Domain.PaymentAttemptStatus.Initiated, CheckoutUrl: { Length: > 0 } url }
        && orderStatus == Domain.OrderStatus.PendingPayment
            ? url
            : null;
}

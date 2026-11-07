namespace PulseReserve.Modules.Payments.Features;

// ───────────── Read models (what the API returns) ─────────────

/// <summary>One order line: a single seat (quantity is always 1) at its immutable snapshot price.</summary>
public sealed record OrderLine(Guid SeatId, int Quantity, decimal UnitPrice, string Currency, decimal LineTotal);

public sealed record PaymentAttempt(
    Guid AttemptId,
    int AttemptNumber,
    string Status,
    string Provider,
    bool IsSimulated,
    string? ProviderReference,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    string? CheckoutUrl = null);

public sealed record Order(
    Guid OrderId,
    Guid ReservationId,
    Guid EventId,
    Guid CustomerId,
    string Status,
    string Currency,
    decimal TotalAmount,
    DateTime CreatedAtUtc,
    DateTime PaymentDeadlineUtc,
    IReadOnlyList<OrderLine> Lines,
    PaymentAttempt? Payment);

// ───────────── Ports implemented outside the module ─────────────

/// <summary>A reservation's seats and snapshot prices as the Reservation module recorded them.</summary>
public sealed record ReservationLine(Guid SeatId, decimal UnitPrice);

public sealed record ReservationSnapshot(
    Guid ReservationId, Guid EventId, string Currency, DateTime ExpiresAtUtc, IReadOnlyList<ReservationLine> Lines);

public enum CheckoutStart { Ready, NotFound, NotActive }

public sealed record CheckoutStartResult(CheckoutStart Status, ReservationSnapshot? Snapshot);

public enum SaleResult { Confirmed, Expired, NotConfirmable }

/// <summary>
/// Port to the Reservation module's atomic transitions (adapted in the composition root; this module never touches
/// reservation tables). Expiry is decided by the database clock inside those transitions.
/// </summary>
public interface IReservationCheckoutGateway
{
    /// <summary>Held -> Checkout under a row lock, or resume if already in Checkout. Never succeeds for an expired hold.</summary>
    Task<CheckoutStartResult> BeginAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>Checkout -> Confirmed (seats Sold) unless the hold expired first. Idempotent for an already confirmed sale.</summary>
    Task<SaleResult> ConfirmSaleAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>Ends a Checkout (order cancelled): seats return to the pool. Idempotent.</summary>
    Task ReleaseAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default);
}

/// <summary>What the provider says about a payment attempt when asked (never inferred from silence).</summary>
public enum ProviderPaymentState { Pending, Succeeded, Failed, NotFound }

/// <summary>What the provider says about a refund submission when asked by its idempotency key.</summary>
public enum ProviderRefundState { NotFound, Pending, Succeeded, Failed }

public sealed record ProviderRefundRequest(
    Guid RefundId, string IdempotencyKey, string PaymentProviderReference, decimal Amount, string Currency);

/// <summary>
/// Definitive answer to a refund submission. <c>Accepted</c> with <c>Completed</c> true = money returned;
/// <c>Accepted</c> with false = the provider will finish later (poll). <c>Rejected</c> = definitively no refund was
/// made; <c>Retryable</c> says whether submitting again can succeed. An ambiguous outcome (timeout, connection loss,
/// unreadable answer) is NOT a result: the provider adapter throws, and the workflow treats it as Unknown.
/// </summary>
public sealed record ProviderRefundResult(bool Accepted, bool Completed, string? ProviderRefundId, string? RejectionCode, bool Retryable);

public sealed record PaymentInitiation(Guid AttemptId, Guid OrderId, decimal Amount, string Currency);

/// <summary>
/// What a provider returned for a new attempt. <paramref name="CheckoutUrl"/> is the provider-hosted page the customer
/// is sent to (null for the simulator). It is persisted with the reference so an idempotent replay returns the same URL.
/// </summary>
public sealed record ProviderSession(string ProviderReference, string? CheckoutUrl = null);

/// <summary>What the reconciliation worker knows when it asks a provider about an attempt: the server-side order facts to verify against.</summary>
public sealed record PaymentStatusQuery(Guid AttemptId, string? ProviderReference, Guid OrderId, decimal Amount, string Currency);

/// <summary>What the refund worker knows when it asks a provider about a refund it may already have submitted.</summary>
public sealed record RefundStatusQuery(string IdempotencyKey, Guid RefundId, string? PaymentProviderReference);

/// <summary>
/// The provider DEFINITIVELY refused to start a payment (for example invalid credentials or an unsupported currency):
/// nothing was created on its side. Any other exception from <see cref="IPaymentProvider.InitiateAsync"/> (timeout,
/// connection loss, an unreadable answer) is ambiguous and must never be treated as a refusal.
/// </summary>
public sealed class ProviderRejectedException(string code) : Exception($"The payment provider refused the request ({code}).")
{
    public string Code { get; } = code;
}

/// <summary>
/// Payment provider abstraction. A real provider is added as another adapter; the order domain never learns provider
/// specifics. <see cref="InitiateAsync"/> must be idempotent per <see cref="PaymentInitiation.AttemptId"/>.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    /// <summary>True for fake providers: such payments never represent real money.</summary>
    bool IsSimulated { get; }

    /// <summary>False when nothing can take a payment (e.g. production without a configured provider).</summary>
    bool IsAvailable { get; }

    Task<ProviderSession> InitiateAsync(PaymentInitiation initiation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the provider for the real status of an attempt (reconciliation). Throws when it cannot get a definitive
    /// answer; callers must treat that as "unknown", never as success or failure.
    /// </summary>
    Task<ProviderPaymentState> GetPaymentStatusAsync(Guid attemptId, string? providerReference, CancellationToken cancellationToken = default);

    /// <summary>Submits a refund. Idempotent per <see cref="ProviderRefundRequest.IdempotencyKey"/>. Throws when ambiguous.</summary>
    Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>Looks up a refund submission by its idempotency key. Throws when it cannot get a definitive answer.</summary>
    Task<ProviderRefundState> GetRefundStatusAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Whether this provider can charge in <paramref name="currency"/> (the server-side configuration decides, never the client).</summary>
    bool SupportsCurrency(string currency) => true;

    /// <summary>False when the provider offers no verified way to ask about a payment; such attempts resolve only through a verified webhook or an operator.</summary>
    bool SupportsPaymentLookup => true;

    /// <summary>False when refunds cannot be made through the API; such refunds go to an operator (NeedsReview) and are never marked complete automatically.</summary>
    bool SupportsRefunds => true;

    /// <summary>
    /// False when the provider cannot deduplicate a repeated initiation (no idempotency key, as with Paymob): a second call for the
    /// same attempt may create a second payment session there. Such a provider is called AT MOST ONCE per attempt; after an
    /// ambiguous answer the attempt is closed and the customer starts again with a new attempt, instead of this system retrying
    /// blindly.
    /// </summary>
    bool IdempotentInitiation => true;

    /// <summary>Provider status lookup with the order facts to verify (amount, currency). Defaults to the basic lookup.</summary>
    Task<ProviderPaymentState> GetPaymentStatusAsync(PaymentStatusQuery query, CancellationToken cancellationToken = default) =>
        GetPaymentStatusAsync(query.AttemptId, query.ProviderReference, cancellationToken);

    /// <summary>Refund lookup with the context a provider may need to find it. Defaults to the lookup by idempotency key.</summary>
    Task<ProviderRefundState> GetRefundStatusAsync(RefundStatusQuery query, CancellationToken cancellationToken = default) =>
        GetRefundStatusAsync(query.IdempotencyKey, cancellationToken);
}

/// <summary>Port for telling a customer about their order. Adapters are provided by the host (no paid service required).</summary>
public interface INotificationSender
{
    string Channel { get; }

    /// <summary>False when nothing can deliver notifications (e.g. production without a configured adapter).</summary>
    bool IsAvailable { get; }

    /// <summary>Sends one notification. Throws on failure; a failure never affects the purchase.</summary>
    Task SendAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Deliberately free of personal data: the adapter resolves how to reach the customer.</summary>
public sealed record NotificationRequest(Guid CustomerId, Guid OrderId, string Kind, int TicketCount);

public sealed record Ticket(Guid TicketId, string PublicId, Guid OrderId, Guid SeatId, Guid EventId, string Status, DateTime IssuedAtUtc);

/// <summary>A provider's verdict about one attempt (a real provider would deliver it by signed webhook).</summary>
public sealed record PaymentCallback(Guid AttemptId, Guid OrderId, string ProviderReference, bool Succeeded, string? FailureCode, string? ProviderEventId = null);

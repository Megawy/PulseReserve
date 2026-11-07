using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.Modules.Payments.Persistence;

public sealed record NewOrder(Guid OrderId, Guid CustomerId, string IdempotencyKey, ReservationSnapshot Snapshot);

public enum CreateOrderOutcome
{
    Created,

    /// <summary>(customer, idempotency key) already has an order: decided by a unique index.</summary>
    KeyAlreadyUsed,

    /// <summary>The reservation already has its one order: decided by a unique index.</summary>
    ReservationAlreadyHasOrder
}

public enum BeginPaymentOutcome { Started, Replay, OrderNotFound, NotPayable, InProgress, HeldForReview }

public sealed record BeginPaymentResult(BeginPaymentOutcome Outcome, Order? Order, PaymentAttempt? Attempt);

public enum RecordAttemptOutcome
{
    Recorded,
    AlreadyRecordedSame,
    ConflictingOutcome,
    NotFound,

    /// <summary>A verified success arrived for an attempt recorded as Failed: the verdict is kept and an exception was created now.</summary>
    LateSuccessRecorded,

    /// <summary>The same situation again: the exception already existed, nothing was added.</summary>
    LateSuccessAlreadyRecorded
}

public enum CancelOrderOutcome { Cancelled, NotCancellable, NotFound }

public sealed record CancelOrderResult(CancelOrderOutcome Outcome, Order? Order);

/// <summary>
/// Persistence boundary for orders and payment attempts. Every state change is one PostgreSQL transaction guarded by
/// the order's row lock; uniqueness (one order per reservation, one key per customer, one in-flight attempt per
/// order) is decided by the database, never by an application-level check.
/// </summary>
public interface IOrderStore
{
    Task<Order?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Order?> FindByReservationAsync(Guid reservationId, Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>Creates the order and its immutable line snapshot atomically.</summary>
    Task<CreateOrderOutcome> TryCreateAsync(NewOrder order, CancellationToken cancellationToken = default);

    /// <summary>The customer's own orders, newest first (bounded).</summary>
    Task<IReadOnlyList<Order>> ListForCustomerAsync(Guid customerId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Reads an order with its effective status (an overdue PendingPayment is reported as Expired). Null customer = system read.</summary>
    Task<Order?> GetAsync(Guid orderId, Guid? customerId, CancellationToken cancellationToken = default);

    Task<CancelOrderResult> CancelAsync(Guid orderId, Guid customerId, CancellationToken cancellationToken = default);

    Task<BeginPaymentResult> BeginPaymentAsync(
        Guid orderId, Guid customerId, string idempotencyKey, string provider, bool isSimulated, CancellationToken cancellationToken = default);

    /// <summary>Sets the provider reference once (idempotent; never overwrites).</summary>
    Task SetProviderReferenceAsync(Guid attemptId, string providerReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the provider reference TOGETHER with the hosted checkout URL, once (never overwrites either), so the
    /// attempt is durable before the customer is redirected and an idempotent replay returns the same URL.
    /// </summary>
    Task SetProviderSessionAsync(Guid attemptId, string providerReference, string? checkoutUrl, CancellationToken cancellationToken = default) =>
        SetProviderReferenceAsync(attemptId, providerReference, cancellationToken);

    /// <summary>The attempt this (order, key) already produced, if any: a replay always answers with that attempt and its provider.</summary>
    Task<PaymentAttempt?> FindAttemptByKeyAsync(Guid orderId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<PaymentAttempt?>(null);

    /// <summary>
    /// Cancels an Initiated attempt that never obtained a provider reference because the provider definitively refused
    /// to start it (nothing exists on the provider side). Never touches an attempt that has a reference.
    /// </summary>
    Task CancelUnreferencedAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Verifies the callback belongs to this order and attempt and records the provider's verdict exactly once. A success for an
    /// attempt already recorded as Failed does NOT rewrite it: the verdict stays and, in the same transaction, one payment
    /// exception is created (idempotent per attempt).
    /// </summary>
    Task<RecordAttemptOutcome> RecordAttemptOutcomeAsync(PaymentCallback callback, CancellationToken cancellationToken = default);

    /// <summary>True when the order has (or ever had) a payment exception: it can never be paid again.</summary>
    Task<bool> HasPaymentExceptionAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Closes an attempt that has no provider reference because the provider's answer to the initiation was AMBIGUOUS and the
    /// provider cannot deduplicate a repeat. Same effect as <see cref="CancelUnreferencedAttemptAsync"/> but recorded with its own
    /// failure code so it is distinguishable from a definitive refusal.
    /// </summary>
    Task CancelUncertainAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) => CancelUnreferencedAttemptAsync(attemptId, cancellationToken);

    /// <summary>Marks the order Paid, or RefundRequired when the seats could not be sold. Idempotent; never leaves a final state.</summary>
    Task FinalizeAfterSaleAsync(Guid orderId, bool sold, CancellationToken cancellationToken = default);
}

using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Payments.Domain;

/// <summary>
/// Order lifecycle (persisted in <c>payments.orders.status</c>).
/// <code>
/// PendingPayment --payment confirmed + seats sold--------------> Paid            (terminal)
/// PendingPayment --customer cancels----------------------------> Cancelled
/// PendingPayment --deadline passes (reported lazily, DB clock)-> Expired
/// PendingPayment | Cancelled | Expired
///     --provider reports success but the seats can no longer be sold--> RefundRequired
/// RefundRequired --the refund is confirmed by the provider----------------> Refunded       (terminal)
/// </code>
/// Paid and Refunded are final. A Paid order never changes state: refunding a paid purchase is a later, separate
/// business flow (it would also have to void tickets and decide on inventory).
/// The RefundRequired transition and its refund record are written in one transaction (see FinalizeAfterSaleAsync).
/// Likewise Paid is written together with its outbox events, so a paid order always has recoverable post-payment work.
/// A failed payment attempt does NOT change the order: it stays PendingPayment, so the customer may retry until the
/// deadline (which is the reservation's original expiry; it is never extended) or cancel.
/// </summary>
public static class OrderStatus
{
    public const string PendingPayment = "PendingPayment";
    public const string Paid = "Paid";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
    public const string RefundRequired = "RefundRequired";
    public const string Refunded = "Refunded";
}

/// <summary>
/// Payment attempt lifecycle. Initiated -> Succeeded | Failed | Cancelled. A Cancelled attempt may still become
/// Succeeded when the provider reports a late success (money moved), which then leads to RefundRequired.
/// </summary>
public static class PaymentAttemptStatus
{
    public const string Initiated = "Initiated";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// Refund lifecycle. Requested -> (intent recorded as Unknown BEFORE the provider is called) -> Pending | Succeeded |
/// Failed; an ambiguous provider answer leaves it Unknown, and Unknown is only ever resolved by QUERYING the provider
/// (never by blindly submitting again). Succeeded and Failed (a definitive refusal) are terminal for the worker;
/// NeedsReview means automatic attempts were exhausted and an administrator must look.
/// </summary>
public static class RefundState
{
    public const string Requested = "Requested";
    public const string Unknown = "Unknown";
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string NeedsReview = "NeedsReview";

    /// <summary>
    /// Terminal. An administrator recorded that the refund was performed OUTSIDE this system (in a provider dashboard whose
    /// API cannot refund or look refunds up). It is deliberately a different state from <see cref="Succeeded"/>, which means
    /// "the provider confirmed it": nothing here was confirmed by a provider, and no provider was called. Only reachable
    /// from NeedsReview, only for a provider that cannot refund through the API, only by an administrator.
    /// </summary>
    public const string ManuallyRecorded = "ManuallyRecorded";
}

/// <summary>Durable work item state shared by reconciliation tasks and outbox events.</summary>
public static class WorkState
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Done = "Done";
    public const string NeedsReview = "NeedsReview";
}

public static class ReconciliationKind
{
    /// <summary>A payment attempt succeeded but its order was never finalized (crash window). Subject = attempt id.</summary>
    public const string FinalizeSuccessfulPayment = "FinalizeSuccessfulPayment";

    /// <summary>An attempt stayed Initiated past the staleness threshold. Subject = attempt id.</summary>
    public const string ResolveStaleAttempt = "ResolveStaleAttempt";

    /// <summary>A PendingPayment order is past its deadline. Subject = order id.</summary>
    public const string ExpireOrder = "ExpireOrder";
}

public static class OutboxEventType
{
    public const string IssueTickets = "IssueTickets";
    public const string NotifyOrderPaid = "NotifyOrderPaid";
}

public static class RefundReason
{
    public const string PaymentAfterSaleFailed = "PaymentAfterSaleFailed";
    public const string AdminRequested = "AdminRequested";
}

/// <summary>
/// A payment-side situation that no automatic rule may resolve and that therefore waits for a person. Today there is one kind:
/// a cryptographically verified provider success for an attempt this system had already recorded as Failed.
/// </summary>
public static class PaymentExceptionKind
{
    /// <summary>
    /// The provider verifiably reports money taken for an attempt whose recorded verdict is Failed. The Failed verdict is kept
    /// as it was; the order is NOT paid or fulfilled by this and no ticket follows; new payments for the order are blocked.
    /// </summary>
    public const string LateSuccessAfterFailedAttempt = "LateSuccessAfterFailedAttempt";
}

public static class PaymentExceptionState
{
    public const string Open = "Open";

    /// <summary>An administrator recorded that the money was returned outside this system. Not provider-verified.</summary>
    public const string Resolved = "Resolved";
}

public static class PaymentExceptionResolution
{
    public const string RefundedExternally = "RefundedExternally";
}

/// <summary>The one next step this system supports for an exception (the console shows nothing else).</summary>
public static class PaymentExceptionAction
{
    public const string RefundExternallyThenRecord = "RefundExternallyThenRecord";
    public const string None = "None";
}

/// <summary>
/// Why a customer cannot pay for an order right now (per ORDER, unlike provider capabilities, which belong to the provider).
/// Customer-safe on purpose: it names the situation, never an administrator's investigation details.
/// </summary>
public static class PaymentIneligibility
{
    /// <summary>The order had a payment that needs our attention; it is never payable again (the customer starts a new order).</summary>
    public const string OrderHeldForReview = "OrderHeldForReview";

    /// <summary>The order is no longer awaiting payment (paid, cancelled, expired, refund pending or refunded) or its window ended.</summary>
    public const string OrderNotPayable = "OrderNotPayable";

    /// <summary>The order is payable but no enabled provider can charge its currency.</summary>
    public const string NoPaymentMethod = "NoPaymentMethod";
}

/// <summary>
/// A verified-signature success event that could NOT be applied as a payment because it disagrees with this system's own
/// records. A valid signature proves who sent the event, not that its amount, currency, order or reference are right, so the event
/// changes nothing: the order is not paid, no ticket, sale or refund follows. It is kept as minimal, sanitized evidence for an
/// administrator. Values that came from the event are untrusted; values loaded from the database are trusted, and the two are
/// stored in separate columns.
/// </summary>
public static class PaymentAnomalyKind
{
    /// <summary>Correlated to a known attempt and order, but the amount or currency differs from the order's.</summary>
    public const string AmountOrCurrencyMismatch = "AmountOrCurrencyMismatch";

    /// <summary>Correlated to a known attempt and order, but the provider reference differs from the one stored (or none was stored).</summary>
    public const string ReferenceMismatch = "ReferenceMismatch";

    /// <summary>Names an attempt this system does not know. Nothing is associated.</summary>
    public const string UnknownPaymentReference = "UnknownPaymentReference";

    /// <summary>Names an attempt that belongs to another provider, or an order that does not own it. Nothing is associated.</summary>
    public const string InconsistentAssociation = "InconsistentAssociation";
}

public static class PaymentAnomalyState
{
    public const string Open = "Open";

    /// <summary>An administrator recorded the outcome of their investigation. Not provider-verified.</summary>
    public const string Resolved = "Resolved";
}

/// <summary>What an administrator may state after investigating. Every value is a statement; none changes an order, attempt or refund.</summary>
public static class PaymentAnomalyResolution
{
    /// <summary>Confirmed with the provider that this payment does not belong to this system or environment.</summary>
    public const string NotOurPayment = "NotOurPayment";

    /// <summary>The money was returned outside this system.</summary>
    public const string RefundedExternally = "RefundedExternally";

    /// <summary>Investigated; nothing further is needed (for example the provider corrected or reversed it).</summary>
    public const string NoActionNeeded = "NoActionNeeded";

    public static readonly string[] All = [NotOurPayment, RefundedExternally, NoActionNeeded];
}

public static class PaymentAnomalyAction
{
    public const string InvestigateThenRecord = "InvestigateThenRecord";
    public const string None = "None";
}

public static class OrderPolicy
{
    public const int MaxIdempotencyKeyLength = 100;
}

public static class PaymentsErrors
{
    public static readonly Error MissingIdempotencyKey =
        new("Order.MissingIdempotencyKey", "An Idempotency-Key header is required.");

    public static readonly Error IdempotencyKeyTooLong =
        new("Order.IdempotencyKeyTooLong", $"The Idempotency-Key must be at most {OrderPolicy.MaxIdempotencyKeyLength} characters.");

    public static readonly Error IdempotencyKeyReused =
        Error.Conflict("Order.IdempotencyKeyReused", "The Idempotency-Key was already used for a different request.");

    public static readonly Error InvalidReservation =
        new("Order.InvalidReservation", "A reservation ID is required.");

    public static readonly Error ReservationNotFound =
        Error.NotFound("Order.ReservationNotFound", "The reservation was not found.");

    public static readonly Error ReservationNotActive =
        Error.Conflict("Order.ReservationNotActive", "The reservation is no longer active (it expired, was released, or was already paid), so it cannot be checked out.");

    public static readonly Error ReservationConsumed =
        Error.Conflict("Order.ReservationConsumed", "This reservation already has an order that is no longer payable.");

    public static readonly Error OrderNotFound =
        Error.NotFound("Order.NotFound", "The order was not found.");

    public static readonly Error OrderNotPayable =
        Error.Conflict("Order.NotPayable", "The order is no longer awaiting payment.");

    public static readonly Error OrderNotCancellable =
        Error.Conflict("Order.NotCancellable", "The order can no longer be cancelled.");

    public static readonly Error AttemptInProgress =
        Error.Conflict("Payment.AttemptInProgress", "A payment attempt for this order is already in progress.");

    public static readonly Error AttemptNotFound =
        Error.NotFound("Payment.AttemptNotFound", "The payment attempt was not found for this order.");

    public static readonly Error AlreadyCompleted =
        Error.Conflict("Payment.AlreadyCompleted", "This payment attempt was already completed with a different outcome.");

    public static readonly Error LateSuccessNeedsReview =
        Error.Conflict("Payment.LateSuccessNeedsReview", "A success was reported for a payment attempt that had already failed. It has been recorded for review and nothing was changed.");

    public static readonly Error InitiationUncertain =
        Error.Conflict("Payment.InitiationUncertain", "We could not confirm whether the payment page was created, so this attempt was closed. Nothing has been charged. Please start the payment again.");

    public static readonly Error OrderHeldForReview =
        Error.Conflict("Payment.OrderHeldForReview", "An earlier payment for this order needs attention from our team, so this order cannot be paid again. Please start a new order.");

    public static readonly Error ExceptionNotConfirmed =
        new("Operations.ExceptionNotConfirmed", "Confirm that the money was returned outside this system before recording it.");

    public static readonly Error ExceptionResolutionInvalid =
        new("Operations.ExceptionResolutionInvalid", "The resolution record is invalid.");

    public static readonly Error ExceptionAmountMismatch =
        Error.Conflict("Operations.ExceptionAmountMismatch", "The amount or currency does not match the payment this exception is about.");

    public static readonly Error ExceptionResolutionConflict =
        Error.Conflict("Operations.ExceptionResolutionConflict", "This exception was already resolved with different details.");

    public static readonly Error AnomalyNotConfirmed =
        new("Operations.AnomalyNotConfirmed", "Confirm that you investigated this payment event before recording the outcome.");

    public static readonly Error AnomalyResolutionInvalid =
        new("Operations.AnomalyResolutionInvalid", "The outcome of the investigation is not valid.");

    public static readonly Error AnomalyResolutionConflict =
        Error.Conflict("Operations.AnomalyResolutionConflict", "This anomaly was already resolved with a different statement.");

    public static readonly Error NotFoundForOperation =
        Error.NotFound("Operations.NotFound", "The item was not found.");

    public static readonly Error InvalidOperationState =
        Error.Conflict("Operations.InvalidState", "The item is not in a state that allows this operation.");

    public static readonly Error ProviderUnavailable =
        new("Payment.ProviderUnavailable", "No payment provider is available.", ErrorType.Unavailable);

    public static readonly Error ManualRefundNotConfirmed =
        new("Operations.ManualRefundNotConfirmed", "Confirm that the refund was performed in the provider's dashboard before recording it.");

    public static readonly Error ManualRefundInvalid =
        new("Operations.ManualRefundInvalid", "The manual refund record is invalid.");

    public static readonly Error ManualRefundNotAllowed =
        Error.Conflict("Operations.ManualRefundNotAllowed", "A manual completion can only be recorded for a refund waiting for review whose payment provider cannot refund through the API.");

    public static readonly Error ManualRefundMismatch =
        Error.Conflict("Operations.ManualRefundMismatch", "The amount or currency does not match the refund owed for this order.");

    public static readonly Error ManualRefundConflict =
        Error.Conflict("Operations.ManualRefundConflict", "This refund was already recorded as manually completed with different details.");

    public static readonly Error ProviderRequired =
        new("Payment.ProviderRequired", "Several payment providers are available: choose one.");

    public static readonly Error ProviderNotEnabled =
        new("Payment.ProviderNotEnabled", "That payment provider is not available.");

    public static readonly Error CurrencyNotSupported =
        new("Payment.CurrencyNotSupported", "No available payment provider can take payment in this order's currency.");

    public static readonly Error ProviderMismatch =
        Error.Conflict("Payment.ProviderMismatch", "A payment for this order was already started with a different provider. Finish or cancel it before choosing another.");

    public static readonly Error ProviderRejected =
        new("Payment.ProviderRejected", "The payment provider could not start this payment.", ErrorType.Unavailable);

    public static readonly Error ProviderTemporarilyUnavailable =
        new("Payment.ProviderTemporarilyUnavailable", "The payment provider did not answer. Nothing was charged; try again with the same request.", ErrorType.Unavailable);
}

namespace PulseReserve.Modules.Payments.Features;

/// <summary>
/// Tunables of the durable workflows. All durations are enforced with the database clock. Defaults are conservative;
/// tests shrink them (grace and backoff 0) to run deterministically.
/// </summary>
public sealed record WorkflowOptions
{
    /// <summary>Rows processed per worker pass (bounded batches).</summary>
    public int BatchSize { get; init; } = 20;

    /// <summary>How long a claimed item stays owned by a worker. A crashed worker's items become claimable again afterwards.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A just-completed payment is left to its own request this long before reconciliation steps in.</summary>
    public TimeSpan CompletionGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>An attempt still Initiated after this long is asked about at the provider.</summary>
    public TimeSpan StaleAttemptAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A stale attempt the provider still reports as Pending, on an order that is no longer payable, is abandoned (marked Cancelled) after this long. A late verdict is still accepted and ends as RefundRequired.</summary>
    public TimeSpan AbandonAttemptAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>First retry delay; doubles per attempt up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromHours(1);

    /// <summary>After this many processing attempts an item becomes NeedsReview (terminal until an administrator retries).</summary>
    public int MaxAttempts { get; init; } = 8;
}

public sealed record ReconciliationTask(
    Guid Id, string Kind, Guid SubjectId, string State, int Attempts, string? LastOutcome, string? LastError,
    DateTime NextAttemptAtUtc, DateTime CreatedAtUtc, DateTime? CompletedAtUtc)
{
    /// <summary>
    /// The payment provider of the attempt this task is about (null for tasks about an order, and when unknown). Filled in
    /// for operators by <see cref="PaymentOperationsService"/>; it is never stored on the task.
    /// </summary>
    public string? Provider { get; init; }
}

/// <summary>
/// What an operator needs to know about a configured payment provider: what the system can and cannot do with it. Names and
/// capability flags only; no configuration, key or account detail ever appears here.
/// </summary>
public sealed record OperationsProvider(string Name, bool IsSimulated, bool IsAvailable, bool SupportsPaymentLookup, bool SupportsRefunds)
{
    /// <summary>False when a repeated initiation could create a second payment session at the provider (so it is never repeated).</summary>
    public bool SupportsIdempotentInitiation { get; init; } = true;
}

public sealed record AttemptRecord(
    Guid AttemptId, Guid OrderId, string Status, string Provider, string? ProviderReference, DateTime CreatedAtUtc, TimeSpan Age);

public sealed record Refund(
    Guid RefundId, Guid OrderId, Guid PaymentAttemptId, decimal Amount, string Currency, string Reason, string State,
    string Provider, bool IsSimulated, int SubmissionNumber, string? ProviderRefundId, int Attempts, string? LastError,
    Guid? RequestedBy, DateTime CreatedAtUtc, DateTime? CompletedAtUtc, DateTime NextAttemptAtUtc, DateTime UpdatedAtUtc)
{
    /// <summary>The provider-side idempotency key: stable across ambiguous retries, new only after a definitive refusal.</summary>
    public string IdempotencyKey => $"refund-{RefundId:N}-{SubmissionNumber}";

    // Present only when state is ManuallyRecorded: an administrator's record that the refund was done outside this system.
    // None of this is provider-confirmed, and it is kept apart from ProviderRefundId on purpose.
    public string? ManualReference { get; init; }
    public string? ManualNote { get; init; }
    public Guid? ManuallyRecordedBy { get; init; }
    public DateTime? ManuallyRecordedAtUtc { get; init; }
}

/// <summary>
/// An administrator's statement that they performed a refund in an external dashboard. <c>Amount</c> and <c>Currency</c> are an
/// ECHO of what the console showed (the refund is always the full order amount): they are verified against the server's
/// own record and never trusted as the amount owed. Provider, order and payment come only from the stored refund.
/// </summary>
public sealed record ManualRefundRecord(bool ConfirmedPerformedExternally, decimal Amount, string Currency, string? ExternalReference, string? Note);

public enum ManualRefundOutcome { Recorded, Replay, NotFound, NotAllowed, AmountMismatch, Conflict }

public sealed record ManualRefundResult(ManualRefundOutcome Outcome, Refund? Refund);

public sealed record OutboxEvent(
    Guid Id, string Type, Guid AggregateId, string State, int Attempts, string? LastError, DateTime CreatedAtUtc, DateTime? ProcessedAtUtc,
    DateTime NextAttemptAtUtc, DateTime UpdatedAtUtc);

/// <summary>
/// One row of the append-only financial transition history: which entity moved from which state to which, who or what caused
/// it, and on what basis. <c>FactBasis</c> keeps a provider's verified fact apart from an application decision, an administrator's
/// command and an administrator's statement. It carries identifiers and codes only: never a payload, signature, secret or customer
/// detail. <c>Kind</c> is <c>Transition</c> (a state change that happened) or <c>Rejected</c> (an event that was refused or conflicted
/// and changed nothing).
/// </summary>
/// <summary>
/// The minimal, sanitized evidence of a success event that was not applied (see <see cref="Domain.PaymentAnomalyKind"/>). The
/// <c>Received*</c> and <c>Claimed*</c> values come from the event and are UNTRUSTED; the <c>Expected*</c> values and the
/// attempt/order associations come from this system's own records (and exist only when the event matched a known attempt of that
/// provider and order). No payload, signature, reference or customer detail is part of it.
/// </summary>
public sealed record PaymentAnomalyRecord(
    string Kind, string ReasonCode, string Provider, string? ProviderEventId, Guid? AttemptId, Guid? OrderId, Guid? ClaimedAttemptId,
    Guid? ClaimedOrderId, long? ReceivedAmountMinor, string? ReceivedCurrency, long? ExpectedAmountMinor, string? ExpectedCurrency)
{
    /// <summary>The same provider event is one anomaly however often it is redelivered.</summary>
    public string DedupeKey => ProviderEventId is { Length: > 0 } e
        ? $"{Provider}:{e}"
        : $"{Provider}:{ClaimedAttemptId}:{ReceivedAmountMinor}:{ReceivedCurrency}:{ReasonCode}";
}

/// <summary>Writes anomalies. Idempotent per provider event; a failure surfaces as an exception so the webhook is not acknowledged.</summary>
public interface IPaymentAnomalyRecorder
{
    /// <summary>True when a new anomaly was recorded, false when this event was already on record.</summary>
    Task<bool> RecordAsync(PaymentAnomalyRecord record, CancellationToken cancellationToken = default);
}

public sealed record PaymentAnomaly(
    Guid AnomalyId, string Kind, string ReasonCode, string State, string Provider, string? ProviderEventId, Guid? AttemptId, Guid? OrderId,
    Guid? ClaimedAttemptId, Guid? ClaimedOrderId, long? ReceivedAmountMinor, string? ReceivedCurrency, long? ExpectedAmountMinor,
    string? ExpectedCurrency, string? OrderStatusAtDetection, string? AttemptStatusAtDetection, DateTime DetectedAtUtc, string? Resolution,
    Guid? ResolvedBy, DateTime? ResolvedAtUtc, string? ResolutionReference, string? ResolutionNote, string? OrderStatus)
{
    /// <summary>The only step the system supports: derived from the state, never chosen by the client.</summary>
    public string NextAction => State == Domain.PaymentAnomalyState.Open ? Domain.PaymentAnomalyAction.InvestigateThenRecord : Domain.PaymentAnomalyAction.None;

    /// <summary>
    /// Seconds on the DATABASE clock from detection to now (while Open) or to the recorded resolution (once Resolved), so the age does not
    /// depend on the browser's clock.
    /// </summary>
    public long AgeSeconds { get; init; }
}

/// <summary>An administrator's statement about the outcome of investigating an anomaly. It asserts nothing the provider confirmed.</summary>
public sealed record PaymentAnomalyResolutionRecord(bool InvestigationConfirmed, string Resolution, string? ExternalReference, string? Note);

public enum PaymentAnomalyResolveOutcome { Resolved, Replay, NotFound, Conflict }

public sealed record PaymentAnomalyResolveResult(PaymentAnomalyResolveOutcome Outcome, PaymentAnomaly? Anomaly);

public sealed record FinancialTransition(
    Guid Id, DateTime OccurredAtUtc, string EntityType, Guid EntityId, Guid? OrderId, string? FromState, string ToState, string EventCode,
    string Kind, string Source, string FactBasis, string? Provider, string? ProviderEventId, Guid? ActorUserId, string? CorrelationId, string? Reason);

public sealed record AuditEntry(
    Guid Id, Guid ActorUserId, string Action, string SubjectType, Guid SubjectId, string? Detail, DateTime CreatedAtUtc);

public sealed record WorkflowSummary(
    int ReconciliationNeedsReview, int ReconciliationPending, int RefundsNeedingAttention, int RefundsOpen,
    int OutboxNeedsReview, int OutboxPending, int OrdersAwaitingRefund,
    IReadOnlyDictionary<string, int> TasksByState, IReadOnlyDictionary<string, int> RefundsByState,
    IReadOnlyDictionary<string, int> OutboxByState)
{
    /// <summary>Payment exceptions (a verified success for an attempt recorded as failed) still waiting for an administrator.</summary>
    public int PaymentExceptionsOpen { get; init; }

    /// <summary>Success events that could not be applied (amount, currency, reference or association disagree) and wait for an administrator.</summary>
    public int PaymentAnomaliesOpen { get; init; }

    /// <summary>Open anomalies by kind (only kinds that have an open anomaly). Counted from the durable table, never from memory.</summary>
    public IReadOnlyDictionary<string, int> PaymentAnomaliesOpenByKind { get; init; } = new Dictionary<string, int>();

    /// <summary>Seconds (database clock) since the oldest open anomaly was detected; null when none is open. The number that must not keep growing.</summary>
    public long? OldestOpenPaymentAnomalyAgeSeconds { get; init; }

    /// <summary>Seconds (database clock) since the oldest open payment exception was detected; null when none is open.</summary>
    public long? OldestOpenPaymentExceptionAgeSeconds { get; init; }
}

/// <summary>
/// A verified provider success that contradicts the recorded verdict of a payment attempt. Two groups of facts, kept apart:
/// what the PROVIDER verifiably reported (amount and currency are the order's own, checked before the exception is created;
/// the time it was detected) and what THIS SYSTEM had recorded (the attempt's original Failed verdict, preserved, and the order
/// as it stood). The order is never paid or fulfilled by an exception; the next supported step is a refund made outside this
/// system, then an administrator's recorded statement. No webhook payload, signature, provider reference or customer
/// identifier is part of this record.
/// </summary>
public sealed record PaymentException(
    Guid ExceptionId, string Kind, Guid AttemptId, Guid OrderId, string Provider, bool IsSimulated, decimal Amount, string Currency,
    string OriginalAttemptStatus, string? OriginalFailureCode, DateTime? OriginalVerdictAtUtc, string OrderStatusAtDetection,
    DateTime DetectedAtUtc, string State, string? Resolution, Guid? ResolvedBy, DateTime? ResolvedAtUtc,
    string? ResolutionReference, string? ResolutionNote, string OrderStatus, DateTime OrderPaymentDeadlineUtc, string? OrderRefundState)
{
    /// <summary>The only step the system supports: derived from the state, never chosen by the client.</summary>
    public string NextAction => State == Domain.PaymentExceptionState.Open
        ? Domain.PaymentExceptionAction.RefundExternallyThenRecord
        : Domain.PaymentExceptionAction.None;
}

/// <summary>
/// An administrator's statement that the money for the exception's payment was returned OUTSIDE this system. <c>Amount</c> and
/// <c>Currency</c> are an echo of what the console showed, verified against the stored exception and never trusted.
/// </summary>
public sealed record PaymentExceptionResolutionRecord(bool RefundPerformedExternally, decimal Amount, string Currency, string? ExternalReference, string? Note);

public enum PaymentExceptionResolveOutcome { Resolved, Replay, NotFound, AmountMismatch, Conflict }

public sealed record PaymentExceptionResolveResult(PaymentExceptionResolveOutcome Outcome, PaymentException? Exception);

/// <summary>
/// An order as operators see it: money, state and refund progress only. Deliberately no customer identifiers,
/// provider references or tokens.
/// </summary>
public sealed record AdminOrder(
    Guid OrderId, string Status, decimal TotalAmount, string Currency, DateTime CreatedAtUtc, DateTime UpdatedAtUtc,
    Guid? RefundId, string? RefundState, string? PaymentProvider, bool? PaymentIsSimulated);

/// <summary>What one worker pass did (for logs, tests and the "process now" operation).</summary>
public sealed record WorkflowRunResult(
    int TasksDiscovered, int TasksProcessed, int TasksFailed, int RefundsProcessed, int RefundsFailed,
    int OutboxProcessed, int OutboxFailed);

public enum OperationOutcome { Done, NotFound, InvalidState }

public sealed record OperationResult(OperationOutcome Outcome);

/// <summary>
/// Durable work coordination. Items are claimed with <c>FOR UPDATE SKIP LOCKED</c> plus a lease, so concurrent workers
/// never take the same item, a crashed worker's items return after the lease, and every operation is a short
/// transaction. Retry counters, backoff, the NeedsReview terminal state and safe diagnostics live in the rows.
/// </summary>
/// <summary>The one thing the webhook path needs from the workflow store: a payment attempt by id.</summary>
public interface IPaymentAttemptLookup
{
    Task<AttemptRecord?> GetAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default);
}

public interface IWorkflowStore : IPaymentAttemptLookup, IPaymentAnomalyRecorder
{
    // ── reconciliation discovery and tasks ──
    Task<int> DiscoverReconciliationTasksAsync(WorkflowOptions options, CancellationToken cancellationToken = default);

    Task<int> EnsureRefundsForRefundRequiredOrdersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReconciliationTask>> ClaimTasksAsync(WorkflowOptions options, CancellationToken cancellationToken = default);

    Task CompleteTaskAsync(Guid taskId, string outcome, CancellationToken cancellationToken = default);

    Task RetryTaskLaterAsync(Guid taskId, string outcome, string? error, WorkflowOptions options, CancellationToken cancellationToken = default);

    /// <summary>Marks a still-Initiated attempt Cancelled even though it has a provider reference (see AbandonAttemptAfter). Idempotent.</summary>
    Task AbandonAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default);

    /// <summary>Cancels an attempt that never obtained a provider reference (nothing was ever charged). Idempotent.</summary>
    Task CancelAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default);

    /// <summary>Persists Expired for an overdue PendingPayment order. Returns the order's reservation and customer when it was (or already is) closed.</summary>
    Task<(Guid ReservationId, Guid CustomerId)?> ExpireOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    // ── refunds ──
    Task<IReadOnlyList<Refund>> ClaimRefundsAsync(WorkflowOptions options, CancellationToken cancellationToken = default);

    /// <summary>Records the INTENT to call the provider (Requested -> Unknown) before any call, so a crash mid-call cannot lead to a blind resubmission.</summary>
    Task<bool> MarkRefundUnknownAsync(Guid refundId, CancellationToken cancellationToken = default);

    Task MarkRefundPendingAsync(Guid refundId, string? providerRefundId, WorkflowOptions options, CancellationToken cancellationToken = default);

    /// <summary>Marks the refund Succeeded and the order Refunded in one transaction.</summary>
    Task MarkRefundSucceededAsync(Guid refundId, string? providerRefundId, CancellationToken cancellationToken = default);

    /// <summary>Definite refusal: permanent -> Failed (terminal); retryable -> Requested with a NEW submission number and backoff.</summary>
    Task MarkRefundRejectedAsync(Guid refundId, string? code, bool retryable, WorkflowOptions options, CancellationToken cancellationToken = default);

    /// <summary>Ambiguity or "still pending": keeps the state, schedules a later check, escalates to NeedsReview after the attempt limit.</summary>
    Task DeferRefundAsync(Guid refundId, string? error, WorkflowOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Escalates an open refund straight to NeedsReview (an administrator must act), for providers whose refunds cannot be
    /// made or looked up through the API. Never marks anything complete.
    /// </summary>
    Task MarkRefundNeedsReviewAsync(Guid refundId, string error, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Refund>> ListRefundsAsync(string? state, int limit, CancellationToken cancellationToken = default);

    Task<OperationResult> RetryRefundAsync(Guid refundId, Guid adminUserId, CancellationToken cancellationToken = default);

    Task<Refund?> GetRefundAsync(Guid refundId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records, in ONE transaction under a row lock, that a refund waiting for review was performed externally: the refund
    /// becomes ManuallyRecorded (terminal), the order becomes Refunded, and the audit entry is written. Verifies state, amount
    /// and currency against the stored refund; an identical repeat is an idempotent replay; a different repeat is a conflict.
    /// Never calls a provider. The caller has already established that the provider cannot refund through the API.
    /// </summary>
    Task<ManualRefundResult> RecordManualRefundAsync(Guid refundId, Guid adminUserId, ManualRefundRecord record, CancellationToken cancellationToken = default);

    // ── payment anomalies (verified-signature success events that disagree with our records) ──
    Task<IReadOnlyList<PaymentAnomaly>> ListAnomaliesAsync(string? state, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records, in ONE transaction under a row lock, an administrator's statement about an anomaly and audits it. Creating the
    /// anomaly is <see cref="IPaymentAnomalyRecorder.RecordAsync"/>, never this. Calls no provider and changes no order, attempt or refund.
    /// </summary>
    Task<PaymentAnomalyResolveResult> ResolveAnomalyAsync(Guid anomalyId, Guid adminUserId, PaymentAnomalyResolutionRecord record, CancellationToken cancellationToken = default);

    // ── payment exceptions (verified success for an attempt recorded as failed) ──
    Task<IReadOnlyList<PaymentException>> ListExceptionsAsync(string? state, int limit, CancellationToken cancellationToken = default);

    Task<PaymentException?> GetExceptionAsync(Guid exceptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records, in ONE transaction under a row lock, an administrator's statement that the money was returned outside this
    /// system, and audits it. The exception is created elsewhere (atomically with the verdict), never here. An identical repeat is
    /// an idempotent replay; a different repeat is a conflict and never overwrites. Calls no provider.
    /// </summary>
    Task<PaymentExceptionResolveResult> ResolveExceptionAsync(Guid exceptionId, Guid adminUserId, PaymentExceptionResolutionRecord record, CancellationToken cancellationToken = default);

    Task<(OperationResult Result, Refund? Refund)> RequestRefundAsync(Guid orderId, Guid adminUserId, CancellationToken cancellationToken = default);

    // ── outbox, tickets, notifications ──
    Task<IReadOnlyList<OutboxEvent>> ClaimOutboxAsync(WorkflowOptions options, CancellationToken cancellationToken = default);

    Task CompleteOutboxAsync(Guid eventId, CancellationToken cancellationToken = default);

    Task RetryOutboxLaterAsync(Guid eventId, string? error, WorkflowOptions options, CancellationToken cancellationToken = default);

    /// <summary>Issues one ticket per order line, atomically and only for a Paid order. Returns the number of tickets that exist afterwards, or null if the order is not Paid.</summary>
    Task<int?> IssueTicketsAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Ticket>> ListTicketsAsync(Guid orderId, Guid? customerId, CancellationToken cancellationToken = default);

    Task<bool> HasNotificationAsync(Guid orderId, string kind, CancellationToken cancellationToken = default);

    Task RecordNotificationAsync(Guid orderId, string kind, string channel, CancellationToken cancellationToken = default);

    // ── operations ──
    Task<IReadOnlyList<ReconciliationTask>> ListTasksAsync(string? state, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboxEvent>> ListOutboxAsync(string? state, int limit, CancellationToken cancellationToken = default);

    Task<OperationResult> RetryTaskAsync(Guid taskId, Guid adminUserId, CancellationToken cancellationToken = default);

    Task<OperationResult> RetryOutboxAsync(Guid eventId, Guid adminUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>The financial transition history, newest first; optionally for one order, one entity type or one entity.</summary>
    Task<IReadOnlyList<FinancialTransition>> ListTransitionsAsync(Guid? orderId, string? entityType, Guid? entityId, int limit, CancellationToken cancellationToken = default);

    Task<WorkflowSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>Orders for the operations console (optionally by status), most recently changed first.</summary>
    Task<IReadOnlyList<AdminOrder>> ListOrdersForOperationsAsync(string? status, int limit, CancellationToken cancellationToken = default);

    Task<AdminOrder?> GetOrderForOperationsAsync(Guid orderId, CancellationToken cancellationToken = default);
}

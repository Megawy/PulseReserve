/*
 * Models for the Admin payment operations API (PaymentOperationsEndpoints.cs, WorkflowContracts.cs). Property names and
 * states are taken from the C# records; nothing here is invented. Provider references and refund idempotency keys exist
 * in the API's refund payload but are deliberately NOT modelled: the console never shows them.
 */

/** Pending, Processing, Done, NeedsReview (WorkState). Shared by reconciliation tasks and outbox events. */
export type WorkState = 'Pending' | 'Processing' | 'Done' | 'NeedsReview' | (string & {});

/**
 * Requested, Unknown, Pending, Succeeded, Failed, NeedsReview, ManuallyRecorded (RefundState). `Succeeded` means the provider
 * confirmed the refund; `ManuallyRecorded` means an administrator recorded that it was done outside this system.
 */
export type RefundState =
  | 'Requested'
  | 'Unknown'
  | 'Pending'
  | 'Succeeded'
  | 'Failed'
  | 'NeedsReview'
  | 'ManuallyRecorded'
  | (string & {});

export const WORK_STATES: readonly WorkState[] = ['Pending', 'Processing', 'Done', 'NeedsReview'];
export const REFUND_STATES: readonly RefundState[] = [
  'Requested',
  'Unknown',
  'Pending',
  'Succeeded',
  'Failed',
  'NeedsReview',
  'ManuallyRecorded',
];

/** Open, Resolved (PaymentExceptionState). */
export type PaymentExceptionState = 'Open' | 'Resolved' | (string & {});
export const PAYMENT_EXCEPTION_STATES: readonly PaymentExceptionState[] = ['Open', 'Resolved'];

export interface WorkflowSummary {
  /** Verified provider successes for attempts recorded as failed that still wait for an administrator. */
  readonly paymentExceptionsOpen?: number;
  /** Signature-verified success events that were NOT applied (amount, currency, reference or association disagree) and wait for an administrator. */
  readonly paymentAnomaliesOpen?: number;
  /** Open anomalies by kind, counted from the database (only kinds that have one). */
  readonly paymentAnomaliesOpenByKind?: Readonly<Record<string, number>>;
  /** Seconds, on the server's clock, since the oldest open anomaly was detected; null/absent when none is open. */
  readonly oldestOpenPaymentAnomalyAgeSeconds?: number | null;
  /** Seconds, on the server's clock, since the oldest open payment exception was detected; null/absent when none is open. */
  readonly oldestOpenPaymentExceptionAgeSeconds?: number | null;
  readonly reconciliationNeedsReview: number;
  readonly reconciliationPending: number;
  readonly refundsNeedingAttention: number;
  readonly refundsOpen: number;
  readonly outboxNeedsReview: number;
  readonly outboxPending: number;
  readonly ordersAwaitingRefund: number;
  readonly tasksByState: Readonly<Record<string, number>>;
  readonly refundsByState: Readonly<Record<string, number>>;
  readonly outboxByState: Readonly<Record<string, number>>;
}

export interface ReconciliationTask {
  readonly id: string;
  /** FinalizeSuccessfulPayment, ResolveStaleAttempt or ExpireOrder. */
  readonly kind: string;
  readonly subjectId: string;
  readonly state: WorkState;
  readonly attempts: number;
  readonly lastOutcome: string | null;
  readonly lastError: string | null;
  readonly nextAttemptAtUtc: string;
  readonly createdAtUtc: string;
  readonly completedAtUtc: string | null;
  /** The payment provider of the attempt this task is about (absent for order tasks). */
  readonly provider?: string | null;
}

/** What the system can do with a configured provider (names and flags only, never configuration). */
export interface OperationsProvider {
  readonly name: string;
  readonly isSimulated: boolean;
  readonly isAvailable: boolean;
  /** False: the provider cannot be asked for a payment's status; only a verified callback or a person resolves it. */
  readonly supportsPaymentLookup: boolean;
  /** False: refunds cannot be made or confirmed through the API; a person refunds in the provider's dashboard. */
  readonly supportsRefunds: boolean;
  /** False when a repeated initiation could create a second payment session at the provider, so it is never repeated. */
  readonly supportsIdempotentInitiation?: boolean;
}

export interface OperationsRefund {
  readonly refundId: string;
  readonly orderId: string;
  readonly paymentAttemptId: string;
  readonly amount: number;
  readonly currency: string;
  /** PaymentAfterSaleFailed or AdminRequested. */
  readonly reason: string;
  readonly state: RefundState;
  readonly provider: string;
  readonly isSimulated: boolean;
  readonly submissionNumber: number;
  readonly attempts: number;
  readonly lastError: string | null;
  /** The administrator who requested it; null when the system created it. */
  readonly requestedBy: string | null;
  readonly createdAtUtc: string;
  readonly completedAtUtc: string | null;
  readonly nextAttemptAtUtc: string;
  readonly updatedAtUtc: string;
  /** Present only for a ManuallyRecorded refund. None of it is provider-confirmed. */
  readonly manualReference?: string | null;
  readonly manualNote?: string | null;
  readonly manuallyRecordedBy?: string | null;
  readonly manuallyRecordedAtUtc?: string | null;
}

/**
 * An administrator's statement that the refund was performed in the provider's dashboard. Amount and currency are an echo of
 * what the console showed; the server verifies them against its own record and never takes them as the amount owed.
 */
export interface ManualRefundRequest {
  readonly confirmedPerformedExternally: boolean;
  readonly amount: number;
  readonly currency: string;
  readonly externalReference: string | null;
  readonly note: string | null;
}

/**
 * A verified provider success that contradicts a payment attempt this system had recorded as failed. Two groups of facts
 * that must never be blended: what THIS SYSTEM recorded (`originalAttemptStatus`, kept as it was; the order) and what the
 * PROVIDER verifiably reported (a success of `amount` `currency`, detected at `detectedAtUtc`). The order is never paid by
 * an exception. Webhook payloads, signatures, provider references and customer identifiers are deliberately absent.
 */
export interface PaymentException {
  readonly exceptionId: string;
  readonly kind: string;
  readonly attemptId: string;
  readonly orderId: string;
  readonly provider: string;
  readonly isSimulated: boolean;
  readonly amount: number;
  readonly currency: string;
  /** The verdict this system had recorded and still holds. */
  readonly originalAttemptStatus: string;
  readonly originalFailureCode: string | null;
  readonly originalVerdictAtUtc: string | null;
  readonly orderStatusAtDetection: string;
  readonly detectedAtUtc: string;
  readonly state: PaymentExceptionState;
  readonly resolution: string | null;
  readonly resolvedBy: string | null;
  readonly resolvedAtUtc: string | null;
  readonly resolutionReference: string | null;
  readonly resolutionNote: string | null;
  /** The order as it is now. */
  readonly orderStatus: string;
  readonly orderPaymentDeadlineUtc: string;
  /** State of the order's OWN refund record, if it has one (a different payment's refund, never this payment's). */
  readonly orderRefundState: RefundState | null;
  /** The only step the server supports: `RefundExternallyThenRecord` or `None`. */
  readonly nextAction: string;
}

/** An administrator's statement that the money was returned outside this system; amount/currency are echoes, verified server-side. */
export interface ResolvePaymentExceptionRequest {
  readonly refundPerformedExternally: boolean;
  readonly amount: number;
  readonly currency: string;
  readonly externalReference: string | null;
  readonly note: string | null;
}

/**
 * A signature-verified success event that was NOT applied because it disagrees with this system's records. `received*` and
 * `claimed*` come from the event and are UNTRUSTED; `expected*`, `attemptId`, `orderId` and the statuses come from this system's own
 * records (and the ids exist only when the event matched a known attempt of that provider and order). No payload, signature,
 * reference or customer detail is part of it.
 */
export interface PaymentAnomaly {
  readonly anomalyId: string;
  readonly kind: string;
  readonly reasonCode: string;
  readonly state: PaymentExceptionState;
  readonly provider: string;
  readonly providerEventId: string | null;
  readonly attemptId: string | null;
  readonly orderId: string | null;
  readonly claimedAttemptId: string | null;
  readonly claimedOrderId: string | null;
  readonly receivedAmountMinor: number | null;
  readonly receivedCurrency: string | null;
  readonly expectedAmountMinor: number | null;
  readonly expectedCurrency: string | null;
  readonly orderStatusAtDetection: string | null;
  readonly attemptStatusAtDetection: string | null;
  readonly detectedAtUtc: string;
  readonly resolution: string | null;
  readonly resolvedBy: string | null;
  readonly resolvedAtUtc: string | null;
  readonly resolutionReference: string | null;
  readonly resolutionNote: string | null;
  /** The order as it is now (only when the anomaly is associated with one). */
  readonly orderStatus: string | null;
  /** The only step the server supports: `InvestigateThenRecord` or `None`. */
  readonly nextAction: string;
  /** Seconds on the server's clock from detection to now (Open) or to the recorded resolution (Resolved). */
  readonly ageSeconds?: number;
}

export type AnomalyResolution = 'NotOurPayment' | 'RefundedExternally' | 'NoActionNeeded';
export const ANOMALY_RESOLUTIONS: readonly AnomalyResolution[] = [
  'NotOurPayment',
  'RefundedExternally',
  'NoActionNeeded',
];

/** An administrator's statement after investigating; asserts nothing the provider confirmed and changes no order, attempt or refund. */
export interface ResolvePaymentAnomalyRequest {
  readonly investigationConfirmed: boolean;
  readonly resolution: AnomalyResolution;
  readonly externalReference: string | null;
  readonly note: string | null;
}

/**
 * One row of the append-only financial transition history. `factBasis` keeps a provider's verified fact apart from an application
 * decision, an administrator's command and an administrator's statement. `kind` is `Transition` (a change that happened) or `Rejected`
 * (an event that was refused or conflicted and changed nothing).
 */
export interface FinancialTransition {
  readonly id: string;
  readonly occurredAtUtc: string;
  readonly entityType: string;
  readonly entityId: string;
  readonly orderId: string | null;
  readonly fromState: string | null;
  readonly toState: string;
  readonly eventCode: string;
  readonly kind: string;
  readonly source: string;
  readonly factBasis: string;
  readonly provider: string | null;
  readonly providerEventId: string | null;
  readonly actorUserId: string | null;
  readonly correlationId: string | null;
  readonly reason: string | null;
}

export interface OutboxEvent {
  readonly id: string;
  /** IssueTickets or NotifyOrderPaid. */
  readonly type: string;
  readonly aggregateId: string;
  readonly state: WorkState;
  readonly attempts: number;
  readonly lastError: string | null;
  readonly createdAtUtc: string;
  readonly processedAtUtc: string | null;
  readonly nextAttemptAtUtc: string;
  readonly updatedAtUtc: string;
}

export interface AuditEntry {
  readonly id: string;
  readonly actorUserId: string;
  readonly action: string;
  readonly subjectType: string;
  readonly subjectId: string;
  readonly detail: string | null;
  readonly createdAtUtc: string;
}

/** An order as operators see it (no customer identifiers). */
export interface OperationsOrder {
  readonly orderId: string;
  readonly status: string;
  readonly totalAmount: number;
  readonly currency: string;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
  readonly refundId: string | null;
  readonly refundState: RefundState | null;
  readonly paymentProvider: string | null;
  readonly paymentIsSimulated: boolean | null;
}

/** What one process-now pass did. */
export interface WorkflowRunResult {
  readonly tasksDiscovered: number;
  readonly tasksProcessed: number;
  readonly tasksFailed: number;
  readonly refundsProcessed: number;
  readonly refundsFailed: number;
  readonly outboxProcessed: number;
  readonly outboxFailed: number;
}

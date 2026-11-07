/**
 * How backend states are shown. One table per state machine, so an order status reads the same on the event page, in
 * the order history and in the checkout panel, and a state the UI does not know is shown verbatim (never hidden or
 * mislabelled). Wording stays honest about the simulator: "Paid (simulated)" is never presented as real money.
 */
export type Tone = 'neutral' | 'success' | 'warning' | 'danger' | 'info' | 'brand';

export interface StatusView {
  readonly label: string;
  readonly tone: Tone;
}

export type StatusKind =
  'order' | 'payment' | 'event' | 'reservation' | 'ticket' | 'work' | 'refund' | 'exception';

const TABLES: Record<StatusKind, Record<string, StatusView>> = {
  order: {
    PendingPayment: { label: 'Awaiting payment', tone: 'warning' },
    Paid: { label: 'Paid (simulated)', tone: 'success' },
    Cancelled: { label: 'Cancelled', tone: 'neutral' },
    Expired: { label: 'Expired', tone: 'neutral' },
    RefundRequired: { label: 'Refund pending (simulated)', tone: 'warning' },
    Refunded: { label: 'Refunded (simulated)', tone: 'info' },
  },
  payment: {
    Initiated: { label: 'In progress', tone: 'info' },
    Succeeded: { label: 'Succeeded (simulated)', tone: 'success' },
    Failed: { label: 'Failed', tone: 'danger' },
    Cancelled: { label: 'Cancelled', tone: 'neutral' },
  },
  event: {
    OnSale: { label: 'On sale', tone: 'success' },
    Published: { label: 'Coming soon', tone: 'brand' },
    Closed: { label: 'Sales closed', tone: 'neutral' },
    Cancelled: { label: 'Cancelled', tone: 'danger' },
  },
  reservation: {
    Held: { label: 'Seats held', tone: 'info' },
    Checkout: { label: 'In checkout', tone: 'brand' },
    Confirmed: { label: 'Confirmed', tone: 'success' },
    Released: { label: 'Released', tone: 'neutral' },
    Expired: { label: 'Expired', tone: 'neutral' },
  },
  ticket: {
    Valid: { label: 'Valid', tone: 'success' },
  },
  // Reconciliation tasks and outbox events share one state machine. "Queued" is still automatic; only "Needs review"
  // waits for a person, and "Completed" is the only resolved state.
  work: {
    Pending: { label: 'Queued', tone: 'info' },
    Processing: { label: 'Processing', tone: 'info' },
    Done: { label: 'Completed', tone: 'success' },
    NeedsReview: { label: 'Needs review', tone: 'danger' },
  },
  // A refund counts as complete only in "Succeeded", which the backend sets once the provider confirmed it.
  refund: {
    Requested: { label: 'Requested', tone: 'info' },
    Unknown: { label: 'Outcome unconfirmed', tone: 'warning' },
    Pending: { label: 'Awaiting provider', tone: 'warning' },
    Succeeded: { label: 'Refunded (provider confirmed)', tone: 'success' },
    Failed: { label: 'Provider refused', tone: 'danger' },
    NeedsReview: { label: 'Needs review', tone: 'danger' },
    // Not "provider confirmed": a person recorded it. Deliberately a different label and tone from Succeeded.
    ManuallyRecorded: { label: 'Refunded (manually recorded)', tone: 'info' },
  },
  // A verified provider success that contradicts a recorded failure. "Resolved" is an administrator's statement that the
  // money was returned elsewhere: it is never described as a provider-confirmed refund.
  exception: {
    Open: { label: 'Needs an administrator', tone: 'danger' },
    Resolved: { label: 'Resolved (administrator statement)', tone: 'info' },
  },
};

/**
 * Labels for orders and payments made through a REAL provider: the "(simulated)" qualifier belongs only to the
 * development/test simulator, and a real payment must never be described as simulated (nor the reverse).
 */
const REAL_PAYMENT_LABELS: Partial<Record<StatusKind, Record<string, StatusView>>> = {
  order: {
    Paid: { label: 'Paid', tone: 'success' },
    RefundRequired: { label: 'Refund pending', tone: 'warning' },
    Refunded: { label: 'Refunded', tone: 'info' },
  },
  payment: {
    Succeeded: { label: 'Succeeded', tone: 'success' },
  },
};

/** `simulated` is false for a payment made through a real provider (default true keeps the simulator wording). */
export function statusView(
  kind: StatusKind,
  value: string | null | undefined,
  simulated = true,
): StatusView {
  const raw = value ?? '';
  if (!simulated) {
    const real = REAL_PAYMENT_LABELS[kind]?.[raw];
    if (real) {
      return real;
    }
  }
  return TABLES[kind][raw] ?? { label: raw || 'Unknown', tone: 'neutral' };
}

const TASK_KINDS: Record<string, string> = {
  FinalizeSuccessfulPayment: 'Finalize successful payment',
  ResolveStaleAttempt: 'Resolve stale payment attempt',
  ExpireOrder: 'Expire unpaid order',
};

const OUTBOX_TYPES: Record<string, string> = {
  IssueTickets: 'Issue tickets',
  NotifyOrderPaid: 'Send order confirmation',
};

const REFUND_REASONS: Record<string, string> = {
  PaymentAfterSaleFailed: 'Paid after the seats were lost',
  AdminRequested: 'Requested by an administrator',
};

const AUDIT_ACTIONS: Record<string, string> = {
  RetryReconciliationTask: 'Retried a reconciliation task',
  RetryOutboxEvent: 'Retried an outbox event',
  RetryRefund: 'Retried a refund',
  RequestRefund: 'Requested a refund',
  RecordManualRefund: 'Recorded a manual refund (not provider-confirmed)',
  ResolvePaymentAnomaly:
    'Recorded the outcome of an investigation (administrator statement, not provider-verified)',
  ResolvePaymentException:
    'Recorded that a payment exception was refunded outside the system (not provider-verified)',
};

const SUBJECT_TYPES: Record<string, string> = {
  ReconciliationTask: 'Reconciliation task',
  OutboxEvent: 'Outbox event',
  Refund: 'Refund',
  Order: 'Order',
  PaymentException: 'Payment exception',
  PaymentAnomaly: 'Payment anomaly',
};

const ANOMALY_KINDS: Record<string, string> = {
  AmountOrCurrencyMismatch: 'Amount or currency does not match the order',
  ReferenceMismatch: 'Provider reference does not match the attempt',
  UnknownPaymentReference: 'Payment for an attempt this system does not know',
  InconsistentAssociation: 'Event names another provider’s attempt or another order',
};

const ANOMALY_RESOLUTIONS: Record<string, string> = {
  NotOurPayment: 'Not our payment (confirmed with the provider)',
  RefundedExternally: 'Money returned outside this system',
  NoActionNeeded: 'Investigated, no action needed',
};

const FACT_BASES: Record<string, string> = {
  ProviderVerified: 'Provider-verified fact',
  ApplicationDecision: 'Application decision',
  AdministratorAction: 'Administrator action',
  AdministratorStatement: 'Administrator statement (not provider-verified)',
  Unspecified: 'Not specified',
};

const TRANSITION_SOURCES: Record<string, string> = {
  ProviderWebhook: 'Provider webhook (signature verified)',
  ReconciliationLookup: 'Provider status lookup',
  Worker: 'Background worker',
  Administrator: 'Administrator',
  Customer: 'Customer',
  Simulator: 'Payment simulator',
  System: 'System',
  Unspecified: 'Not specified',
};

const ENTITY_TYPES: Record<string, string> = {
  Order: 'Order',
  PaymentAttempt: 'Payment attempt',
  Refund: 'Refund',
  PaymentException: 'Payment exception',
  PaymentAnomaly: 'Payment anomaly',
};

/** Unknown values are shown as the backend sent them, never hidden. */
const lookup = (table: Record<string, string>, value: string | null | undefined): string =>
  table[value ?? ''] ?? (value || 'Unknown');

export const anomalyKindLabel = (v: string | null | undefined) => lookup(ANOMALY_KINDS, v);
export const anomalyResolutionLabel = (v: string | null | undefined) =>
  lookup(ANOMALY_RESOLUTIONS, v);
export const factBasisLabel = (v: string | null | undefined) => lookup(FACT_BASES, v);
export const transitionSourceLabel = (v: string | null | undefined) =>
  lookup(TRANSITION_SOURCES, v);
export const entityTypeLabel = (v: string | null | undefined) => lookup(ENTITY_TYPES, v);
export const taskKindLabel = (v: string | null | undefined) => lookup(TASK_KINDS, v);
export const outboxTypeLabel = (v: string | null | undefined) => lookup(OUTBOX_TYPES, v);
export const refundReasonLabel = (v: string | null | undefined) => lookup(REFUND_REASONS, v);
export const auditActionLabel = (v: string | null | undefined) => lookup(AUDIT_ACTIONS, v);
export const subjectTypeLabel = (v: string | null | undefined) => lookup(SUBJECT_TYPES, v);

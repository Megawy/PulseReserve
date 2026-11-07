import {
  OperationsOrder,
  OperationsProvider,
  OperationsRefund,
  PaymentAnomaly,
  PaymentException,
} from '../../../core/api/admin-payments.models';
import { statusView } from '../../../shared/presentation';

/**
 * What an operator may do with an item, mirroring the server's own eligibility rules (WorkflowStore.cs). The UI
 * only decides which buttons to offer; the API re-checks the state under a row lock and answers 409 when it differs.
 */

/** Reconciliation tasks and outbox events: only "Needs review" can be re-queued (back to Queued, attempts reset). */
export function canRetryWork(state: string): boolean {
  return state === 'NeedsReview';
}

/**
 * Refunds: "Provider refused" can be resubmitted; "Needs review" is re-checked with the provider first. Queued,
 * in-flight and unconfirmed refunds are advanced by the worker on its own, and a succeeded refund is final.
 */
export function canRetryRefund(state: string): boolean {
  return state === 'Failed' || state === 'NeedsReview';
}

/** Only an order that already needs a refund and has no refund record yet can have one requested. */
export function canRequestRefund(order: OperationsOrder): boolean {
  return order.status === 'RefundRequired' && order.refundId === null;
}

/**
 * Recording a manual completion is offered only for a refund waiting for review whose provider is KNOWN not to refund through
 * the API. The server re-checks all of it; an unknown provider or an unavailable directory offers nothing.
 */
export function canRecordManualRefund(
  refund: Pick<OperationsRefund, 'provider' | 'state'>,
  providers: readonly OperationsProvider[],
): boolean {
  return refund.state === 'NeedsReview' && providerCannotRefund(refund.provider, providers);
}

/** A refund is complete (provider-confirmed) only when the backend reports the Succeeded terminal state. */
export function isRefundComplete(
  refund: Pick<OperationsRefund, 'state' | 'completedAtUtc'>,
): boolean {
  return refund.state === 'Succeeded' && refund.completedAtUtc !== null;
}

/**
 * What a successful "request refund" call may claim. The call is idempotent, so it can return a refund that already exists
 * in any state: a provider-confirmed one, one an administrator recorded manually, or one still in progress. Only the first
 * is described as confirmed by the provider.
 */
export function refundRequestMessage(
  refund: Pick<OperationsRefund, 'state' | 'completedAtUtc'>,
): string {
  if (isRefundComplete(refund)) {
    return 'The refund was already confirmed by the provider.';
  }
  if (refund.state === 'ManuallyRecorded') {
    return 'This refund already exists and was recorded as completed manually by an administrator. The provider has not confirmed it.';
  }
  return `Refund requested (${statusView('refund', refund.state).label}). It is not complete until the provider confirms it.`;
}

/** One short line saying what happens next to a task or outbox event, so a missing button is never a mystery. */
export function workGuidance(state: string): string {
  switch (state) {
    case 'Pending':
      return 'Waiting for the next worker pass; retried automatically.';
    case 'Processing':
      return 'Being handled right now.';
    case 'Done':
      return 'Finished; nothing to do.';
    case 'NeedsReview':
      return 'Automatic retries are exhausted. Re-queue it after checking the cause.';
    default:
      return '';
  }
}

export function refundGuidance(state: string): string {
  switch (state) {
    case 'Requested':
      return 'Waiting to be sent to the payment provider.';
    case 'Unknown':
      return 'The provider has not confirmed the outcome; the worker is checking.';
    case 'Pending':
      return 'Accepted by the provider; waiting for confirmation.';
    case 'Succeeded':
      return 'Confirmed by the provider; final.';
    case 'Failed':
      return 'The provider refused the refund. Nothing was returned to the customer.';
    case 'NeedsReview':
      return 'The outcome could not be confirmed automatically.';
    case 'ManuallyRecorded':
      return 'Recorded by an administrator as refunded outside this app; not confirmed by the provider. Final.';
    default:
      return '';
  }
}

/** The consequence shown in the confirmation dialog for a refund retry. */
export function refundRetryConsequence(
  refund: Pick<OperationsRefund, 'state' | 'submissionNumber'>,
): string {
  return refund.state === 'Failed'
    ? `The provider refused submission ${refund.submissionNumber}. Retrying sends a new submission for the same amount.`
    : 'The earlier outcome could not be confirmed. The system first asks the provider whether the refund exists and resubmits only if the provider has no record of it, so the customer cannot be refunded twice.';
}

/** The server names the only supported step; the console never offers one the server did not name. */
export function canResolveException(exception: Pick<PaymentException, 'nextAction'>): boolean {
  return exception.nextAction === 'RefundExternallyThenRecord';
}

/**
 * An anomaly offers one step, and only the one the server names: record the outcome of an investigation. It never offers to
 * pay the order, issue tickets or refund the event: the event was not applied and the console cannot apply it.
 */
export function canInvestigateAnomaly(anomaly: Pick<PaymentAnomaly, 'nextAction'>): boolean {
  return anomaly.nextAction === 'InvestigateThenRecord';
}

/**
 * What an exception means for its order, in words that claim nothing beyond the records: the payment is never applied to the
 * order, and a different payment's refund is a different payment's refund.
 */
export function exceptionOrderNote(
  exception: Pick<PaymentException, 'orderStatus' | 'state'>,
): string {
  switch (exception.orderStatus) {
    case 'PendingPayment':
      return 'Still awaiting payment. This payment was not applied, and this order can no longer be paid: the customer must start a new order.';
    case 'Cancelled':
    case 'Expired':
      return 'Closed without being paid. This payment was not applied and the order will not be fulfilled.';
    case 'Paid':
      return 'Paid by a different payment, so this one is an additional charge.';
    case 'RefundRequired':
    case 'Refunded':
      return 'Has its own refund record for a different payment; this payment is separate.';
    default:
      return 'This payment was not applied to the order.';
  }
}

const MAX_ERROR_CHARS = 200;

/** Server error text is shown verbatim but capped so one noisy provider message cannot blow up a table row. */
export function shortError(message: string | null): string | null {
  if (!message) {
    return null;
  }
  return message.length > MAX_ERROR_CHARS ? `${message.slice(0, MAX_ERROR_CHARS)}…` : message;
}

const find = (providers: readonly OperationsProvider[], name: string | null | undefined) =>
  providers.find((p) => p.name === name);

/**
 * True for an unfinished refund whose provider cannot refund through the API (for example Paymob): the system moves it to
 * "needs review" and only a person can refund in the provider's dashboard. Retry would only send it back to the same
 * place; the way forward is to refund in the dashboard and then record that here. Unknown providers are never assumed manual.
 */
export function refundNeedsManualAction(
  refund: Pick<OperationsRefund, 'provider' | 'state'>,
  providers: readonly OperationsProvider[],
): boolean {
  const provider = find(providers, refund.provider);
  return (
    !!provider &&
    !provider.supportsRefunds &&
    refund.state !== 'Succeeded' &&
    refund.state !== 'ManuallyRecorded'
  );
}

/** True when the provider of an attempt cannot be asked for its payment status. */
export function paymentLookupUnsupported(
  providerName: string | null | undefined,
  providers: readonly OperationsProvider[],
): boolean {
  const provider = find(providers, providerName);
  return !!provider && !provider.supportsPaymentLookup;
}

/** What the operator should do about a refund that only a person can complete. */
export function manualRefundGuidance(providerName: string): string {
  return `Refund this payment in the ${providerName} dashboard first. This app cannot confirm it; once it is done, record it here.`;
}

/** What the operator should do about a payment attempt whose provider cannot be queried. */
export function unqueryableAttemptGuidance(providerName: string): string {
  return `${providerName} cannot be asked for a payment's status. Check the transaction in the ${providerName} dashboard; a verified callback from ${providerName} completes it automatically.`;
}

/** The label shown for a payment's provider: the simulator is "Simulated", a real provider is named. */
export function providerLabel(
  name: string | null | undefined,
  simulated: boolean | null | undefined,
): string {
  return simulated ? 'Simulated' : (name ?? 'Unknown provider');
}

/** True when a payment's provider is known and cannot refund through the API (so a requested refund waits for a person). */
export function providerCannotRefund(
  providerName: string | null | undefined,
  providers: readonly OperationsProvider[],
): boolean {
  const provider = find(providers, providerName);
  return !!provider && !provider.supportsRefunds;
}

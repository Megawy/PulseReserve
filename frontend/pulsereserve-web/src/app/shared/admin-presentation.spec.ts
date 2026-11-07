import { shortId } from './format';
import {
  auditActionLabel,
  outboxTypeLabel,
  refundReasonLabel,
  statusView,
  subjectTypeLabel,
  taskKindLabel,
} from './presentation';

describe('operations status labels', () => {
  it.each([
    ['work', 'Pending', 'Queued', 'info'],
    ['work', 'Processing', 'Processing', 'info'],
    ['work', 'Done', 'Completed', 'success'],
    ['work', 'NeedsReview', 'Needs review', 'danger'],
    ['refund', 'Requested', 'Requested', 'info'],
    ['refund', 'Unknown', 'Outcome unconfirmed', 'warning'],
    ['refund', 'Pending', 'Awaiting provider', 'warning'],
    ['refund', 'Succeeded', 'Refunded (provider confirmed)', 'success'],
    ['refund', 'Failed', 'Provider refused', 'danger'],
    ['refund', 'NeedsReview', 'Needs review', 'danger'],
  ] as const)('%s %s -> %s', (kind, value, label, tone) => {
    expect(statusView(kind, value)).toEqual({ label, tone });
  });

  it('only a provider-confirmed refund reads as refunded', () => {
    const refunded = ['Requested', 'Unknown', 'Pending', 'Failed', 'NeedsReview']
      .map((s) => statusView('refund', s).label.toLowerCase())
      .filter((label) => label.includes('refunded'));
    expect(refunded).toEqual([]);
  });

  it('shows a state it does not know verbatim', () => {
    expect(statusView('work', 'Mystery')).toEqual({ label: 'Mystery', tone: 'neutral' });
  });
});

describe('operations value labels', () => {
  it('names known task kinds, outbox types, refund reasons and audit values', () => {
    expect(taskKindLabel('FinalizeSuccessfulPayment')).toBe('Finalize successful payment');
    expect(taskKindLabel('ExpireOrder')).toBe('Expire unpaid order');
    expect(outboxTypeLabel('IssueTickets')).toBe('Issue tickets');
    expect(outboxTypeLabel('NotifyOrderPaid')).toBe('Send order confirmation');
    expect(refundReasonLabel('AdminRequested')).toBe('Requested by an administrator');
    expect(auditActionLabel('RetryRefund')).toBe('Retried a refund');
    expect(subjectTypeLabel('OutboxEvent')).toBe('Outbox event');
  });

  it('falls back to the raw value, or Unknown when there is none', () => {
    expect(taskKindLabel('BrandNew')).toBe('BrandNew');
    expect(auditActionLabel(null)).toBe('Unknown');
  });

  it('shortens identifiers for dense tables', () => {
    expect(shortId('0a1b2c3d-1111-2222-3333-444444444444')).toBe('0a1b2c3d');
    expect(shortId(null)).toBe('');
  });
});

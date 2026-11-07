import { OperationsOrder } from '../../../core/api/admin-payments.models';
import {
  canRequestRefund,
  canRetryRefund,
  canRetryWork,
  isRefundComplete,
  refundRetryConsequence,
  shortError,
  workGuidance,
} from './ops-rules';

const order = (over: Partial<OperationsOrder> = {}): OperationsOrder => ({
  orderId: 'o',
  status: 'RefundRequired',
  totalAmount: 10,
  currency: 'USD',
  createdAtUtc: '2030-01-01T00:00:00Z',
  updatedAtUtc: '2030-01-01T00:00:00Z',
  refundId: null,
  refundState: null,
  paymentProvider: 'Simulator',
  paymentIsSimulated: true,
  ...over,
});

describe('operations eligibility rules', () => {
  it('re-queues tasks and outbox events only from Needs review', () => {
    expect(['Pending', 'Processing', 'Done', 'NeedsReview'].filter(canRetryWork)).toEqual([
      'NeedsReview',
    ]);
  });

  it('retries refunds only when the provider refused or the outcome needs review', () => {
    expect(
      ['Requested', 'Unknown', 'Pending', 'Succeeded', 'Failed', 'NeedsReview'].filter(
        canRetryRefund,
      ),
    ).toEqual(['Failed', 'NeedsReview']);
  });

  it('offers a refund request only for a RefundRequired order with no refund yet', () => {
    expect(canRequestRefund(order())).toBe(true);
    expect(canRequestRefund(order({ refundId: 'r1', refundState: 'Pending' }))).toBe(false);
    expect(canRequestRefund(order({ status: 'Paid' }))).toBe(false);
  });

  it('counts a refund as complete only when the provider-confirmed terminal state is reported', () => {
    expect(isRefundComplete({ state: 'Succeeded', completedAtUtc: '2030-01-01T00:00:00Z' })).toBe(
      true,
    );
    expect(isRefundComplete({ state: 'Succeeded', completedAtUtc: null })).toBe(false);
    expect(isRefundComplete({ state: 'Pending', completedAtUtc: null })).toBe(false);
    expect(isRefundComplete({ state: 'NeedsReview', completedAtUtc: '2030-01-01T00:00:00Z' })).toBe(
      false,
    );
  });

  it('explains what happens to items that have no action', () => {
    expect(workGuidance('Pending')).toContain('automatically');
    expect(workGuidance('Done')).toContain('nothing to do');
    expect(workGuidance('weird')).toBe('');
  });

  it('describes the real consequence of retrying a refused versus an unconfirmed refund', () => {
    expect(refundRetryConsequence({ state: 'Failed', submissionNumber: 2 })).toContain(
      'new submission',
    );
    expect(refundRetryConsequence({ state: 'NeedsReview', submissionNumber: 1 })).toContain(
      'asks the provider',
    );
  });

  it('caps very long server error text', () => {
    expect(shortError(null)).toBeNull();
    expect(shortError('short')).toBe('short');
    expect(shortError('x'.repeat(500))?.length).toBe(201);
  });
});

import { statusView } from './presentation';
import { dateTile, formatDateRange, formatMoney, formatTime, isValidDate } from './format';

describe('statusView', () => {
  it.each([
    ['order', 'Paid', 'Paid (simulated)', 'success'],
    ['order', 'PendingPayment', 'Awaiting payment', 'warning'],
    ['order', 'RefundRequired', 'Refund pending (simulated)', 'warning'],
    ['order', 'Refunded', 'Refunded (simulated)', 'info'],
    ['payment', 'Failed', 'Failed', 'danger'],
    ['payment', 'Initiated', 'In progress', 'info'],
    ['event', 'OnSale', 'On sale', 'success'],
    ['event', 'Published', 'Coming soon', 'brand'],
    ['reservation', 'Checkout', 'In checkout', 'brand'],
    ['ticket', 'Valid', 'Valid', 'success'],
  ] as const)('%s %s -> %s', (kind, value, label, tone) => {
    expect(statusView(kind, value)).toEqual({ label, tone });
  });

  it('shows an unknown state verbatim instead of hiding or mislabelling it', () => {
    expect(statusView('order', 'SomethingNew')).toEqual({ label: 'SomethingNew', tone: 'neutral' });
    expect(statusView('order', null)).toEqual({ label: 'Unknown', tone: 'neutral' });
  });

  it('never presents simulated payments as real money', () => {
    expect(statusView('order', 'Paid').label).toContain('simulated');
    expect(statusView('payment', 'Succeeded').label).toContain('simulated');
  });
});

describe('format helpers', () => {
  it('formats money with the currency, falling back for unknown codes', () => {
    expect(formatMoney(12.5, 'USD')).toBe(
      new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD' }).format(12.5),
    );
    expect(formatMoney(3, 'NOT-A-CODE')).toBe('3.00 NOT-A-CODE');
  });

  it('validates and formats dates defensively', () => {
    expect(isValidDate('2030-03-01T20:00:00Z')).toBe(true);
    expect(isValidDate('nope')).toBe(false);
    expect(formatTime('nope')).toBe('');
    expect(dateTile('nope')).toBeNull();
    expect(dateTile('2030-03-15T12:00:00Z')).not.toBeNull();
  });

  it('shows the end time only when it is valid', () => {
    expect(formatDateRange('2030-03-01T20:00:00Z', null)).not.toContain('–');
    expect(formatDateRange('2030-03-01T20:00:00Z', '2030-03-01T23:00:00Z')).toContain('–');
  });
});

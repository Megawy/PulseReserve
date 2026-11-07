import { TestBed } from '@angular/core/testing';
import { statusView } from './presentation';
import { StatusBadge } from './ui/status-badge/status-badge';

describe('payment wording for real versus simulated payments', () => {
  it.each([
    ['order', 'Paid', 'Paid', 'Paid (simulated)'],
    ['order', 'RefundRequired', 'Refund pending', 'Refund pending (simulated)'],
    ['order', 'Refunded', 'Refunded', 'Refunded (simulated)'],
    ['payment', 'Succeeded', 'Succeeded', 'Succeeded (simulated)'],
  ] as const)('%s %s: real "%s", simulated "%s"', (kind, value, real, simulated) => {
    expect(statusView(kind, value, false).label).toBe(real);
    expect(statusView(kind, value, true).label).toBe(simulated);
    expect(statusView(kind, value).label).toBe(simulated); // the default keeps the simulator wording
  });

  it('never calls a real payment simulated, and leaves unaffected states alone', () => {
    for (const value of ['Paid', 'RefundRequired', 'Refunded', 'PendingPayment', 'Cancelled']) {
      expect(statusView('order', value, false).label.toLowerCase()).not.toContain('simulated');
    }
    expect(statusView('order', 'PendingPayment', false).label).toBe('Awaiting payment');
    expect(statusView('order', 'Mystery', false).label).toBe('Mystery');
  });

  it('the status badge follows the payment kind', async () => {
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.componentRef.setInput('kind', 'order');
    fixture.componentRef.setInput('value', 'Paid');
    fixture.componentRef.setInput('simulated', false);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toBe('Paid');

    fixture.componentRef.setInput('simulated', true);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toBe('Paid (simulated)');
  });
});

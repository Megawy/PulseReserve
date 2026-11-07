import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Order } from '../../core/api/order-api.models';
import { formatMoney } from '../format';
import { Alert } from './alert/alert';
import { OrderSummary } from './order-summary/order-summary';
import { StatusBadge } from './status-badge/status-badge';
import { TicketList } from './ticket-list/ticket-list';
import { ToastService } from './toast/toast.service';

@Component({
  imports: [Alert],
  template: '<app-alert [tone]="tone()" heading="Heads up">Body</app-alert>',
})
class AlertHost {
  tone = signal<'error' | 'info'>('error');
}

describe('Alert', () => {
  it('announces errors assertively and other tones politely, with a text icon not just colour', async () => {
    const fixture = TestBed.createComponent(AlertHost);
    await fixture.whenStable();
    const el = fixture.nativeElement.querySelector('app-alert') as HTMLElement;
    expect(el.getAttribute('role')).toBe('alert');
    expect(el.className).toContain('alert--error');
    expect(el.textContent).toContain('Heads up');
    expect(el.querySelector('.alert__icon')?.textContent?.trim()).toBe('!');

    fixture.componentInstance.tone.set('info');
    await fixture.whenStable();
    expect(el.getAttribute('role')).toBe('status');
  });
});

describe('StatusBadge', () => {
  it('renders the shared label and tone class', async () => {
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.componentRef.setInput('kind', 'order');
    fixture.componentRef.setInput('value', 'Paid');
    await fixture.whenStable();
    const badge = fixture.nativeElement.querySelector('.badge') as HTMLElement;
    expect(badge.textContent).toBe('Paid (simulated)');
    expect(badge.className).toContain('badge--success');
  });
});

describe('ToastService', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('auto-dismisses, keeps errors longer, and caps the stack', () => {
    const toasts = TestBed.inject(ToastService);
    toasts.success('ok');
    toasts.error('bad');
    expect(toasts.toasts()).toHaveLength(2);
    vi.advanceTimersByTime(5100);
    expect(toasts.toasts().map((t) => t.text)).toEqual(['bad']);
    vi.advanceTimersByTime(4000);
    expect(toasts.toasts()).toHaveLength(0);

    for (let i = 0; i < 8; i++) {
      toasts.show(`t${i}`);
    }
    expect(toasts.toasts().length).toBeLessThanOrEqual(4);
  });

  it('can be dismissed by hand', () => {
    const toasts = TestBed.inject(ToastService);
    toasts.show('hello');
    toasts.dismiss(toasts.toasts()[0].id);
    expect(toasts.toasts()).toHaveLength(0);
  });
});

const order: Order = {
  orderId: 'abcdef12-0000-4000-8000-000000000000',
  reservationId: 'r',
  eventId: 'e',
  status: 'Paid',
  currency: 'USD',
  totalAmount: 91,
  createdAtUtc: '2030-01-01T10:00:00Z',
  paymentDeadlineUtc: '2030-01-01T10:10:00Z',
  lines: [
    { seatId: 's1', quantity: 1, unitPrice: 45.5, lineTotal: 45.5 },
    { seatId: 's2', quantity: 1, unitPrice: 45.5, lineTotal: 45.5 },
  ],
  payment: {
    attemptId: 'a',
    attemptNumber: 2,
    status: 'Succeeded',
    provider: 'Simulator',
    isSimulated: true,
    simulatorToken: null,
    createdAtUtc: 'x',
    completedAtUtc: null,
  },
};

describe('OrderSummary and TicketList', () => {
  it('shows only server-provided amounts, the shared status label and seat names', async () => {
    const fixture = TestBed.createComponent(OrderSummary);
    fixture.componentRef.setInput('order', order);
    fixture.componentRef.setInput('seatNames', new Map([['s1', 'Section A · Row 1 · Seat 1']]));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="order-status"]')?.textContent).toBe('Paid (simulated)');
    expect(el.querySelector('[data-testid="attempt-status"]')?.textContent).toBe(
      '#2 Succeeded (simulated)',
    );
    expect(el.querySelector('[data-testid="order-total"]')?.textContent).toBe(
      formatMoney(91, 'USD'),
    );
    const rows = el.querySelectorAll('.line');
    expect(rows[0].textContent).toContain('Section A · Row 1 · Seat 1');
    expect(rows[1].textContent).toContain('Seat s2'); // unknown seat falls back to a short id, never blank
    expect(el.textContent).toContain('ABCDEF12');
  });

  it('shows the pay-within countdown only while payment is pending', async () => {
    const fixture = TestBed.createComponent(OrderSummary);
    fixture.componentRef.setInput('order', { ...order, status: 'PendingPayment' });
    fixture.componentRef.setInput('remaining', '9:30');
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('9:30');
  });

  it('lists tickets with a short or full public id', async () => {
    const fixture = TestBed.createComponent(TicketList);
    fixture.componentRef.setInput('tickets', [
      {
        ticketId: 'abcdefghijklmnopqrstuvwxyz',
        seatId: 's1',
        eventId: 'e',
        status: 'Valid',
        issuedAtUtc: 'x',
      },
    ]);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="ticket-id"]')?.textContent).toBe('abcdefghijkl…');
    fixture.componentRef.setInput('full', true);
    await fixture.whenStable();
    expect(el.querySelector('[data-testid="ticket-id"]')?.textContent).toBe(
      'abcdefghijklmnopqrstuvwxyz',
    );
  });
});

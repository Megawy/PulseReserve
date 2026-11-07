import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Order } from '../../../core/api/order-api.models';
import { formatMoney } from '../../../shared/format';
import { BookingFlow } from '../booking/booking-flow';
import { CheckoutFlow } from '../booking/checkout-flow';
import { CheckoutPanel } from './checkout-panel';

function order(over: Partial<Order> = {}): Order {
  return {
    orderId: '00000000-0000-4000-8000-0000000000b1',
    reservationId: 'res-1',
    eventId: 'e1',
    status: 'PendingPayment',
    currency: 'USD',
    totalAmount: 91,
    createdAtUtc: '2026-10-21T10:00:00Z',
    paymentDeadlineUtc: '2026-10-21T10:10:00Z',
    lines: [
      { seatId: 's1', quantity: 1, unitPrice: 45.5, lineTotal: 45.5 },
      { seatId: 's2', quantity: 1, unitPrice: 45.5, lineTotal: 45.5 },
    ],
    payment: null,
    ...over,
  };
}

describe('CheckoutPanel', () => {
  let flowStub: Record<string, unknown>;

  function render(o: Order | null, loaded = true) {
    TestBed.resetTestingModule();
    const orderSignal = signal(o);
    const attempt = signal(o?.payment ?? null);
    flowStub = {
      order: orderSignal,
      attempt,
      loaded: signal(loaded),
      busy: signal('idle'),
      notice: signal(null),
      isBusy: () => false,
      paymentInFlight: () => attempt()?.status === 'Initiated',
      canPay: () => o?.status === 'PendingPayment' && attempt()?.status !== 'Initiated',
      canCancel: () => o?.status === 'PendingPayment',
      simulatorReady: () => attempt()?.status === 'Initiated' && attempt()?.isSimulated === true,
      optionsStatus: () => 'ready',
      providers: () => [
        { provider: 'Simulator', displayName: 'Payment simulator', isSimulated: true },
      ],
      selectedProvider: () => 'Simulator',
      selectedOption: () =>
        attempt()?.isSimulated === false
          ? null
          : { provider: 'Simulator', displayName: 'Payment simulator', isSimulated: true },
      noProvider: () => false,
      heldForReview: () => false,
      ineligibleReason: () => null,
      mustChoose: () => false,
      awaitingProvider: () => false,
      loadOptions: vi.fn(),
      selectProvider: vi.fn(),
      continueToPayment: vi.fn(),
      load: vi.fn(),
      tickets: signal([] as { ticketId: string; seatId: string }[]),
      ticketsLoading: signal(false),
      loadTickets: vi.fn(),
      refresh: vi.fn(),
      createOrder: vi.fn(),
      startPayment: vi.fn(),
      simulate: vi.fn(),
      cancel: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: CheckoutFlow, useValue: flowStub },
        { provide: BookingFlow, useValue: { phase: signal('checkout') } },
      ],
    });
    const fixture = TestBed.createComponent(CheckoutPanel);
    fixture.componentRef.setInput('reservationId', 'res-1');
    fixture.componentRef.setInput(
      'seatNames',
      new Map([
        ['s1', 'Section A · Row 1 · Seat 1'],
        ['s2', 'Section A · Row 1 · Seat 2'],
      ]),
    );
    fixture.componentRef.setInput('remaining', '9:30');
    return fixture;
  }

  it('shows the server-provided lines, quantities, currency and total with real seat names', async () => {
    const fixture = render(order());
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    const rows = el.querySelectorAll('.line');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Section A · Row 1 · Seat 1');
    expect(rows[0].textContent).toContain(formatMoney(45.5, 'USD'));
    expect(el.querySelector('[data-testid="order-total"]')?.textContent).toBe(
      formatMoney(91, 'USD'),
    );
    expect(el.textContent).toContain('Total (USD)');
    expect(el.textContent).toContain('Awaiting payment');
    expect(el.textContent).toContain('9:30');
  });

  it('labels the flow as a simulator and collects no card data', async () => {
    const fixture = render(order());
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.sim-banner')?.textContent).toContain('no real money');
    // The only inputs are the payment-method radios: nothing that could take card details.
    expect(el.querySelector('input:not([type="radio"]), textarea, select')).toBeNull();
  });

  it('offers payment first, then the simulator controls for a simulated in-flight attempt', async () => {
    const fixture = render(order());
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Pay now (simulator)');
    expect(el.textContent).not.toContain('Simulate successful payment');

    const inFlight = render(
      order({
        payment: {
          attemptId: 'a1',
          attemptNumber: 1,
          status: 'Initiated',
          provider: 'Simulator',
          isSimulated: true,
          simulatorToken: 'sim_x',
          createdAtUtc: '2026-10-21T10:00:00Z',
          completedAtUtc: null,
        },
      }),
    );
    await inFlight.whenStable();
    const el2 = inFlight.nativeElement as HTMLElement;
    expect(el2.textContent).toContain('Simulate successful payment');
    expect(el2.textContent).toContain('Simulate failed payment');
    expect(el2.textContent).not.toContain('Pay now (simulator)');
    el2.querySelectorAll<HTMLButtonElement>('fieldset button')[0].click();
    expect(flowStub['simulate']).toHaveBeenCalledWith('succeed');
  });

  it.each([
    ['Paid', 'Paid (simulated)'],
    ['Expired', 'Expired'],
    ['Refunded', 'Refunded (simulated)'],
    ['Cancelled', 'Cancelled'],
    ['RefundRequired', 'Refund pending (simulated)'],
  ])('shows the %s state without payment controls', async (status, text) => {
    const fixture = render(order({ status }));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="order-status"]')?.textContent).toBe(text);
    expect(el.querySelector('.actions')).toBeNull();
  });

  it('offers to continue checkout when the reservation is in checkout but has no order yet', async () => {
    const fixture = render(null, true);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector<HTMLButtonElement>('button')!.click();
    expect(flowStub['createOrder']).toHaveBeenCalledWith('res-1');
  });

  it('looks up the order of the reservation once on arrival (refresh recovery)', async () => {
    const fixture = render(null, false);
    await fixture.whenStable();
    expect(flowStub['load']).toHaveBeenCalledWith('res-1');
  });

  it('lists the tickets of a paid order with a shortened public id and no private data', async () => {
    const fixture = render(order({ status: 'Paid' }));
    (flowStub['tickets'] as ReturnType<typeof signal>).set([
      { ticketId: 'abcdefghijklmnopqrstuvwxyz0123456789', seatId: 's1' },
    ]);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Section A · Row 1 · Seat 1');
    expect(el.querySelector('[data-testid="ticket-id"]')?.textContent).toBe('abcdefghijkl…');
    expect(el.textContent).not.toContain('abcdefghijklmnopqrstuvwxyz0123456789');
  });

  it('says tickets are being prepared and offers to check again when none exist yet', async () => {
    const fixture = render(order({ status: 'Paid' }));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Your tickets are being prepared');
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Check again'))!.click();
    expect(flowStub['loadTickets']).toHaveBeenCalled();
  });
});

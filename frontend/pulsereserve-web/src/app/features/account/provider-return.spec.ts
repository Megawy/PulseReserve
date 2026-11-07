import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';
import { CatalogApiClient } from '../../core/api/catalog-api.client';
import { OrderApiClient } from '../../core/api/order-api.client';
import { Order, PaymentAttempt } from '../../core/api/order-api.models';
import { OrderDetail, PAYMENT_CONFIRMATION_POLL } from './order-detail';

const ORDER = '00000000-0000-4000-8000-0000000000b1';

const attempt = (over: Partial<PaymentAttempt> = {}): PaymentAttempt => ({
  attemptId: 'a1',
  attemptNumber: 1,
  status: 'Initiated',
  provider: 'Stripe',
  isSimulated: false,
  simulatorToken: null,
  createdAtUtc: '2030-01-01T10:00:00Z',
  completedAtUtc: null,
  checkoutUrl: 'https://checkout.stripe.com/c/pay/cs_test_1',
  ...over,
});

const order = (over: Partial<Order> = {}): Order => ({
  orderId: ORDER,
  reservationId: 'r',
  eventId: 'e1',
  status: 'PendingPayment',
  currency: 'USD',
  totalAmount: 91,
  createdAtUtc: '2030-01-01T10:00:00Z',
  paymentDeadlineUtc: '2030-01-01T10:10:00Z',
  lines: [{ seatId: 's1', quantity: 1, unitPrice: 91, lineTotal: 91 }],
  payment: attempt(),
  ...over,
});

describe('returning from a payment provider', () => {
  let orders: Record<string, ReturnType<typeof vi.fn>>;
  let query: BehaviorSubject<{ get: (k: string) => string | null }>;

  function setup(payment: string | null, first: Order = order()) {
    query = new BehaviorSubject({ get: (k: string) => (k === 'payment' ? payment : null) });
    orders = {
      getOrder: vi.fn(() => of(first)),
      getTickets: vi.fn(() => of([])),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: PAYMENT_CONFIRMATION_POLL, useValue: { intervalMs: 5, maxPolls: 3 } },
        { provide: OrderApiClient, useValue: orders },
        {
          provide: CatalogApiClient,
          useValue: {
            getEvent: vi.fn(() =>
              of({
                id: 'e1',
                name: 'Concert',
                seatingLayoutId: null,
                scheduledStartUtc: '2030-03-01T20:00:00Z',
                scheduledEndUtc: '2030-03-01T23:00:00Z',
              }),
            ),
            getSeatingLayout: vi.fn(() => of(null)),
          },
        },
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of({ get: () => ORDER }), queryParamMap: query },
        },
      ],
    });
  }

  const text = (el: HTMLElement) => (el.textContent ?? '').replace(/\s+/g, ' ');

  it('says the payment is being confirmed, never that it is: returning proves nothing', async () => {
    setup('return');
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(text(el)).toContain('Confirming your payment');
    expect(text(el)).toContain('Returning here does not confirm it');
    expect(text(el)).not.toContain('Payment confirmed');
    expect(el.querySelector('[data-testid="order-status"]')?.textContent).toContain(
      'Awaiting payment',
    );
    expect(text(el)).not.toContain('Payment pending'); // one specific message, not two stacked ones
  });

  it('keeps asking the server, and shows the confirmation (and tickets) only once the order is Paid', async () => {
    setup('return');
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(orders['getOrder']).toHaveBeenCalledTimes(1);

    // The provider's webhook arrives: the next read says Paid.
    orders['getOrder'].mockReturnValue(
      of(order({ status: 'Paid', payment: attempt({ status: 'Succeeded', checkoutUrl: null }) })),
    );
    await vi.waitFor(async () => {
      await fixture.whenStable();
      expect(text(el)).toContain('The payment provider confirmed your payment');
    });

    expect(el.querySelector('[data-testid="order-status"]')?.textContent).toBe('Paid');
    expect(orders['getTickets']).toHaveBeenCalled();
  });

  it('stops polling after a few tries and leaves a manual "Check now"', async () => {
    setup('return');
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();

    // 1 initial read + 3 quiet re-reads (the test's limit), then it stops.
    await vi.waitFor(() => expect(orders['getOrder']).toHaveBeenCalledTimes(4));
    await new Promise((r) => setTimeout(r, 50)); // several intervals: nothing more is requested
    await fixture.whenStable();
    expect(orders['getOrder']).toHaveBeenCalledTimes(4);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('Confirming your payment');
    expect(
      [...el.querySelectorAll('button')].some((b) => b.textContent?.includes('Check now')),
    ).toBe(true);
  });

  it('a cancelled return says nothing is confirmed, and does not poll', async () => {
    setup('cancelled');
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();
    await new Promise((r) => setTimeout(r, 50)); // many poll intervals: a cancelled return never polls
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('You left the payment page');
    expect(text(el)).toContain('Nothing is confirmed until the payment provider says so');
    expect(orders['getOrder']).toHaveBeenCalledTimes(1);
  });

  it('shows no return notice without the return marker, or for a simulated payment', async () => {
    setup(null);
    const plain = TestBed.createComponent(OrderDetail);
    await plain.whenStable();
    expect(text(plain.nativeElement as HTMLElement)).not.toContain('Confirming your payment');

    TestBed.resetTestingModule();
    setup(
      'return',
      order({
        payment: attempt({
          isSimulated: true,
          provider: 'Simulator',
          checkoutUrl: null,
          simulatorToken: 'x',
        }),
      }),
    );
    const sim = TestBed.createComponent(OrderDetail);
    await sim.whenStable();
    expect(text(sim.nativeElement as HTMLElement)).not.toContain('Confirming your payment');
  });

  it('ignores any other value of the return marker', async () => {
    setup('success-trust-me');
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).not.toContain('Confirming your payment');
    expect(text(el)).not.toContain('Payment confirmed');
  });

  it('describes a refund of a real payment without simulated wording', async () => {
    setup(
      null,
      order({
        status: 'RefundRequired',
        payment: attempt({ status: 'Succeeded', checkoutUrl: null }),
      }),
    );
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(text(el)).toContain('complete only once the payment provider confirms it');
    expect(text(el)).not.toContain('simulated');
    expect(el.querySelector('[data-testid="order-status"]')?.textContent).toBe('Refund pending');
  });
});

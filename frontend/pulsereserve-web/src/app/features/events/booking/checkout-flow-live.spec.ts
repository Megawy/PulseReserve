import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { ApiError } from '../../../core/api/api-error';
import { OrderApiClient } from '../../../core/api/order-api.client';
import { Order, PaymentAttempt, Ticket } from '../../../core/api/order-api.models';
import { LiveNotice } from '../../../core/live/live.models';
import { LIVE_TIMING, LiveService } from '../../../core/live/live.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { BookingFlow } from './booking-flow';
import { CheckoutFlow } from './checkout-flow';

const RES = '00000000-0000-4000-8000-0000000000a1';
const ORDER = '00000000-0000-4000-8000-0000000000b1';
const OTHER_ORDER = '00000000-0000-4000-8000-0000000000b2';
const ATTEMPT = '00000000-0000-4000-8000-0000000000d1';

const attempt = (over: Partial<PaymentAttempt> = {}): PaymentAttempt => ({
  attemptId: ATTEMPT,
  attemptNumber: 1,
  status: 'Initiated',
  provider: 'Stripe',
  isSimulated: false,
  simulatorToken: null,
  createdAtUtc: '2026-10-21T10:00:00Z',
  completedAtUtc: null,
  ...over,
});

const order = (over: Partial<Order> = {}): Order => ({
  orderId: ORDER,
  reservationId: RES,
  eventId: 'e1',
  status: 'PendingPayment',
  currency: 'USD',
  totalAmount: 100,
  createdAtUtc: '2026-10-21T10:00:00Z',
  paymentDeadlineUtc: '2026-10-21T10:10:00Z',
  lines: [{ seatId: 's1', quantity: 1, unitPrice: 100, lineTotal: 100 }],
  payment: attempt(),
  ...over,
});

const orderNotice = (orderId = ORDER, status = 'Paid'): LiveNotice => ({
  kind: 'order',
  orderId,
  reservationId: RES,
  status,
  paymentStatus: 'Succeeded',
  ticketCount: 0,
});

describe('CheckoutFlow with live updates', () => {
  let api: {
    getOrder: ReturnType<typeof vi.fn>;
    getOrderByReservation: ReturnType<typeof vi.fn>;
    cancelOrder: ReturnType<typeof vi.fn>;
    getTickets: ReturnType<typeof vi.fn>;
    paymentOptions: ReturnType<typeof vi.fn>;
  };
  let booking: {
    recheckReservation: ReturnType<typeof vi.fn>;
    refreshAvailability: ReturnType<typeof vi.fn>;
  };
  let notices: Subject<LiveNotice>;
  let toasts: { show: ReturnType<typeof vi.fn>; success: ReturnType<typeof vi.fn> };
  let flow: CheckoutFlow;

  beforeEach(() => {
    vi.useFakeTimers();
    notices = new Subject<LiveNotice>();
    toasts = { show: vi.fn(), success: vi.fn() };
    api = {
      getOrder: vi.fn(() => of(order())),
      getOrderByReservation: vi.fn(() => of(order())),
      cancelOrder: vi.fn(() => of(order({ status: 'Cancelled' }))),
      getTickets: vi.fn(() => of([] as Ticket[])),
      paymentOptions: vi.fn(() => of({ currency: 'USD', providers: [] })),
    };
    booking = { recheckReservation: vi.fn(), refreshAvailability: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        CheckoutFlow,
        { provide: OrderApiClient, useValue: api },
        { provide: BookingFlow, useValue: { ...booking, phase: signal('checkout') } },
        { provide: ToastService, useValue: toasts },
        { provide: LiveService, useValue: { notices: notices.asObservable() } },
        { provide: LIVE_TIMING, useValue: { now: () => 1000 } },
      ],
    });
    flow = TestBed.inject(CheckoutFlow);
    flow.load(RES); // the order is on screen: PendingPayment with a provider attempt in flight
    api.getOrder.mockClear();
    booking.recheckReservation.mockClear();
  });

  afterEach(() => vi.useRealTimers());

  it('re-reads the order when a notice says it changed, and shows what the server answers', () => {
    api.getOrder.mockReturnValue(
      of(order({ status: 'Paid', payment: attempt({ status: 'Succeeded' }) })),
    );

    notices.next(orderNotice());
    expect(api.getOrder).not.toHaveBeenCalled(); // debounced
    vi.advanceTimersByTime(300);

    expect(api.getOrder).toHaveBeenCalledTimes(1);
    expect(flow.order()?.status).toBe('Paid');
    expect(flow.notice()?.text).toContain('Payment confirmed');
    expect(toasts.success).toHaveBeenCalledTimes(1);
    expect(booking.recheckReservation).toHaveBeenCalled();
  });

  it('never shows the notice as the state: a contradicting server answer wins', () => {
    api.getOrder.mockReturnValue(of(order())); // still pending

    notices.next(orderNotice(ORDER, 'Paid'));
    vi.advanceTimersByTime(300);

    expect(flow.order()?.status).toBe('PendingPayment');
    expect(toasts.success).not.toHaveBeenCalled();
  });

  it('does not flicker the buttons: no busy state for a live read', () => {
    const pending = new Subject<Order>();
    api.getOrder.mockReturnValue(pending);

    notices.next(orderNotice());
    vi.advanceTimersByTime(300);

    expect(flow.busy()).toBe('idle');
  });

  it('does not repeat the confirmation when the order has not changed', () => {
    api.getOrder.mockReturnValue(
      of(order({ status: 'Paid', payment: attempt({ status: 'Succeeded' }) })),
    );
    notices.next(orderNotice());
    vi.advanceTimersByTime(300);
    expect(toasts.success).toHaveBeenCalledTimes(1);

    notices.next(orderNotice());
    vi.advanceTimersByTime(300);

    expect(api.getOrder).toHaveBeenCalledTimes(2);
    expect(toasts.success).toHaveBeenCalledTimes(1);
  });

  it('reads the tickets when a hint arrives for a paid order that has none yet', () => {
    api.getOrder.mockReturnValue(
      of(order({ status: 'Paid', payment: attempt({ status: 'Succeeded' }) })),
    );
    notices.next(orderNotice());
    vi.advanceTimersByTime(300);
    api.getTickets.mockClear();

    notices.next({ ...(orderNotice() as Extract<LiveNotice, { kind: 'order' }>), ticketCount: 1 });
    vi.advanceTimersByTime(300);

    expect(api.getTickets).toHaveBeenCalledTimes(1);
  });

  it('ignores a notice about another order', () => {
    notices.next(orderNotice(OTHER_ORDER));
    vi.advanceTimersByTime(1000);

    expect(api.getOrder).not.toHaveBeenCalled();
  });

  it('leaves the screen alone when the live read fails', () => {
    api.getOrder.mockReturnValue(
      throwError(() => new ApiError('network', 0, null, null, 'Could not reach the server.')),
    );

    notices.next(orderNotice());
    vi.advanceTimersByTime(300);

    expect(flow.order()?.status).toBe('PendingPayment');
    expect(flow.notice()).toBeNull();
  });

  it('waits for its own request to finish, then reads', () => {
    const cancelling = new Subject<Order>();
    api.cancelOrder.mockReturnValue(cancelling);
    api.getOrder.mockReturnValue(of(order({ status: 'Cancelled', payment: null })));
    flow.cancel();
    expect(flow.busy()).toBe('cancelling');

    notices.next(orderNotice(ORDER, 'Cancelled'));
    vi.advanceTimersByTime(300);
    expect(api.getOrder).not.toHaveBeenCalled(); // not while our own request runs

    cancelling.next(order({ status: 'Cancelled', payment: null }));

    expect(api.getOrder).toHaveBeenCalledTimes(1);
    expect(flow.busy()).toBe('idle');
  });

  it('re-reads after an announced gap, but not when there is no order yet', () => {
    notices.next({ kind: 'resync', reason: 'connected', at: 2000 });
    vi.advanceTimersByTime(300);
    expect(api.getOrder).toHaveBeenCalledTimes(1);

    flow.reset();
    api.getOrder.mockClear();
    notices.next({ kind: 'resync', reason: 'connected', at: 3000 });
    vi.advanceTimersByTime(300);
    expect(api.getOrder).not.toHaveBeenCalled();
  });
});

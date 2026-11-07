import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { ApiError } from '../../../core/api/api-error';
import { OrderApiClient } from '../../../core/api/order-api.client';
import { Order, PaymentAttempt } from '../../../core/api/order-api.models';
import { BookingFlow } from './booking-flow';
import { CheckoutFlow } from './checkout-flow';

const RES = '00000000-0000-4000-8000-0000000000a1';
const ORDER = '00000000-0000-4000-8000-0000000000b1';
const ATTEMPT = '00000000-0000-4000-8000-0000000000d1';

function attempt(over: Partial<PaymentAttempt> = {}): PaymentAttempt {
  return {
    attemptId: ATTEMPT,
    attemptNumber: 1,
    status: 'Initiated',
    provider: 'Simulator',
    isSimulated: true,
    simulatorToken: 'sim_token',
    createdAtUtc: '2026-10-21T10:00:00Z',
    completedAtUtc: null,
    ...over,
  };
}

function order(over: Partial<Order> = {}): Order {
  return {
    orderId: ORDER,
    reservationId: RES,
    eventId: 'e1',
    status: 'PendingPayment',
    currency: 'USD',
    totalAmount: 100,
    createdAtUtc: '2026-10-21T10:00:00Z',
    paymentDeadlineUtc: '2026-10-21T10:10:00Z',
    lines: [
      { seatId: 's1', quantity: 1, unitPrice: 50, lineTotal: 50 },
      { seatId: 's2', quantity: 1, unitPrice: 50, lineTotal: 50 },
    ],
    payment: null,
    ...over,
  };
}

const networkError = () => new ApiError('network', 0, null, null, 'Could not reach the server.');
const problem = (status: number, title: string, message: string) =>
  new ApiError('problem', status, title, message, message);

describe('CheckoutFlow', () => {
  let api: {
    createOrder: ReturnType<typeof vi.fn>;
    getOrder: ReturnType<typeof vi.fn>;
    getOrderByReservation: ReturnType<typeof vi.fn>;
    cancelOrder: ReturnType<typeof vi.fn>;
    startPayment: ReturnType<typeof vi.fn>;
    completeSimulatedPayment: ReturnType<typeof vi.fn>;
    getTickets: ReturnType<typeof vi.fn>;
    paymentOptions: ReturnType<typeof vi.fn>;
  };
  let booking: {
    recheckReservation: ReturnType<typeof vi.fn>;
    refreshAvailability: ReturnType<typeof vi.fn>;
  };
  let flow: CheckoutFlow;

  beforeEach(() => {
    api = {
      createOrder: vi.fn(() => of({ order: order(), replayed: false })),
      getOrder: vi.fn(() => of(order())),
      getOrderByReservation: vi.fn(() => of(order())),
      cancelOrder: vi.fn(() => of(order({ status: 'Cancelled' }))),
      getTickets: vi.fn(() => of([])),
      paymentOptions: vi.fn(() =>
        of({
          currency: 'USD',
          providers: [
            { provider: 'Simulator', displayName: 'Payment simulator', isSimulated: true },
          ],
        }),
      ),
      startPayment: vi.fn(() => of({ order: order({ payment: attempt() }), replayed: false })),
      completeSimulatedPayment: vi.fn(() =>
        of(
          order({
            status: 'Paid',
            payment: attempt({ status: 'Succeeded', simulatorToken: null }),
          }),
        ),
      ),
    };
    booking = { recheckReservation: vi.fn(), refreshAvailability: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        CheckoutFlow,
        { provide: OrderApiClient, useValue: api },
        { provide: BookingFlow, useValue: { ...booking, phase: signal('checkout') } },
      ],
    });
    flow = TestBed.inject(CheckoutFlow);
  });

  describe('creating the order', () => {
    it('sends only the reservation ID with a generated key and shows the server order', () => {
      flow.createOrder(RES);

      expect(api.createOrder).toHaveBeenCalledTimes(1);
      const [reservationId, key] = api.createOrder.mock.calls[0];
      expect(reservationId).toBe(RES);
      expect(key).toMatch(/^[0-9a-f-]{36}$/);
      expect(flow.order()?.totalAmount).toBe(100);
      expect(flow.order()?.lines).toHaveLength(2);
    });

    it('does not show an order before the API answers, and ignores a second click while in flight', () => {
      const pending = new Subject<unknown>();
      api.createOrder.mockReturnValue(pending);
      flow.createOrder(RES);
      flow.createOrder(RES);

      expect(flow.order()).toBeNull();
      expect(flow.busy()).toBe('creating');
      expect(api.createOrder).toHaveBeenCalledTimes(1);
    });

    it('reuses the SAME key after an ambiguous failure and a NEW one after a definite answer', () => {
      api.createOrder.mockReturnValueOnce(throwError(() => networkError()));
      flow.createOrder(RES);
      expect(flow.order()).toBeNull();
      expect(flow.notice()?.text).toContain('repeats the same request safely');

      flow.createOrder(RES);
      const [first, second] = api.createOrder.mock.calls;
      expect(second[1]).toBe(first[1]);
      expect(flow.order()).not.toBeNull();

      flow.reset();
      api.createOrder.mockReturnValueOnce(
        throwError(() =>
          problem(409, 'Order.ReservationNotActive', 'The reservation is no longer active.'),
        ),
      );
      flow.createOrder(RES);
      flow.createOrder(RES);
      expect(api.createOrder.mock.calls[3][1]).not.toBe(api.createOrder.mock.calls[2][1]);
    });

    it('tells the booking flow to re-read the reservation, which is now in checkout', () => {
      flow.createOrder(RES);
      expect(booking.recheckReservation).toHaveBeenCalled();
    });

    it('explains a conflict with the server message and re-reads the truth', () => {
      flow.order.set(order());
      api.createOrder.mockReturnValue(
        throwError(() =>
          problem(
            409,
            'Order.ReservationNotActive',
            'The reservation is no longer active (it expired).',
          ),
        ),
      );
      flow.createOrder(RES);

      expect(flow.notice()?.text).toContain('no longer active');
      expect(flow.notice()?.tone).toBe('warning');
      expect(api.getOrder).toHaveBeenCalled();
    });
  });

  describe('error handling', () => {
    it.each([
      [401, 'session has expired'],
      [403, 'could not be verified'],
      [503, 'not available in this environment'],
    ])('maps %i to a clear message', (status, text) => {
      api.createOrder.mockReturnValue(
        throwError(() => new ApiError('problem', status, null, null, 'raw')),
      );
      flow.createOrder(RES);
      expect(flow.notice()?.text).toContain(text);
    });

    it('shows the server message for a 404 and a generic one for unexpected 4xx', () => {
      api.createOrder.mockReturnValue(
        throwError(() =>
          problem(404, 'Order.ReservationNotFound', 'The reservation was not found.'),
        ),
      );
      flow.createOrder(RES);
      expect(flow.notice()?.text).toBe('The reservation was not found.');
    });
  });

  describe('loading an existing order', () => {
    it('adopts the order of a reservation already in checkout (refresh recovery)', () => {
      flow.load(RES);
      expect(api.getOrderByReservation).toHaveBeenCalledWith(RES);
      expect(flow.order()?.orderId).toBe(ORDER);
      expect(flow.loaded()).toBe(true);
    });

    it('treats 404 as "no order yet" without an error', () => {
      api.getOrderByReservation.mockReturnValue(
        throwError(() => problem(404, 'Order.NotFound', 'The order was not found.')),
      );
      flow.load(RES);
      expect(flow.order()).toBeNull();
      expect(flow.loaded()).toBe(true);
      expect(flow.notice()).toBeNull();
    });
  });

  describe('payment', () => {
    beforeEach(() => {
      flow.order.set(order());
      flow.loadOptions();
    });

    it('starts a payment with a key, shows the pending attempt and offers the simulator controls', () => {
      flow.startPayment();

      const [orderId, key] = api.startPayment.mock.calls[0];
      expect(orderId).toBe(ORDER);
      expect(key).toMatch(/^[0-9a-f-]{36}$/);
      expect(flow.attempt()?.status).toBe('Initiated');
      expect(flow.simulatorReady()).toBe(true);
      expect(flow.canPay()).toBe(false);
    });

    it('does not start twice while in flight, and reuses the key after an ambiguous failure', () => {
      const pending = new Subject<unknown>();
      api.startPayment.mockReturnValueOnce(pending);
      flow.startPayment();
      flow.startPayment();
      expect(api.startPayment).toHaveBeenCalledTimes(1);
      pending.error(networkError());

      flow.startPayment();
      expect(api.startPayment.mock.calls[1][1]).toBe(api.startPayment.mock.calls[0][1]);
    });

    it('reports an unavailable payment provider (503) without retrying with the same key forever', () => {
      api.startPayment.mockReturnValue(
        throwError(() =>
          problem(503, 'Payment.ProviderUnavailable', 'No payment provider is available.'),
        ),
      );
      flow.startPayment();
      expect(flow.notice()?.text).toContain('not available');
      expect(flow.attempt()).toBeNull();
    });

    it('simulated success sends the order, attempt and token (never an amount) and settles the order', () => {
      flow.order.set(order({ payment: attempt() }));
      flow.simulate('succeed');

      expect(api.completeSimulatedPayment).toHaveBeenCalledWith(
        ORDER,
        ATTEMPT,
        'sim_token',
        'succeed',
      );
      expect(flow.order()?.status).toBe('Paid');
      expect(flow.notice()?.text).toContain('simulated');
      expect(booking.recheckReservation).toHaveBeenCalled();
      expect(booking.refreshAvailability).toHaveBeenCalled();
      expect(flow.simulatorReady()).toBe(false);
    });

    it('simulated failure leaves the order payable so the customer can retry', () => {
      flow.order.set(order({ payment: attempt() }));
      api.completeSimulatedPayment.mockReturnValue(
        of(order({ payment: attempt({ status: 'Failed', simulatorToken: null }) })),
      );
      flow.simulate('fail');

      expect(flow.order()?.status).toBe('PendingPayment');
      expect(flow.attempt()?.status).toBe('Failed');
      expect(flow.canPay()).toBe(true);
      expect(booking.refreshAvailability).not.toHaveBeenCalled();
    });

    it('shows a late success as a refund-required order, never as paid', () => {
      flow.order.set(order({ payment: attempt() }));
      api.completeSimulatedPayment.mockReturnValue(
        of(
          order({
            status: 'RefundRequired',
            payment: attempt({ status: 'Succeeded', simulatorToken: null }),
          }),
        ),
      );
      flow.simulate('succeed');

      expect(flow.order()?.status).toBe('RefundRequired');
      expect(flow.notice()?.tone).toBe('warning');
      expect(flow.notice()?.text).toContain('could not be sold');
    });

    it('cannot simulate without an in-flight attempt token', () => {
      flow.simulate('succeed');
      expect(api.completeSimulatedPayment).not.toHaveBeenCalled();
    });
  });

  describe('cancel and expiry', () => {
    beforeEach(() => flow.order.set(order()));

    it('cancels through the server, shows the result and refreshes the reservation and seats', () => {
      flow.cancel();
      expect(api.cancelOrder).toHaveBeenCalledWith(ORDER);
      expect(flow.order()?.status).toBe('Cancelled');
      expect(booking.refreshAvailability).toHaveBeenCalled();
    });

    it('refresh reports an expired order using the server status', () => {
      api.getOrder.mockReturnValue(of(order({ status: 'Expired' })));
      flow.refresh();
      expect(flow.order()?.status).toBe('Expired');
      expect(flow.canPay()).toBe(false);
      expect(flow.notice()?.text).toContain('payment window ended');
    });
  });

  describe('tickets and refunds', () => {
    it('loads the tickets of a paid order (they may appear a moment after payment)', () => {
      flow.order.set(order({ payment: attempt() }));
      flow.simulate('succeed');
      expect(api.getTickets).toHaveBeenCalledWith(ORDER);
      expect(flow.tickets()).toEqual([]);

      api.getTickets.mockReturnValue(
        of([
          {
            ticketId: 'public-ticket-id-xyz',
            seatId: 's1',
            eventId: 'e1',
            status: 'Valid',
            issuedAtUtc: 'x',
          },
        ]),
      );
      flow.loadTickets();
      expect(flow.tickets()).toHaveLength(1);
      expect(flow.ticketsLoading()).toBe(false);
    });

    it('does not request tickets for an order that is not paid', () => {
      flow.order.set(order({ status: 'RefundRequired' }));
      flow.loadTickets();
      expect(api.getTickets).not.toHaveBeenCalled();
    });

    it('a failing ticket lookup never affects the confirmed purchase', () => {
      api.getTickets.mockReturnValue(throwError(() => networkError()));
      flow.order.set(order({ payment: attempt() }));
      flow.simulate('succeed');
      expect(flow.order()?.status).toBe('Paid');
      expect(flow.ticketsLoading()).toBe(false);
    });

    it('explains a refunded order as simulated', () => {
      api.getOrder.mockReturnValue(of(order({ status: 'Refunded' })));
      flow.order.set(order());
      flow.refresh();
      expect(flow.order()?.status).toBe('Refunded');
      expect(flow.notice()?.text).toContain('refunded');
    });
  });
});

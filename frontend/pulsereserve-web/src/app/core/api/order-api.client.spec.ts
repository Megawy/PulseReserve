import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { OrderApiClient } from './order-api.client';
import { OrderResult } from './order-api.models';

const RES = '00000000-0000-4000-8000-0000000000a1';
const ORDER = '00000000-0000-4000-8000-0000000000b1';
const ATTEMPT = '00000000-0000-4000-8000-0000000000d1';
const order = { orderId: ORDER, status: 'PendingPayment', lines: [], payment: null };

describe('OrderApiClient', () => {
  let client: OrderApiClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    client = TestBed.inject(OrderApiClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('creates an order with only the reservation ID in the body and an Idempotency-Key', () => {
    let result: OrderResult | undefined;
    client.createOrder(RES, 'key-1').subscribe((r) => (result = r));
    const req = http.expectOne('/api/orders');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(req.request.body).toEqual({ reservationId: RES }); // no totals, no prices
    req.flush(order, { status: 201, statusText: 'Created' });
    expect(result?.replayed).toBe(false);
  });

  it('flags a replayed creation', () => {
    let result: OrderResult | undefined;
    client.createOrder(RES, 'key-1').subscribe((r) => (result = r));
    http.expectOne('/api/orders').flush(order, { headers: { 'Idempotent-Replayed': 'true' } });
    expect(result?.replayed).toBe(true);
  });

  it('reads orders by id and by reservation', () => {
    client.getOrder(ORDER).subscribe();
    expect(http.expectOne(`/api/orders/${ORDER}`).request.method).toBe('GET');
    client.getOrderByReservation(RES).subscribe();
    http.expectOne(`/api/orders/by-reservation/${RES}`).flush(order);
  });

  it('reads the tickets of an order', () => {
    client.getTickets(ORDER).subscribe();
    const req = http.expectOne(`/api/orders/${ORDER}/tickets`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('cancels with POST and no body', () => {
    client.cancelOrder(ORDER).subscribe();
    const req = http.expectOne(`/api/orders/${ORDER}/cancel`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
  });

  it('starts a payment with an Idempotency-Key and no amount', () => {
    client.startPayment(ORDER, 'pay-1').subscribe();
    const req = http.expectOne(`/api/orders/${ORDER}/payments`);
    expect(req.request.headers.get('Idempotency-Key')).toBe('pay-1');
    expect(req.request.body).toBeNull();
  });

  it('reports the simulator verdict with order, attempt and token only', () => {
    client.completeSimulatedPayment(ORDER, ATTEMPT, 'sim_t', 'succeed').subscribe();
    const req = http.expectOne('/api/dev/payment-simulator/complete');
    expect(req.request.body).toEqual({
      orderId: ORDER,
      attemptId: ATTEMPT,
      simulatorToken: 'sim_t',
      outcome: 'succeed',
    });
  });
});

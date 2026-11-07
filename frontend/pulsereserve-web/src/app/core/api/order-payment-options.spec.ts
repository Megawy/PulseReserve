import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { OrderApiClient } from './order-api.client';

const ORDER = '00000000-0000-4000-8000-0000000000b1';

describe('OrderApiClient payment methods', () => {
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

  it('reads the methods the server offers for the order', () => {
    let result: unknown;
    client.paymentOptions(ORDER).subscribe((r) => (result = r));
    const req = http.expectOne(`/api/orders/${ORDER}/payment-options`);
    expect(req.request.method).toBe('GET');
    req.flush({ currency: 'USD', providers: [] });
    expect(result).toEqual({ currency: 'USD', providers: [] });
  });

  it('names only the provider when starting a payment: no amount, currency or URL', () => {
    client.startPayment(ORDER, 'pay-1', 'Stripe').subscribe();
    const req = http.expectOne(`/api/orders/${ORDER}/payments`);
    expect(req.request.headers.get('Idempotency-Key')).toBe('pay-1');
    expect(req.request.body).toEqual({ provider: 'Stripe' });
    expect(Object.keys(req.request.body as object)).toEqual(['provider']);
  });

  it('keeps the body empty when no provider is named', () => {
    client.startPayment(ORDER, 'pay-2').subscribe();
    expect(http.expectOne(`/api/orders/${ORDER}/payments`).request.body).toBeNull();
  });
});

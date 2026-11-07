import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { AdminPaymentsApiClient } from './admin-payments-api.client';
import { problemDetailsInterceptor } from './problem-details.interceptor';

const session = {
  isAuthenticated: true,
  user: { id: 'u', email: 'a@x.io', displayName: 'Ada' },
  isAdmin: true,
};
const base = '/api/admin/payments';

describe('AdminPaymentsApiClient', () => {
  let api: AdminPaymentsApiClient;
  let http: HttpTestingController;
  let auth: AuthService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor, problemDetailsInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    api = TestBed.inject(AdminPaymentsApiClient);
    http = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
  });

  afterEach(() => http.verify());

  it('sends the filter and limit the API understands, and omits an empty filter', () => {
    api.tasks('NeedsReview', 100).subscribe();
    const filtered = http.expectOne((r) => r.url === `${base}/reconciliation-tasks`);
    expect(filtered.request.params.get('state')).toBe('NeedsReview');
    expect(filtered.request.params.get('limit')).toBe('100');
    filtered.flush([]);

    api.refunds(null, 50).subscribe();
    const all = http.expectOne((r) => r.url === `${base}/refunds`);
    expect(all.request.params.has('state')).toBe(false);
    all.flush([]);

    api.orders('RefundRequired', 100).subscribe();
    const orders = http.expectOne((r) => r.url === `${base}/orders`);
    expect(orders.request.params.get('status')).toBe('RefundRequired');
    orders.flush([]);
  });

  it('uses the existing retry, refund and process-now routes with POST and no body', () => {
    api.retryTask('t1').subscribe();
    api.retryOutbox('o1').subscribe();
    api.retryRefund('r1').subscribe();
    api.requestRefund('ord1').subscribe();
    api.processNow().subscribe();
    for (const url of [
      `${base}/reconciliation-tasks/t1/retry`,
      `${base}/outbox-events/o1/retry`,
      `${base}/refunds/r1/retry`,
      `${base}/orders/ord1/refund`,
      `${base}/process-now`,
    ]) {
      const req = http.expectOne(url);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toBeNull();
      req.flush(null);
    }
  });

  it('reads anomalies and the financial history with only the parameters the API understands', () => {
    api.anomalies('Open', 100).subscribe();
    const anomalies = http.expectOne((r) => r.url === `${base}/anomalies`);
    expect(anomalies.request.method).toBe('GET');
    expect(anomalies.request.params.get('state')).toBe('Open');
    expect(anomalies.request.params.get('limit')).toBe('100');
    anomalies.flush([]);

    api.history('ord1', null, 200).subscribe();
    const history = http.expectOne((r) => r.url === `${base}/history`);
    expect(history.request.params.get('orderId')).toBe('ord1');
    expect(history.request.params.has('entityType')).toBe(false);
    expect(history.request.params.get('limit')).toBe('200');
    history.flush([]);
  });

  it('records an anomaly outcome with POST, an encoded id and exactly the statement fields', () => {
    api
      .resolveAnomaly('a/1', {
        investigationConfirmed: true,
        resolution: 'NoActionNeeded',
        externalReference: null,
        note: null,
      })
      .subscribe();
    const req = http.expectOne(`${base}/anomalies/a%2F1/resolve`);
    expect(req.request.method).toBe('POST');
    expect(Object.keys(req.request.body as object).sort()).toEqual([
      'externalReference',
      'investigationConfirmed',
      'note',
      'resolution',
    ]);
    req.flush({});
  });

  it('reads the overview and the audit log from their routes', () => {
    api.overview().subscribe();
    http.expectOne(`${base}/overview`).flush({});
    api.audit(50).subscribe();
    http.expectOne((r) => r.url === `${base}/audit-log`).flush([]);
  });

  it('surfaces a 403 from the server as an error and keeps the session (not an expiry)', async () => {
    const loading = auth.load();
    http.expectOne('/api/identity/session').flush(session);
    await loading;

    let status = 0;
    api.processNow().subscribe({ error: (e: { status: number }) => (status = e.status) });
    http
      .expectOne(`${base}/process-now`)
      .flush({ title: 'Auth.Forbidden' }, { status: 403, statusText: 'Forbidden' });
    expect(status).toBe(403);
    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.sessionExpired()).toBe(false);
  });

  it('marks the session expired when an operations call answers 401', async () => {
    const loading = auth.load();
    http.expectOne('/api/identity/session').flush(session);
    await loading;

    api.overview().subscribe({ error: () => undefined });
    http.expectOne(`${base}/overview`).flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.isAdmin()).toBe(false);
    expect(auth.sessionExpired()).toBe(true);
  });

  it('recovers once from a stale antiforgery token by refreshing the session and replaying the action', async () => {
    let done = false;
    api.retryTask('t1').subscribe(() => (done = true));
    http
      .expectOne(`${base}/reconciliation-tasks/t1/retry`)
      .flush({ title: 'Csrf.Invalid' }, { status: 403, statusText: 'Forbidden' });
    await new Promise((r) => setTimeout(r));
    http.expectOne('/api/identity/session').flush(session);
    await new Promise((r) => setTimeout(r));
    http.expectOne(`${base}/reconciliation-tasks/t1/retry`).flush(null);
    expect(done).toBe(true);
  });
});

import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { problemDetailsInterceptor } from '../api/problem-details.interceptor';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

const user = {
  id: '00000000-0000-4000-8000-0000000000c1',
  email: 'a@example.com',
  displayName: 'Ada',
};
const session = (authenticated: boolean) => ({
  isAuthenticated: authenticated,
  user: authenticated ? user : null,
  csrfToken: 'token',
});

describe('AuthService and authInterceptor', () => {
  let auth: AuthService;
  let http: HttpTestingController;
  let client: HttpClient;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor, problemDetailsInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
  });

  afterEach(() => http.verify());

  it('loads the session from the server and mirrors it', async () => {
    const loading = auth.load();
    http.expectOne('/api/identity/session').flush(session(true));
    await loading;
    expect(auth.user()).toEqual(user);
    expect(auth.isAuthenticated()).toBe(true);
  });

  it('exposes the admin hint from the server, defaults to false, and resets on sign-out or expiry', async () => {
    const loading = auth.load();
    http.expectOne('/api/identity/session').flush({ ...session(true), isAdmin: true });
    await loading;
    expect(auth.isAdmin()).toBe(true);

    client.get('/api/catalog/x').subscribe({ error: () => undefined });
    http.expectOne('/api/catalog/x').flush({}, { status: 403, statusText: 'Forbidden' });
    expect(auth.isAdmin()).toBe(true); // 403 does not end the session

    client.get('/api/reservations/abc').subscribe({ error: () => undefined });
    http.expectOne('/api/reservations/abc').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(auth.isAdmin()).toBe(false);

    auth.login('a@example.com', 'a-long-password-1').subscribe();
    http.expectOne('/api/identity/login').flush(session(true)); // no isAdmin field => not admin
    expect(auth.isAdmin()).toBe(false);
  });

  it('treats a failed session load as signed out instead of throwing', async () => {
    const loading = auth.load();
    http.expectOne('/api/identity/session').error(new ProgressEvent('error'));
    await expect(loading).resolves.toBeUndefined();
    expect(auth.user()).toBeNull();
  });

  it('logs in with email and password only, never persisting them anywhere', () => {
    const localSet = vi.spyOn(Storage.prototype, 'setItem');
    let result: unknown;
    auth.login('a@example.com', 'a-long-password-1').subscribe((u) => (result = u));
    const req = http.expectOne('/api/identity/login');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'a@example.com', password: 'a-long-password-1' });
    req.flush(session(true));
    expect(result).toEqual(user);
    expect(auth.isAuthenticated()).toBe(true);
    expect(localSet).not.toHaveBeenCalled();
    localSet.mockRestore();
  });

  it('registers and signs in', () => {
    auth.register('a@example.com', 'a-long-password-1', 'Ada').subscribe();
    const req = http.expectOne('/api/identity/register');
    expect(req.request.body).toEqual({
      email: 'a@example.com',
      password: 'a-long-password-1',
      displayName: 'Ada',
    });
    req.flush(session(true), { status: 201, statusText: 'Created' });
    expect(auth.user()).toEqual(user);
  });

  it('keeps the user signed out and surfaces the server message on invalid credentials', () => {
    let message = '';
    auth.login('a@example.com', 'wrong').subscribe({ error: (e: Error) => (message = e.message) });
    http
      .expectOne('/api/identity/login')
      .flush(
        { title: 'Identity.InvalidCredentials', detail: 'The email or password is incorrect.' },
        { status: 401, statusText: 'Unauthorized' },
      );
    expect(message).toBe('The email or password is incorrect.');
    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.sessionExpired()).toBe(false); // a wrong password is not an expired session
  });

  it('signs out through the server and clears the user', () => {
    auth.login('a@example.com', 'a-long-password-1').subscribe();
    http.expectOne('/api/identity/login').flush(session(true));
    auth.logout().subscribe();
    http.expectOne('/api/identity/logout').flush(session(false));
    expect(auth.user()).toBeNull();
  });

  it('marks the session expired when a protected call answers 401 while signed in', () => {
    auth.login('a@example.com', 'a-long-password-1').subscribe();
    http.expectOne('/api/identity/login').flush(session(true));

    client.get('/api/reservations/abc').subscribe({ error: () => undefined });
    http.expectOne('/api/reservations/abc').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(auth.user()).toBeNull();
    expect(auth.sessionExpired()).toBe(true);
  });

  it('does not flag expiry for 401s when nobody was signed in, or for non-401 errors', () => {
    client.get('/api/reservations/abc').subscribe({ error: () => undefined });
    http.expectOne('/api/reservations/abc').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(auth.sessionExpired()).toBe(false);

    auth.login('a@example.com', 'a-long-password-1').subscribe();
    http.expectOne('/api/identity/login').flush(session(true));
    client.get('/api/reservations/abc').subscribe({ error: () => undefined });
    http.expectOne('/api/reservations/abc').flush({}, { status: 404, statusText: 'Not Found' });
    expect(auth.isAuthenticated()).toBe(true);
  });

  describe('antiforgery recovery', () => {
    const csrf403 = { title: 'Csrf.Invalid', detail: 'x' };

    it('refreshes the session once and replays a request the server rejected for a stale CSRF token', async () => {
      let result: unknown;
      client.post('/api/reservations/x/release', null).subscribe((r) => (result = r));
      http
        .expectOne('/api/reservations/x/release')
        .flush(csrf403, { status: 403, statusText: 'Forbidden' });
      await new Promise((r) => setTimeout(r));
      http.expectOne('/api/identity/session').flush(session(true));
      await new Promise((r) => setTimeout(r));
      http.expectOne('/api/reservations/x/release').flush({ ok: true });
      expect(result).toEqual({ ok: true });
    });

    it('never replays twice: a second CSRF rejection is reported', async () => {
      let failure: unknown;
      client.post('/api/reservations/x/release', null).subscribe({ error: (e) => (failure = e) });
      http
        .expectOne('/api/reservations/x/release')
        .flush(csrf403, { status: 403, statusText: 'Forbidden' });
      await new Promise((r) => setTimeout(r));
      http.expectOne('/api/identity/session').flush(session(true));
      await new Promise((r) => setTimeout(r));
      http
        .expectOne('/api/reservations/x/release')
        .flush(csrf403, { status: 403, statusText: 'Forbidden' });
      expect((failure as { status: number }).status).toBe(403);
    });

    it('does not replay other 403s (for example a customer calling an admin route)', () => {
      let failure: unknown;
      client.post('/api/catalog/venues', {}).subscribe({ error: (e) => (failure = e) });
      http
        .expectOne('/api/catalog/venues')
        .flush({ title: 'Auth.Forbidden' }, { status: 403, statusText: 'Forbidden' });
      expect((failure as { status: number }).status).toBe(403);
    });
  });
});

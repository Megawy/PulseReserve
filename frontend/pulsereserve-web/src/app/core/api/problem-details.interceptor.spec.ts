import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { API_REQUEST_TIMEOUT_MS } from '../config/app-config';
import { ApiError } from './api-error';
import { problemDetailsInterceptor } from './problem-details.interceptor';

describe('problemDetailsInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([problemDetailsInterceptor])),
        provideHttpClientTesting(),
        { provide: API_REQUEST_TIMEOUT_MS, useValue: 30 },
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function failure(url: string): { error: unknown } {
    const result: { error: unknown } = { error: undefined };
    http.get(url).subscribe({ error: (e: unknown) => (result.error = e) });
    return result;
  }

  it('passes successful responses through untouched', () => {
    let value: unknown;
    http.get('/ok').subscribe((v) => (value = v));
    controller.expectOne('/ok').flush({ a: 1 });
    expect(value).toEqual({ a: 1 });
  });

  it('converts a ProblemDetails error into an ApiError', () => {
    const result = failure('/p');
    controller
      .expectOne('/p')
      .flush(
        { status: 409, title: 'Concurrency', detail: 'Stale.' },
        { status: 409, statusText: 'Conflict' },
      );
    expect(result.error).toBeInstanceOf(ApiError);
    expect((result.error as ApiError).status).toBe(409);
    expect((result.error as ApiError).message).toBe('Stale.');
  });

  it('converts a non-JSON error body into a safe ApiError', () => {
    const result = failure('/h');
    controller
      .expectOne('/h')
      .flush('<html>oops</html>', { status: 500, statusText: 'Server Error' });
    const e = result.error as ApiError;
    expect(e.kind).toBe('http');
    expect(e.message).toBe('The request failed (HTTP 500).');
  });

  it('converts a network failure into a retryable ApiError', () => {
    const result = failure('/n');
    controller.expectOne('/n').error(new ProgressEvent('error'));
    const e = result.error as ApiError;
    expect(e.kind).toBe('network');
    expect(e.retryable).toBe(true);
  });

  it('converts a timeout into a retryable ApiError', async () => {
    const result = failure('/t');
    controller.expectOne('/t'); // request left pending on purpose
    await new Promise((resolve) => setTimeout(resolve, 80));
    const e = result.error as ApiError;
    expect(e.kind).toBe('timeout');
    expect(e.retryable).toBe(true);
  });
});

import { HttpErrorResponse } from '@angular/common/http';
import { TimeoutError } from 'rxjs';
import { ApiError, toApiError } from './api-error';

function http(status: number, error: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, statusText: 'x', error });
}

describe('toApiError', () => {
  it('maps supported ProblemDetails fields (status/title/detail) for a 400', () => {
    const e = toApiError(
      http(400, { status: 400, title: 'Venue.Invalid', detail: 'Name is required.' }),
    );
    expect(e.kind).toBe('problem');
    expect(e.status).toBe(400);
    expect(e.title).toBe('Venue.Invalid');
    expect(e.detail).toBe('Name is required.');
    expect(e.message).toBe('Name is required.');
    expect(e.retryable).toBe(false);
  });

  it('flags 404 and 409 problem responses', () => {
    expect(
      toApiError(http(404, { title: 'Event.NotFound', detail: 'Not found.' })).isNotFound,
    ).toBe(true);
    const conflict = toApiError(http(409, { title: 'Concurrency', detail: 'Stale.' }));
    expect(conflict.status).toBe(409);
    expect(conflict.retryable).toBe(false);
  });

  it('ignores unsupported/extra fields and tolerates missing optional ones', () => {
    const e = toApiError(http(400, { title: 'Only.Title', traceId: 'abc', extra: { a: 1 } }));
    expect(e.kind).toBe('problem');
    expect(e.detail).toBeNull();
    expect(e.message).toBe('Only.Title');
  });

  it('handles a non-JSON (string/HTML) body without crashing', () => {
    const e = toApiError(http(500, '<html>boom</html>'));
    expect(e.kind).toBe('http');
    expect(e.status).toBe(500);
    expect(e.message).toBe('The request failed (HTTP 500).');
    expect(e.message).not.toContain('html');
    expect(e.retryable).toBe(true);
  });

  it.each([[null], [undefined], [[1, 2]], [{}], [{ title: 5 }]])(
    'handles an unusable body %j',
    (body) => {
      const e = toApiError(http(502, body));
      expect(e.kind).toBe('http');
      expect(e.status).toBe(502);
    },
  );

  it('maps status 0 to a retryable network error', () => {
    const e = toApiError(http(0, new ProgressEvent('error')));
    expect(e.kind).toBe('network');
    expect(e.retryable).toBe(true);
  });

  it('maps rxjs TimeoutError to a retryable timeout error', () => {
    const e = toApiError(new TimeoutError());
    expect(e.kind).toBe('timeout');
    expect(e.retryable).toBe(true);
  });

  it('maps unknown errors to a generic non-retryable error without leaking details', () => {
    const e = toApiError(new Error('secret internals'));
    expect(e.kind).toBe('unexpected');
    expect(e.message).toBe('An unexpected error occurred.');
    expect(e.retryable).toBe(false);
  });

  it('explains a rate limit honestly: nothing was processed, and it is not an immediate-retry error', () => {
    const e = toApiError(http(429, null));
    expect(e.status).toBe(429);
    expect(e.message).toContain('Too many requests');
    expect(e.message).toContain('Nothing was changed');
    expect(e.retryable).toBe(false);
  });

  it('returns an existing ApiError unchanged', () => {
    const original = new ApiError('network', 0, null, null, 'm');
    expect(toApiError(original)).toBe(original);
  });
});

import { HttpContextToken, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { ApiError } from '../api/api-error';
import { AuthService } from './auth.service';

/** Credential endpoints answer 401 for "wrong password": that is not an expired session. */
const CREDENTIAL_PATHS = ['/identity/login', '/identity/register', '/identity/session'];

/** Marks a request that was already replayed once after an antiforgery refresh, so it is never replayed twice. */
const CSRF_RETRIED = new HttpContextToken<boolean>(() => false);

/**
 * Runs outside problemDetailsInterceptor, so it sees ApiError.
 *
 * - 401 from any non-credential call: the session ended (expiry, logout elsewhere, account removed). The UI drops to
 *   signed-out and shows a sign-in prompt.
 * - 403 `Csrf.Invalid`: the antiforgery cookie was replaced (another tab or app on the same host, a restart). The
 *   server rejects such a request BEFORE running it, so it is safe to fetch a fresh token and replay it exactly once.
 *   The replay keeps its Idempotency-Key, so even that is harmless if anything was in doubt.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  return next(req).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof ApiError &&
        error.status === 401 &&
        !CREDENTIAL_PATHS.some((p) => req.url.includes(p))
      ) {
        auth.handleUnauthorized();
      }
      if (
        error instanceof ApiError &&
        error.status === 403 &&
        error.code === 'Csrf.Invalid' &&
        !req.context.get(CSRF_RETRIED)
      ) {
        return from(auth.load()).pipe(
          switchMap(() => next(req.clone({ context: req.context.set(CSRF_RETRIED, true) }))),
        );
      }
      return throwError(() => error);
    }),
  );
};

import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError, timeout } from 'rxjs';
import { API_REQUEST_TIMEOUT_MS } from '../config/app-config';
import { toApiError } from './api-error';

/**
 * Centralised HTTP error handling: every failed request reaches subscribers as an `ApiError`
 * (never a raw HttpErrorResponse). No authentication behaviour exists yet: no backend auth.
 */
export const problemDetailsInterceptor: HttpInterceptorFn = (req, next) => {
  const timeoutMs = inject(API_REQUEST_TIMEOUT_MS);
  return next(req).pipe(
    timeout(timeoutMs),
    catchError((error: unknown) => throwError(() => toApiError(error))),
  );
};

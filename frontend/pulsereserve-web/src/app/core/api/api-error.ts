import { HttpErrorResponse } from '@angular/common/http';
import { TimeoutError } from 'rxjs';
import { readProblemDetails } from './problem-details';

/**
 * - problem:    HTTP error whose body had at least one supported ProblemDetails field
 * - http:       HTTP error without a usable ProblemDetails body (e.g. HTML 500)
 * - network:    no response received (HttpErrorResponse.status === 0)
 * - timeout:    client-side timeout elapsed
 * - unexpected: anything else, including a response that failed client-side validation
 */
export type ApiErrorKind = 'problem' | 'http' | 'network' | 'timeout' | 'unexpected';

export class ApiError extends Error {
  constructor(
    readonly kind: ApiErrorKind,
    /** HTTP status from the response itself; 0 when there was no response. */
    readonly status: number,
    readonly title: string | null,
    readonly detail: string | null,
    message: string,
    /** Seat IDs the server reported as no longer available (HoldSeats conflict). */
    readonly unavailableSeatIds: readonly string[] = [],
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /** Machine-readable domain error code (ProblemDetails `title`, e.g. "Reservation.SeatsUnavailable"). */
  get code(): string | null {
    return this.title;
  }

  get isConflict(): boolean {
    return this.status === 409;
  }

  get isNotFound(): boolean {
    return this.status === 404;
  }

  /** Whether offering "Try again" is reasonable. Does not imply the operation is idempotent. */
  get retryable(): boolean {
    return this.kind === 'network' || this.kind === 'timeout' || this.status >= 500;
  }
}

export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) {
    return error;
  }
  if (error instanceof TimeoutError) {
    return new ApiError('timeout', 0, null, null, 'The server took too long to respond.');
  }
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return new ApiError(
        'network',
        0,
        null,
        null,
        'Could not reach the server. Check your connection and try again.',
      );
    }
    if (error.status === 429) {
      // The server's per-user limit on state-changing financial requests. Nothing was processed; waiting is the only remedy.
      return new ApiError(
        'http',
        429,
        null,
        null,
        'Too many requests. Wait a minute, then try again. Nothing was changed.',
      );
    }
    const problem = readProblemDetails(error.error);
    if (problem) {
      const message =
        problem.detail ?? problem.title ?? `The request failed (HTTP ${error.status}).`;
      return new ApiError(
        'problem',
        error.status,
        problem.title ?? null,
        problem.detail ?? null,
        message,
        problem.unavailableSeatIds ?? [],
      );
    }
    return new ApiError(
      'http',
      error.status,
      null,
      null,
      `The request failed (HTTP ${error.status}).`,
    );
  }
  return new ApiError('unexpected', 0, null, null, 'An unexpected error occurred.');
}

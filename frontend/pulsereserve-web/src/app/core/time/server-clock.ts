import { HttpEventType, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { tap } from 'rxjs';

/** Offsets beyond this are treated as a broken `Date` header (e.g. a stale cached response) and ignored. */
const MAX_PLAUSIBLE_OFFSET_MS = 24 * 60 * 60 * 1000;

/**
 * Estimates (server clock − browser clock) from HTTP `Date` headers, so countdowns derived from
 * server-provided expiry timestamps stay correct when the user's clock is wrong. The header has
 * one-second resolution; the error is acceptable for a 10-minute hold, and the server remains
 * authoritative about expiry.
 */
@Injectable({ providedIn: 'root' })
export class ServerClock {
  private offsetMs = 0;

  /** Current time on the server's clock, in epoch milliseconds. */
  now(): number {
    return Date.now() + this.offsetMs;
  }

  get offset(): number {
    return this.offsetMs;
  }

  observe(dateHeader: string | null, receivedAtMs: number = Date.now()): void {
    if (!dateHeader) {
      return;
    }
    const serverMs = Date.parse(dateHeader);
    if (!Number.isFinite(serverMs)) {
      return;
    }
    const offset = serverMs - receivedAtMs;
    if (Math.abs(offset) <= MAX_PLAUSIBLE_OFFSET_MS) {
      this.offsetMs = offset;
    }
  }
}

export const serverClockInterceptor: HttpInterceptorFn = (req, next) => {
  const clock = inject(ServerClock);
  return next(req).pipe(
    tap((event) => {
      if (event.type === HttpEventType.Response) {
        clock.observe(event.headers.get('Date'));
      }
    }),
  );
};

import { Injectable } from '@angular/core';

/**
 * What survives a page refresh for one signed-in user and event (keyed by both, so another account in the same
 * tab never sees or reuses it). Only identifiers and the in-flight idempotency key are
 * stored; the reservation itself (status, expiry, seats) is always re-read from the server.
 */
export interface PersistedBooking {
  readonly reservationId?: string;
  readonly pending?: { readonly key: string; readonly seatsFingerprint: string };
}

/** sessionStorage wrapper that never throws (storage can be unavailable or blocked). */
@Injectable({ providedIn: 'root' })
export class BookingStorage {
  private keyFor(userId: string, eventId: string): string {
    return `pulsereserve.booking.${userId}.${eventId}`;
  }

  read(userId: string, eventId: string): PersistedBooking | null {
    try {
      const raw = globalThis.sessionStorage?.getItem(this.keyFor(userId, eventId));
      if (!raw) {
        return null;
      }
      const parsed: unknown = JSON.parse(raw);
      if (typeof parsed !== 'object' || parsed === null) {
        return null;
      }
      const record = parsed as Record<string, unknown>;
      const pending = record['pending'] as Record<string, unknown> | undefined;
      return {
        reservationId:
          typeof record['reservationId'] === 'string' ? record['reservationId'] : undefined,
        pending:
          pending &&
          typeof pending['key'] === 'string' &&
          typeof pending['seatsFingerprint'] === 'string'
            ? { key: pending['key'], seatsFingerprint: pending['seatsFingerprint'] }
            : undefined,
      };
    } catch {
      return null;
    }
  }

  write(userId: string, eventId: string, value: PersistedBooking): void {
    try {
      if (!value.reservationId && !value.pending) {
        globalThis.sessionStorage?.removeItem(this.keyFor(userId, eventId));
      } else {
        globalThis.sessionStorage?.setItem(this.keyFor(userId, eventId), JSON.stringify(value));
      }
    } catch {
      // Best effort only.
    }
  }
}

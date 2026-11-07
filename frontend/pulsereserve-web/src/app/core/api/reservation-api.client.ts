import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, throwError } from 'rxjs';
import { API_BASE_URL } from '../config/app-config';
import { toApiError } from './api-error';
import {
  EventAvailability,
  HoldSeatsRequest,
  HoldSeatsResult,
  Reservation,
} from './reservation-api.models';

export const IDEMPOTENCY_KEY_HEADER = 'Idempotency-Key';
const IDEMPOTENT_REPLAYED_HEADER = 'Idempotent-Replayed';

/**
 * Client for the Reservation endpoints:
 *   GET  /api/events/{eventId}/availability
 *   POST /api/reservations                       (requires an Idempotency-Key header)
 *   GET  /api/reservations/{id}
 *   GET  /api/reservations/active?eventId=        (the caller's own live hold for an event; 204 No Content when there is none)
 *   POST /api/reservations/{id}/release
 * Reservation calls need a signed-in session (cookie); the owner is never sent by the client. Failures surface as ApiError via the interceptor.
 */
@Injectable({ providedIn: 'root' })
export class ReservationApiClient {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);

  getAvailability(eventId: string): Observable<EventAvailability> {
    return this.http.get<EventAvailability>(
      `${this.base}/events/${encodeURIComponent(eventId)}/availability`,
    );
  }

  /** The caller owns the key: reuse it for a retry of the same logical request, never for a new one. */
  holdSeats(request: HoldSeatsRequest, idempotencyKey: string): Observable<HoldSeatsResult> {
    return this.http
      .post<Reservation>(
        `${this.base}/reservations`,
        { eventId: request.eventId, seatIds: request.seatIds },
        {
          headers: new HttpHeaders({ [IDEMPOTENCY_KEY_HEADER]: idempotencyKey }),
          observe: 'response',
        },
      )
      .pipe(
        map((response) => {
          if (!response.body) {
            throw new Error('Empty reservation response.');
          }
          return {
            reservation: response.body,
            replayed: response.headers.get(IDEMPOTENT_REPLAYED_HEADER) === 'true',
          };
        }),
      );
  }

  getReservation(reservationId: string): Observable<Reservation> {
    return this.http.get<Reservation>(
      `${this.base}/reservations/${encodeURIComponent(reservationId)}`,
    );
  }

  /**
   * The signed-in customer's live hold (or checkout) for an event, or null. Finds a hold made in another browser or after local state
   * was cleared. "None" is a 204 (no body); an older server's 404 is read the same way; every other failure is still an error.
   */
  getActiveReservation(eventId: string): Observable<Reservation | null> {
    return this.http
      .get<Reservation | null>(`${this.base}/reservations/active`, { params: { eventId } })
      .pipe(
        map((reservation) => reservation ?? null),
        catchError((error: unknown) =>
          toApiError(error).isNotFound ? of(null) : throwError(() => error),
        ),
      );
  }

  /** Idempotent on the server: releasing an already released/expired hold returns its current state. */
  releaseReservation(reservationId: string): Observable<Reservation> {
    return this.http.post<Reservation>(
      `${this.base}/reservations/${encodeURIComponent(reservationId)}/release`,
      null,
    );
  }
}

import { DestroyRef, Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, Subscription, timer } from 'rxjs';
import { ApiError, toApiError } from '../../../core/api/api-error';
import { ReservationApiClient } from '../../../core/api/reservation-api.client';
import {
  EventAvailability,
  MAX_SEATS_PER_HOLD,
  Reservation,
  SeatAvailability,
} from '../../../core/api/reservation-api.models';
import { AuthService } from '../../../core/auth/auth.service';
import { LiveService } from '../../../core/live/live.service';
import { createLiveRefresh } from '../../../core/live/live-refresh';
import { SeatDeltaWire } from '../../../core/live/live.models';
import { CountdownState, computeCountdown } from '../../../core/time/countdown';
import { ServerClock } from '../../../core/time/server-clock';
import { BookingStorage } from './booking-storage';

/** Emits once per countdown tick. Overridable so tests do not depend on real time. */
export const COUNTDOWN_TICKS = new InjectionToken<() => Observable<unknown>>('COUNTDOWN_TICKS', {
  providedIn: 'root',
  factory: () => () => timer(0, 1000),
});

/**
 * How often the seat map re-reads availability on its own while live updates are NOT running (disabled, or the connection is down).
 * A frontend default, not a backend-specified value. While the connection is live this poll does nothing.
 */
export const LIVE_FALLBACK_POLL_MS = new InjectionToken<number>('LIVE_FALLBACK_POLL_MS', {
  providedIn: 'root',
  factory: () => 30_000,
});

export type AvailabilityState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly data: EventAvailability }
  | { readonly kind: 'error'; readonly error: ApiError };

export interface Notice {
  readonly tone: 'info' | 'success' | 'warning' | 'error';
  readonly text: string;
}

export type SelectOutcome = 'selected' | 'deselected' | 'limit' | 'unavailable' | 'locked';

/** Display status of the user's reservation, combining server status with the expiry countdown. */
export type ReservationPhase =
  'none' | 'held' | 'checkout' | 'confirmed' | 'expired' | 'released' | 'unverifiable';

/**
 * Booking state for ONE event page (provide it at component level). Holds belong to the signed-in user: the
 * server derives the owner from the session cookie and the client never names a customer. The server is authoritative:
 * selection is only an intent, and a reservation exists in this state only after the API returned it.
 *
 * Idempotency: a new `Idempotency-Key` is generated for each new logical attempt (a different seat
 * set). A retry after an ambiguous outcome (network error, timeout, 5xx) of the SAME seat set reuses
 * the stored key, so the server replays the original result instead of holding twice. Definite
 * outcomes (success, or a 4xx answer) discard the key.
 */
@Injectable()
export class BookingFlow {
  private readonly api = inject(ReservationApiClient);
  private readonly clock = inject(ServerClock);
  private readonly storage = inject(BookingStorage);
  private readonly ticks = inject(COUNTDOWN_TICKS);
  private readonly auth = inject(AuthService);
  private readonly live = inject(LiveService);
  private readonly fallbackPollMs = inject(LIVE_FALLBACK_POLL_MS);
  private readonly destroyRef = inject(DestroyRef);

  private eventId = '';
  private readonly userId: string | null = this.auth.user()?.id ?? null;
  private availabilitySub: Subscription | null = null;
  private holdSub: Subscription | null = null;
  private releaseSub: Subscription | null = null;
  private tickSub: Subscription | null = null;
  private fallbackSub: Subscription | null = null;
  private restoreSub: Subscription | null = null;
  private releaseWatch: (() => void) | null = null;
  /** A live seat change arrived while an availability read was in flight, so that read may predate it. */
  private deltaDuringRead = false;
  private pending: { key: string; seatsFingerprint: string } | null = null;

  /**
   * Follows live notices. They are hints: a change to MY reservation, or any announced gap, re-reads the server's state. Seat
   * changes of an event are applied as they arrive (they carry the effective status read from the database just before sending).
   */
  private readonly liveRefresh = createLiveRefresh({
    matches: (n) =>
      n.kind === 'reservation' &&
      (n.reservationId === this.reservation()?.reservationId ||
        // A hold made elsewhere (another browser, or before this page had its state) concerns this page too.
        (!this.reservation() && n.eventId === this.eventId)),
    refresh: (resync) => {
      if (!this.eventId) {
        return;
      }
      if (resync) {
        this.refreshAvailability();
      }
      if (this.reservation()) {
        this.recheckReservation();
      } else {
        this.restoreActiveHold();
      }
    },
  });

  readonly availability = signal<AvailabilityState>({ kind: 'loading' });
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly reservation = signal<Reservation | null>(null);
  readonly countdown = signal<CountdownState | null>(null);
  readonly holding = signal(false);
  readonly releasing = signal(false);
  readonly notice = signal<Notice | null>(null);
  /** True after an ambiguous hold failure: the next hold of the same seats is a retry with the same key. */
  readonly retryPending = signal(false);

  /** Whether a user is signed in (UI hint only; the server enforces it on every request). */
  readonly canBook = computed(() => this.auth.user() !== null);
  readonly sessionExpired = this.auth.sessionExpired;
  readonly maxSeats = MAX_SEATS_PER_HOLD;

  readonly seatsById = computed(() => {
    const state = this.availability();
    const map = new Map<string, SeatAvailability>();
    if (state.kind === 'ready') {
      for (const seat of state.data.seats) {
        map.set(seat.seatId, seat);
      }
    }
    return map;
  });

  readonly mySeatIds = computed(
    () =>
      new Set(
        ['held', 'checkout', 'confirmed'].includes(this.phase())
          ? (this.reservation()?.seats ?? []).map((s) => s.seatId)
          : [],
      ),
  );

  readonly phase = computed<ReservationPhase>(() => {
    const reservation = this.reservation();
    if (!reservation) {
      return 'none';
    }
    if (reservation.status === 'Released') {
      return 'released';
    }
    if (reservation.status === 'Expired') {
      return 'expired';
    }
    if (reservation.status === 'Confirmed') {
      return 'confirmed';
    }
    const countdown = this.countdown();
    if (countdown?.kind === 'expired') {
      return 'expired';
    }
    if (countdown?.kind === 'invalid') {
      return 'unverifiable';
    }
    return reservation.status === 'Checkout' ? 'checkout' : 'held';
  });

  /** The seat map is read-only while a request is in flight or a reservation is active/consumed. */
  readonly locked = computed(
    () => this.holding() || !['none', 'expired', 'released'].includes(this.phase()),
  );

  readonly selectionTotal = computed(() => {
    const seats = this.seatsById();
    let total = 0;
    let currency: string | null = null;
    let mixed = false;
    for (const id of this.selected()) {
      const seat = seats.get(id);
      if (!seat) {
        continue;
      }
      total += seat.priceAmount;
      if (currency !== null && currency !== seat.currency) {
        mixed = true;
      }
      currency = seat.currency;
    }
    return { amount: total, currency, mixed };
  });

  constructor() {
    this.live.notices.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((notice) => {
      if (notice.kind === 'availability') {
        this.applySeatChanges(notice.eventId, notice.seats);
      }
    });
    this.destroyRef.onDestroy(() => {
      this.availabilitySub?.unsubscribe();
      this.holdSub?.unsubscribe();
      this.releaseSub?.unsubscribe();
      this.fallbackSub?.unsubscribe();
      this.restoreSub?.unsubscribe();
      this.releaseWatch?.();
      this.stopTicking();
    });
  }

  /** Loads availability and restores a reservation that survived a page refresh. */
  start(eventId: string): void {
    this.eventId = eventId;
    // Subscribe before reading: anything that changes from now on is announced, and a read that started before the subscription
    // became active is repeated once it is (see createLiveRefresh).
    this.releaseWatch?.();
    this.releaseWatch = this.live.watchEvent(eventId);
    this.startFallbackPoll();
    const persisted = this.userId ? this.storage.read(this.userId, eventId) : null;
    this.pending = persisted?.pending ?? null;
    this.retryPending.set(this.pending !== null);
    this.refreshAvailability();
    if (!persisted?.reservationId) {
      // Nothing remembered in this browser: the server knows whether this customer holds seats (another browser, cleared storage).
      this.restoreActiveHold();
    }
    if (persisted?.reservationId) {
      this.api.getReservation(persisted.reservationId).subscribe({
        next: (reservation) => this.adoptReservation(reservation),
        error: (error: unknown) => {
          const apiError = toApiError(error);
          if (!apiError.retryable) {
            this.persist(); // 4xx/404: the stored reservation is gone or not ours.
            this.notice.set({
              tone: 'warning',
              text: 'Your earlier reservation could not be found, so it is no longer shown.',
            });
          }
        },
      });
    }
  }

  refreshAvailability(followUp = false): void {
    this.availabilitySub?.unsubscribe();
    if (this.availability().kind !== 'ready') {
      this.availability.set({ kind: 'loading' });
    }
    this.liveRefresh.markRequested();
    this.deltaDuringRead = false;
    this.availabilitySub = this.api.getAvailability(this.eventId).subscribe({
      next: (data) => {
        this.availability.set({ kind: 'ready', data });
        this.pruneSelection();
        // A live seat change arrived while this read was in flight: the read may predate it, so confirm once.
        if (this.deltaDuringRead && !followUp) {
          this.refreshAvailability(true);
        }
      },
      error: (error: unknown) => this.availability.set({ kind: 'error', error: toApiError(error) }),
    });
  }

  /** Applies seat statuses announced for this event on top of what is shown; the full read stays the authority. */
  private applySeatChanges(eventId: string, changes: readonly SeatDeltaWire[]): void {
    const state = this.availability();
    if (eventId !== this.eventId || state.kind !== 'ready') {
      return;
    }
    if (this.availabilitySub !== null && !this.availabilitySub.closed) {
      this.deltaDuringRead = true;
    }
    const byId = new Map(changes.map((c) => [c.seatId, c.status]));
    let changed = false;
    const seats = state.data.seats.map((seat) => {
      const status = byId.get(seat.seatId);
      if (status === undefined || status === seat.status) {
        return seat;
      }
      changed = true;
      return { ...seat, status };
    });
    if (!changed) {
      return;
    }
    this.availability.set({ kind: 'ready', data: { ...state.data, seats } });
    this.pruneSelection();
  }

  /** While live updates are not running, the seat map re-reads on a slow timer; while they are, this does nothing. */
  private startFallbackPoll(): void {
    this.fallbackSub?.unsubscribe();
    this.fallbackSub = timer(this.fallbackPollMs, this.fallbackPollMs).subscribe(() => {
      if (
        !this.live.isLive() &&
        !globalThis.document?.hidden &&
        this.availability().kind === 'ready'
      ) {
        this.refreshAvailability();
      }
    });
  }

  toggleSeat(seatId: string): SelectOutcome {
    const current = this.selected();
    if (this.holding() || !['none', 'expired', 'released'].includes(this.phase())) {
      return 'locked';
    }
    if (current.has(seatId)) {
      const next = new Set(current);
      next.delete(seatId);
      this.selected.set(next);
      return 'deselected';
    }
    if (this.seatsById().get(seatId)?.status !== 'Available') {
      return 'unavailable';
    }
    if (current.size >= MAX_SEATS_PER_HOLD) {
      this.notice.set({
        tone: 'warning',
        text: `You can hold at most ${MAX_SEATS_PER_HOLD} seats at a time.`,
      });
      return 'limit';
    }
    this.selected.set(new Set(current).add(seatId));
    this.notice.set(null);
    return 'selected';
  }

  clearSelection(): void {
    this.selected.set(new Set());
  }

  get canHold(): boolean {
    return (
      this.canBook() &&
      !this.holding() &&
      this.selected().size > 0 &&
      this.selected().size <= MAX_SEATS_PER_HOLD &&
      (this.phase() === 'none' || this.phase() === 'expired' || this.phase() === 'released')
    );
  }

  hold(): void {
    if (!this.canHold) {
      return;
    }
    const seatIds = [...this.selected()].sort();
    const fingerprint = seatIds.join(',');
    if (this.pending?.seatsFingerprint !== fingerprint) {
      this.pending = { key: crypto.randomUUID(), seatsFingerprint: fingerprint };
    }
    const attempt = this.pending;
    this.persist();
    this.holding.set(true);
    this.notice.set(null);
    this.holdSub?.unsubscribe();
    this.holdSub = this.api.holdSeats({ eventId: this.eventId, seatIds }, attempt.key).subscribe({
      next: ({ reservation, replayed }) => {
        this.holding.set(false);
        this.pending = null;
        this.retryPending.set(false);
        this.clearSelection();
        this.adoptReservation(reservation);
        this.refreshAvailability();
        this.notice.set({
          tone: 'success',
          text: replayed
            ? 'Your earlier request had already succeeded. These are the seats you hold.'
            : 'The server confirmed your hold.',
        });
      },
      error: (error: unknown) => this.onHoldError(toApiError(error)),
    });
  }

  release(): void {
    const reservation = this.reservation();
    if (!reservation || this.releasing()) {
      return;
    }
    this.releasing.set(true);
    this.releaseSub?.unsubscribe();
    this.releaseSub = this.api.releaseReservation(reservation.reservationId).subscribe({
      next: (result) => {
        this.releasing.set(false);
        this.adoptReservation(result);
        this.refreshAvailability();
        this.notice.set(
          result.status === 'Released'
            ? {
                tone: 'success',
                text: 'Your hold was released and the seats are available again.',
              }
            : result.status === 'Expired'
              ? {
                  tone: 'info',
                  text: 'That hold had already expired, so there was nothing to release.',
                }
              : {
                  tone: 'warning',
                  text: `The server reports the reservation as "${result.status}".`,
                },
        );
      },
      error: (error: unknown) => {
        this.releasing.set(false);
        const apiError = toApiError(error);
        this.notice.set(
          apiError.status === 401
            ? {
                tone: 'warning',
                text: 'Your session has expired. Sign in again; your hold stays active until it expires.',
              }
            : {
                tone: 'error',
                text: apiError.isNotFound
                  ? 'The server could not find this reservation. Refresh to see the current state.'
                  : `${apiError.message} The hold may still be active; you can try releasing again.`,
              },
        );
      },
    });
  }

  /**
   * Asks the server for this customer's live hold on the event and shows it. Never creates anything: it only makes a hold the server
   * already has visible here. Silent when there is none, when nobody is signed in, or when the question cannot be answered.
   */
  restoreActiveHold(): void {
    if (!this.canBook() || !this.eventId) {
      return;
    }
    this.restoreSub?.unsubscribe();
    this.restoreSub = this.api.getActiveReservation(this.eventId).subscribe({
      next: (found) => {
        if (!found || this.reservation()) {
          return; // none, or one is already shown (it is re-read by the usual paths)
        }
        this.adoptReservation(found);
        this.notice.set({
          tone: 'info',
          text: 'You already hold seats for this event. This is your current hold.',
        });
      },
      error: () => undefined,
    });
  }

  /** Re-reads the reservation from the server (e.g. after an unreadable expiry timestamp). */
  recheckReservation(): void {
    const reservation = this.reservation();
    if (!reservation) {
      return;
    }
    this.api.getReservation(reservation.reservationId).subscribe({
      next: (fresh) => this.adoptReservation(fresh),
      error: (error: unknown) =>
        this.notice.set({ tone: 'error', text: toApiError(error).message }),
    });
  }

  dismissReservation(): void {
    this.reservation.set(null);
    this.countdown.set(null);
    this.stopTicking();
    this.persist();
  }

  private onHoldError(error: ApiError): void {
    this.holding.set(false);
    if (error.status === 401) {
      // Session ended: keep the selection and the pending key; the same user can sign in and retry safely.
      this.notice.set({
        tone: 'warning',
        text: 'Your session has expired. Sign in again to continue.',
      });
      return;
    }
    const ambiguous = error.kind === 'network' || error.kind === 'timeout' || error.status >= 500;
    if (ambiguous) {
      // Keep the key: retrying the same seats must replay, not double-hold.
      this.retryPending.set(true);
      this.notice.set({
        tone: 'warning',
        text: `${error.message} We could not confirm whether the hold was created. Retrying sends the same request safely; changing seats starts a new one.`,
      });
      return;
    }
    this.pending = null;
    this.retryPending.set(false);
    this.persist();
    if (error.isConflict && error.code === 'Reservation.SeatsUnavailable') {
      const taken = new Set(error.unavailableSeatIds);
      this.selected.set(new Set([...this.selected()].filter((id) => !taken.has(id))));
      this.notice.set({
        tone: 'warning',
        text: 'Some of the seats you chose are no longer available. Availability has been refreshed; please choose again.',
      });
      this.refreshAvailability();
    } else if (error.isConflict) {
      this.notice.set({ tone: 'warning', text: error.message });
      this.refreshAvailability();
      if (error.code === 'Reservation.ActiveHoldExists') {
        this.restoreActiveHold(); // show the hold the server says exists, instead of leaving the customer stuck
      }
    } else {
      this.notice.set({ tone: 'error', text: error.message });
    }
  }

  private adoptReservation(reservation: Reservation): void {
    const previous = this.reservation();
    this.reservation.set(reservation);
    if (
      previous?.reservationId === reservation.reservationId &&
      (previous.status === 'Held' || previous.status === 'Checkout') &&
      reservation.status === 'Expired'
    ) {
      // The server closed the hold (for example while this tab was waiting): say so, whichever path noticed first.
      this.notice.set({
        tone: 'warning',
        text: 'Your hold has expired and the seats were returned to the pool.',
      });
    }
    this.stopTicking();
    // A reservation in Checkout keeps its ORIGINAL expiry (the hold is never extended), so the countdown continues.
    if (reservation.status === 'Held' || reservation.status === 'Checkout') {
      this.countdown.set(computeCountdown(reservation.expiresAtUtc, this.clock.now()));
      this.tickSub = this.ticks().subscribe(() => this.onTick());
    } else {
      this.countdown.set(null);
    }
    this.persist();
  }

  private onTick(): void {
    const reservation = this.reservation();
    if (!reservation) {
      return;
    }
    const state = computeCountdown(reservation.expiresAtUtc, this.clock.now());
    this.countdown.set(state);
    if (state.kind === 'expired') {
      this.stopTicking();
      this.notice.set({
        tone: 'warning',
        text: 'Your hold has expired and the seats were returned to the pool.',
      });
      this.refreshAvailability();
      this.recheckReservation();
    } else if (state.kind === 'invalid') {
      this.stopTicking();
      this.recheckReservation();
    }
  }

  private pruneSelection(): void {
    const seats = this.seatsById();
    const before = this.selected();
    const kept = [...before].filter((id) => seats.get(id)?.status === 'Available');
    if (kept.length !== before.size) {
      this.selected.set(new Set(kept));
      this.notice.set({
        tone: 'warning',
        text: 'Some seats you selected are no longer available and were removed from your selection.',
      });
    }
  }

  private stopTicking(): void {
    this.tickSub?.unsubscribe();
    this.tickSub = null;
  }

  private persist(): void {
    const reservation = this.reservation();
    if (!this.userId) {
      return;
    }
    this.storage.write(this.userId, this.eventId, {
      reservationId:
        reservation && (reservation.status === 'Held' || reservation.status === 'Checkout')
          ? reservation.reservationId
          : undefined,
      pending: this.pending ?? undefined,
    });
  }
}

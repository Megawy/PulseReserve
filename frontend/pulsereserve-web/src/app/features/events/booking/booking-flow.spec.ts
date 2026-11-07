import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { ApiError } from '../../../core/api/api-error';
import { ReservationApiClient } from '../../../core/api/reservation-api.client';
import {
  EventAvailability,
  Reservation,
  SeatAvailability,
} from '../../../core/api/reservation-api.models';
import { AuthService } from '../../../core/auth/auth.service';
import { ServerClock } from '../../../core/time/server-clock';
import { BookingFlow, COUNTDOWN_TICKS } from './booking-flow';

const EVENT = '00000000-0000-4000-8000-0000000000e1';
const CUSTOMER = '00000000-0000-4000-8000-0000000000c1';
const seatId = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const seat = (n: number, status = 'Available'): SeatAvailability => ({
  seatId: seatId(n),
  status,
  priceAmount: 50,
  currency: 'USD',
});

const availability = (...seats: SeatAvailability[]): EventAvailability => ({
  eventId: EVENT,
  seats,
});

function range(from: number, to: number) {
  return Array.from({ length: to - from + 1 }, (_, i) => seat(from + i));
}

function reservation(over: Partial<Reservation> = {}): Reservation {
  return {
    reservationId: '00000000-0000-4000-8000-0000000000a1',
    eventId: EVENT,
    customerId: CUSTOMER,
    status: 'Held',
    createdAtUtc: '2026-10-21T10:00:00Z',
    expiresAtUtc: '2026-10-21T10:10:00Z',
    totalAmount: 100,
    currency: 'USD',
    seats: [
      { seatId: seatId(1), priceAmount: 50, currency: 'USD' },
      { seatId: seatId(2), priceAmount: 50, currency: 'USD' },
    ],
    ...over,
  };
}

const networkError = () => new ApiError('network', 0, null, null, 'Could not reach the server.');
const conflict = (ids: string[] = []) =>
  new ApiError(
    'problem',
    409,
    'Reservation.SeatsUnavailable',
    'One or more requested seats are no longer available.',
    'One or more requested seats are no longer available.',
    ids,
  );

describe('BookingFlow', () => {
  let api: {
    getAvailability: ReturnType<typeof vi.fn>;
    holdSeats: ReturnType<typeof vi.fn>;
    getReservation: ReturnType<typeof vi.fn>;
    getActiveReservation: ReturnType<typeof vi.fn>;
    releaseReservation: ReturnType<typeof vi.fn>;
  };
  let ticks: Subject<number>;
  let nowMs: number;
  let flow: BookingFlow;

  function setup(user: unknown = { id: CUSTOMER, email: 'a@example.com', displayName: 'Ada' }) {
    TestBed.configureTestingModule({
      providers: [
        BookingFlow,
        { provide: ReservationApiClient, useValue: api },
        {
          provide: AuthService,
          useValue: { user: signal(user), sessionExpired: signal(false) },
        },
        { provide: COUNTDOWN_TICKS, useValue: () => ticks.asObservable() },
        { provide: ServerClock, useValue: { now: () => nowMs } },
      ],
    });
    flow = TestBed.inject(BookingFlow);
  }

  beforeEach(() => {
    sessionStorage.clear();
    ticks = new Subject<number>();
    nowMs = Date.parse('2026-10-21T10:00:00Z');
    api = {
      getAvailability: vi.fn(() =>
        of(availability(seat(1), seat(2), seat(3), seat(4, 'Held'), ...range(5, 12))),
      ),
      holdSeats: vi.fn(() => of({ reservation: reservation(), replayed: false })),
      getReservation: vi.fn(() => of(reservation())),
      getActiveReservation: vi.fn(() => of(null)),
      releaseReservation: vi.fn(() => of(reservation({ status: 'Released' }))),
    };
  });

  describe('seat selection', () => {
    beforeEach(() => {
      setup();
      flow.start(EVENT);
    });

    it('selects and deselects available seats', () => {
      expect(flow.toggleSeat(seatId(1))).toBe('selected');
      expect(flow.selected().has(seatId(1))).toBe(true);
      expect(flow.toggleSeat(seatId(1))).toBe('deselected');
      expect(flow.selected().size).toBe(0);
    });

    it('refuses held and unknown seats', () => {
      expect(flow.toggleSeat(seatId(4))).toBe('unavailable');
      expect(flow.toggleSeat(seatId(999))).toBe('unavailable');
      expect(flow.selected().size).toBe(0);
    });

    it('allows exactly eight seats and refuses the ninth with a notice', () => {
      for (let n = 1; n <= 3; n++) flow.toggleSeat(seatId(n));
      for (let n = 5; n <= 9; n++) expect(flow.toggleSeat(seatId(n))).toBe('selected');
      expect(flow.selected().size).toBe(8);
      expect(flow.toggleSeat(seatId(10))).toBe('limit');
      expect(flow.selected().size).toBe(8);
      expect(flow.notice()?.text).toContain('at most 8');
      // Deselecting still works at the limit.
      expect(flow.toggleSeat(seatId(1))).toBe('deselected');
    });

    it('drops selected seats that become unavailable after a refresh', () => {
      flow.toggleSeat(seatId(1));
      flow.toggleSeat(seatId(2));
      api.getAvailability.mockReturnValue(of(availability(seat(1, 'Held'), seat(2))));
      flow.refreshAvailability();
      expect([...flow.selected()]).toEqual([seatId(2)]);
      expect(flow.notice()?.text).toContain('no longer available');
    });

    it('computes the selection total from server prices', () => {
      flow.toggleSeat(seatId(1));
      flow.toggleSeat(seatId(2));
      expect(flow.selectionTotal()).toEqual({ amount: 100, currency: 'USD', mixed: false });
    });
  });

  describe('hold and idempotency', () => {
    beforeEach(() => {
      setup();
      flow.start(EVENT);
      flow.toggleSeat(seatId(1));
      flow.toggleSeat(seatId(2));
    });

    it('sends the real DTO with a generated Idempotency-Key and shows the API reservation', () => {
      flow.hold();
      expect(api.holdSeats).toHaveBeenCalledTimes(1);
      const [body, key] = api.holdSeats.mock.calls[0];
      expect(body).toEqual({
        eventId: EVENT,
        seatIds: [seatId(1), seatId(2)],
      });
      expect(key).toMatch(/^[0-9a-f-]{36}$/);
      expect(flow.reservation()?.reservationId).toBe(reservation().reservationId);
      expect(flow.phase()).toBe('held');
      expect(flow.selected().size).toBe(0);
    });

    it('does not show a reservation or success before the API answers', () => {
      const pending = new Subject<unknown>();
      api.holdSeats.mockReturnValue(pending);
      flow.hold();
      expect(flow.holding()).toBe(true);
      expect(flow.reservation()).toBeNull();
      expect(flow.notice()).toBeNull();
      flow.hold(); // double click while in flight
      expect(api.holdSeats).toHaveBeenCalledTimes(1);
    });

    it('reuses the SAME key when retrying after an ambiguous network failure', () => {
      api.holdSeats.mockReturnValueOnce(throwError(() => networkError()));
      flow.hold();
      expect(flow.reservation()).toBeNull();
      expect(flow.retryPending()).toBe(true);
      flow.hold();
      const [first, second] = api.holdSeats.mock.calls;
      expect(second[1]).toBe(first[1]);
      expect(flow.phase()).toBe('held');
      expect(flow.retryPending()).toBe(false);
    });

    it('reuses the key after a 5xx and after a timeout', () => {
      const serverError = new ApiError('http', 503, null, null, 'The request failed (HTTP 503).');
      const timeout = new ApiError('timeout', 0, null, null, 'The server took too long.');
      api.holdSeats
        .mockReturnValueOnce(throwError(() => serverError))
        .mockReturnValueOnce(throwError(() => timeout));
      flow.hold();
      flow.hold();
      flow.hold();
      const keys = api.holdSeats.mock.calls.map((c) => c[1]);
      expect(keys).toHaveLength(3);
      expect(new Set(keys).size).toBe(1);
    });

    it('generates a NEW key when the seat set changes', () => {
      api.holdSeats.mockReturnValueOnce(throwError(() => networkError()));
      flow.hold();
      flow.toggleSeat(seatId(3));
      flow.hold();
      const [first, second] = api.holdSeats.mock.calls;
      expect(second[1]).not.toBe(first[1]);
    });

    it('generates a NEW key for a new logical attempt after a definite rejection', () => {
      api.holdSeats.mockReturnValueOnce(throwError(() => conflict()));
      flow.hold();
      // Same seats are still selected (the server named none as taken); the user tries again.
      flow.hold();
      const keys = api.holdSeats.mock.calls.map((c) => c[1]);
      expect(keys).toHaveLength(2);
      expect(keys[1]).not.toBe(keys[0]);
    });

    it('tells the user when the server replayed an earlier identical request', () => {
      api.holdSeats.mockReturnValue(of({ reservation: reservation(), replayed: true }));
      flow.hold();
      expect(flow.notice()?.text).toContain('already succeeded');
    });

    it('persists the pending key so a page refresh keeps retrying with it', () => {
      api.holdSeats.mockReturnValueOnce(throwError(() => networkError()));
      flow.hold();
      const firstKey = api.holdSeats.mock.calls[0][1];

      TestBed.resetTestingModule();
      setup();
      flow.start(EVENT);
      flow.toggleSeat(seatId(1));
      flow.toggleSeat(seatId(2));
      expect(flow.retryPending()).toBe(true);
      flow.hold();
      expect(api.holdSeats.mock.calls[1][1]).toBe(firstKey);
    });

    it('cannot hold while signed out', () => {
      TestBed.resetTestingModule();
      setup(null);
      flow.start(EVENT);
      flow.toggleSeat(seatId(1));
      expect(flow.canBook()).toBe(false);
      flow.hold();
      expect(api.holdSeats).not.toHaveBeenCalled();
    });
  });

  describe('conflict handling', () => {
    beforeEach(() => {
      setup();
      flow.start(EVENT);
      flow.toggleSeat(seatId(1));
      flow.toggleSeat(seatId(2));
    });

    it('refreshes availability, removes taken seats and explains the conflict', () => {
      api.holdSeats.mockReturnValue(throwError(() => conflict([seatId(1)])));
      api.getAvailability.mockClear();
      api.getAvailability.mockReturnValue(of(availability(seat(1, 'Held'), seat(2))));
      flow.hold();
      expect(api.getAvailability).toHaveBeenCalledTimes(1);
      expect(flow.reservation()).toBeNull();
      expect([...flow.selected()]).toEqual([seatId(2)]);
      expect(flow.seatsById().get(seatId(1))?.status).toBe('Held');
    });

    it('explains that the requested seats may no longer be available', () => {
      api.holdSeats.mockReturnValue(throwError(() => conflict([seatId(1)])));
      flow.hold();
      expect(flow.notice()?.tone).toBe('warning');
      expect(flow.notice()?.text).toContain('no longer available');
    });

    it('surfaces other conflicts (active hold exists) with the server message', () => {
      api.holdSeats.mockReturnValue(
        throwError(
          () =>
            new ApiError(
              'problem',
              409,
              'Reservation.ActiveHoldExists',
              'm',
              'The customer already holds seats for this event. Release that hold first.',
            ),
        ),
      );
      flow.hold();
      expect(flow.notice()?.text).toContain('already holds seats');
    });
  });

  describe('countdown and expiry', () => {
    beforeEach(() => {
      setup();
      flow.start(EVENT);
      flow.toggleSeat(seatId(1));
      flow.toggleSeat(seatId(2));
      flow.hold();
    });

    it('counts down from the server expiry timestamp', () => {
      expect(flow.countdown()).toEqual({ kind: 'running', remainingSeconds: 600 });
      nowMs += 125_000;
      ticks.next(1);
      expect(flow.countdown()).toEqual({ kind: 'running', remainingSeconds: 475 });
      expect(flow.phase()).toBe('held');
    });

    it('expires, refreshes availability and re-reads the reservation when time runs out', () => {
      api.getAvailability.mockClear();
      api.getReservation.mockReturnValue(of(reservation({ status: 'Expired' })));
      nowMs += 601_000;
      ticks.next(1);
      expect(flow.phase()).toBe('expired');
      expect(api.getAvailability).toHaveBeenCalledTimes(1);
      expect(api.getReservation).toHaveBeenCalledTimes(1);
      expect(flow.mySeatIds().size).toBe(0);
      // Further ticks do nothing: the timer was stopped.
      ticks.next(2);
      expect(api.getAvailability).toHaveBeenCalledTimes(1);
    });

    it('treats an unreadable expiry timestamp as unverifiable, not as running', () => {
      api.getReservation.mockReturnValue(of(reservation({ expiresAtUtc: 'not-a-date' })));
      flow.recheckReservation();
      expect(flow.countdown()).toEqual({ kind: 'invalid' });
      expect(flow.phase()).toBe('unverifiable');
      expect(flow.toggleSeat(seatId(3))).toBe('locked');
    });

    it('restores a held reservation from the server after a page refresh', () => {
      TestBed.resetTestingModule();
      setup();
      api.getReservation.mockReturnValue(of(reservation()));
      flow.start(EVENT);
      expect(api.getReservation).toHaveBeenCalledWith(reservation().reservationId);
      expect(flow.phase()).toBe('held');
    });

    it('follows the server-adjusted clock, not the browser clock', () => {
      nowMs = Date.parse('2026-10-21T10:09:30Z');
      ticks.next(1);
      expect(flow.countdown()).toEqual({ kind: 'running', remainingSeconds: 30 });
    });
  });

  describe('release', () => {
    beforeEach(() => {
      setup();
      flow.start(EVENT);
      flow.toggleSeat(seatId(1));
      flow.hold();
    });

    it('releases, shows the server-returned status and refreshes availability', () => {
      api.getAvailability.mockClear();
      flow.release();
      expect(api.releaseReservation).toHaveBeenCalledWith(reservation().reservationId);
      expect(flow.reservation()?.status).toBe('Released');
      expect(flow.phase()).toBe('released');
      expect(flow.notice()?.text).toContain('released');
      expect(api.getAvailability).toHaveBeenCalledTimes(1);
    });

    it('reports the actual result when the hold had already expired', () => {
      api.releaseReservation.mockReturnValue(of(reservation({ status: 'Expired' })));
      flow.release();
      expect(flow.phase()).toBe('expired');
      expect(flow.notice()?.text).toContain('already expired');
    });

    it('ignores a second release while one is in flight', () => {
      const pending = new Subject<Reservation>();
      api.releaseReservation.mockReturnValue(pending);
      flow.release();
      flow.release();
      expect(api.releaseReservation).toHaveBeenCalledTimes(1);
      expect(flow.releasing()).toBe(true);
    });

    it('keeps the hold visible and allows retrying when release fails', () => {
      api.releaseReservation.mockReturnValue(throwError(() => networkError()));
      flow.release();
      expect(flow.releasing()).toBe(false);
      expect(flow.phase()).toBe('held');
      expect(flow.notice()?.tone).toBe('error');
    });
  });

  describe('checkout and confirmed reservations', () => {
    beforeEach(() => {
      setup();
    });

    it('restores a reservation that is in Checkout: countdown continues, map locked, seats shown as yours', () => {
      api.getReservation.mockReturnValue(of(reservation({ status: 'Checkout' })));
      sessionStorage.setItem(
        `pulsereserve.booking.${CUSTOMER}.${EVENT}`,
        JSON.stringify({ reservationId: reservation().reservationId }),
      );
      TestBed.resetTestingModule();
      setup();
      flow.start(EVENT);

      expect(flow.phase()).toBe('checkout');
      expect(flow.countdown()).toEqual({ kind: 'running', remainingSeconds: 600 });
      expect(flow.locked()).toBe(true);
      expect(flow.mySeatIds().size).toBe(2);
      expect(flow.toggleSeat(seatId(3))).toBe('locked');
      expect(flow.canHold).toBe(false);
    });

    it('a Checkout whose countdown runs out becomes expired and refreshes availability', () => {
      api.getReservation.mockReturnValue(of(reservation({ status: 'Checkout' })));
      sessionStorage.setItem(
        `pulsereserve.booking.${CUSTOMER}.${EVENT}`,
        JSON.stringify({ reservationId: reservation().reservationId }),
      );
      TestBed.resetTestingModule();
      setup();
      flow.start(EVENT);
      api.getAvailability.mockClear();
      api.getReservation.mockReturnValue(of(reservation({ status: 'Expired' })));

      nowMs += 601_000;
      ticks.next(1);

      expect(flow.phase()).toBe('expired');
      expect(api.getAvailability).toHaveBeenCalledTimes(1);
    });

    it('shows a Confirmed reservation as confirmed with no countdown and does not persist it', () => {
      api.getReservation.mockReturnValue(of(reservation({ status: 'Confirmed' })));
      sessionStorage.setItem(
        `pulsereserve.booking.${CUSTOMER}.${EVENT}`,
        JSON.stringify({ reservationId: reservation().reservationId }),
      );
      TestBed.resetTestingModule();
      setup();
      flow.start(EVENT);

      expect(flow.phase()).toBe('confirmed');
      expect(flow.countdown()).toBeNull();
      expect(flow.mySeatIds().size).toBe(2);
      expect(sessionStorage.getItem(`pulsereserve.booking.${CUSTOMER}.${EVENT}`)).toBeNull();
    });
  });
});

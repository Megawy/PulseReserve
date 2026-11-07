import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { ReservationApiClient } from '../../../core/api/reservation-api.client';
import {
  EventAvailability,
  Reservation,
  SeatAvailability,
} from '../../../core/api/reservation-api.models';
import { AuthService } from '../../../core/auth/auth.service';
import { LiveNotice } from '../../../core/live/live.models';
import { LIVE_TIMING, LiveService } from '../../../core/live/live.service';
import { ServerClock } from '../../../core/time/server-clock';
import { BookingFlow, COUNTDOWN_TICKS, LIVE_FALLBACK_POLL_MS } from './booking-flow';

const EVENT = '00000000-0000-4000-8000-0000000000e1';
const OTHER_EVENT = '00000000-0000-4000-8000-0000000000e2';
const CUSTOMER = '00000000-0000-4000-8000-0000000000c1';
const RESERVATION = '00000000-0000-4000-8000-0000000000a1';
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

const reservation = (over: Partial<Reservation> = {}): Reservation => ({
  reservationId: RESERVATION,
  eventId: EVENT,
  customerId: CUSTOMER,
  status: 'Held',
  createdAtUtc: '2026-10-21T10:00:00Z',
  expiresAtUtc: '2026-10-21T10:10:00Z',
  totalAmount: 100,
  currency: 'USD',
  seats: [{ seatId: seatId(1), priceAmount: 50, currency: 'USD' }],
  ...over,
});

const seatsChanged = (eventId: string, ...changes: [number, string][]): LiveNotice => ({
  kind: 'availability',
  eventId,
  seats: changes.map(([n, status]) => ({ seatId: seatId(n), status })),
});

describe('BookingFlow with live updates', () => {
  let api: {
    getAvailability: ReturnType<typeof vi.fn>;
    getReservation: ReturnType<typeof vi.fn>;
    getActiveReservation: ReturnType<typeof vi.fn>;
    holdSeats: ReturnType<typeof vi.fn>;
    releaseReservation: ReturnType<typeof vi.fn>;
  };
  let notices: Subject<LiveNotice>;
  let isLive: ReturnType<typeof signal<boolean>>;
  let release: ReturnType<typeof vi.fn>;
  let watchEvent: ReturnType<typeof vi.fn>;
  let nowMs: number;
  let flow: BookingFlow;

  function setup(fallbackMs = 30_000) {
    TestBed.configureTestingModule({
      providers: [
        BookingFlow,
        { provide: ReservationApiClient, useValue: api },
        {
          provide: AuthService,
          useValue: {
            user: signal({ id: CUSTOMER, email: 'a@example.com', displayName: 'Ada' }),
            sessionExpired: signal(false),
          },
        },
        { provide: COUNTDOWN_TICKS, useValue: () => new Subject<number>().asObservable() },
        { provide: ServerClock, useValue: { now: () => Date.parse('2026-10-21T10:00:00Z') } },
        { provide: LiveService, useValue: { notices: notices.asObservable(), watchEvent, isLive } },
        { provide: LIVE_TIMING, useValue: { now: () => nowMs } },
        { provide: LIVE_FALLBACK_POLL_MS, useValue: fallbackMs },
      ],
    });
    flow = TestBed.inject(BookingFlow);
  }

  beforeEach(() => {
    vi.useFakeTimers();
    sessionStorage.clear();
    notices = new Subject<LiveNotice>();
    isLive = signal(true);
    release = vi.fn();
    watchEvent = vi.fn(() => release);
    nowMs = 1000;
    api = {
      getAvailability: vi.fn(() => of(availability(seat(1), seat(2), seat(3, 'Held')))),
      getReservation: vi.fn(() => of(reservation())),
      getActiveReservation: vi.fn(() => of(null)),
      holdSeats: vi.fn(() => of({ reservation: reservation(), replayed: false })),
      releaseReservation: vi.fn(() => of(reservation({ status: 'Released' }))),
    };
  });

  afterEach(() => vi.useRealTimers());

  const statusOf = (n: number) => flow.seatsById().get(seatId(n))?.status;

  describe('subscribing', () => {
    it('watches the event it shows, and lets go when the page is destroyed', () => {
      setup();
      flow.start(EVENT);

      expect(watchEvent).toHaveBeenCalledWith(EVENT);
      expect(release).not.toHaveBeenCalled();

      TestBed.resetTestingModule();
      expect(release).toHaveBeenCalledTimes(1);
    });
  });

  describe('seat changes', () => {
    beforeEach(() => {
      setup();
      flow.start(EVENT);
    });

    it('shows a seat another customer just took, without a request', () => {
      api.getAvailability.mockClear();

      notices.next(seatsChanged(EVENT, [1, 'Held'], [3, 'Available']));

      expect(statusOf(1)).toBe('Held');
      expect(statusOf(3)).toBe('Available');
      expect(statusOf(2)).toBe('Available');
      expect(api.getAvailability).not.toHaveBeenCalled();
    });

    it('ignores changes of another event and seats it does not know', () => {
      notices.next(seatsChanged(OTHER_EVENT, [1, 'Sold']));
      notices.next(seatsChanged(EVENT, [999, 'Sold']));

      expect(statusOf(1)).toBe('Available');
      expect(flow.seatsById().size).toBe(3);
    });

    it('removes a selected seat that has just been taken, and says so', () => {
      flow.toggleSeat(seatId(1));
      flow.toggleSeat(seatId(2));

      notices.next(seatsChanged(EVENT, [1, 'Held']));

      expect([...flow.selected()]).toEqual([seatId(2)]);
      expect(flow.notice()?.text).toContain('no longer available');
    });

    it('does nothing before availability has been read', () => {
      TestBed.resetTestingModule();
      api.getAvailability.mockReturnValue(new Subject());
      setup();
      flow.start(EVENT);

      notices.next(seatsChanged(EVENT, [1, 'Held']));

      expect(flow.availability().kind).toBe('loading');
    });

    it('confirms with one more read when a change arrived while a read was in flight, and not forever', () => {
      const first = new Subject<EventAvailability>();
      const second = new Subject<EventAvailability>();
      api.getAvailability.mockClear();
      api.getAvailability.mockReturnValueOnce(first).mockReturnValueOnce(second);

      flow.refreshAvailability();
      notices.next(seatsChanged(EVENT, [2, 'Held'])); // the read in flight may predate this
      first.next(availability(seat(1), seat(2), seat(3, 'Held')));
      expect(api.getAvailability).toHaveBeenCalledTimes(2);

      notices.next(seatsChanged(EVENT, [1, 'Held'])); // arrives during the confirming read
      second.next(availability(seat(1, 'Held'), seat(2, 'Held'), seat(3, 'Held')));

      expect(api.getAvailability).toHaveBeenCalledTimes(2); // the confirmation is not repeated
      expect(statusOf(2)).toBe('Held');
    });
  });

  describe('my reservation', () => {
    beforeEach(() => {
      setup();
      flow.start(EVENT);
      flow.toggleSeat(seatId(1));
      flow.hold();
      api.getReservation.mockClear();
      api.getAvailability.mockClear();
    });

    it('re-reads it from the server when a notice says it changed (the notice is not the state)', () => {
      api.getReservation.mockReturnValue(of(reservation({ status: 'Expired' })));

      notices.next({
        kind: 'reservation',
        reservationId: RESERVATION,
        eventId: EVENT,
        status: 'Expired',
        expiresAtUtc: '2026-10-21T10:10:00Z',
      });
      expect(api.getReservation).not.toHaveBeenCalled(); // debounced
      vi.advanceTimersByTime(300);

      expect(api.getReservation).toHaveBeenCalledTimes(1);
      expect(flow.reservation()?.status).toBe('Expired');
      expect(flow.phase()).toBe('expired');
      expect(flow.notice()?.text).toContain('expired');
    });

    it('ignores a notice about somebody else’s reservation', () => {
      notices.next({
        kind: 'reservation',
        reservationId: 'someone-elses',
        eventId: EVENT,
        status: 'Expired',
        expiresAtUtc: '2026-10-21T10:10:00Z',
      });
      vi.advanceTimersByTime(1000);

      expect(api.getReservation).not.toHaveBeenCalled();
    });

    it('does not trust a notice that the server contradicts', () => {
      api.getReservation.mockReturnValue(of(reservation({ status: 'Held' })));

      notices.next({
        kind: 'reservation',
        reservationId: RESERVATION,
        eventId: EVENT,
        status: 'Expired',
        expiresAtUtc: '2026-10-21T10:10:00Z',
      });
      vi.advanceTimersByTime(300);

      expect(flow.reservation()?.status).toBe('Held');
    });
  });

  describe('gaps', () => {
    beforeEach(() => {
      setup();
      flow.start(EVENT); // availability requested at nowMs = 1000
      api.getAvailability.mockClear();
    });

    it('re-reads availability and the reservation when a gap is announced after the data was requested', () => {
      flow.toggleSeat(seatId(1));
      flow.hold();
      api.getReservation.mockClear();
      api.getAvailability.mockClear();

      notices.next({ kind: 'resync', reason: 'connected', at: 2000 });
      vi.advanceTimersByTime(300);

      expect(api.getAvailability).toHaveBeenCalledTimes(1);
      expect(api.getReservation).toHaveBeenCalledTimes(1);
    });

    it('does not read twice when the connection came up before the page asked', () => {
      notices.next({ kind: 'resync', reason: 'connected', at: 500 });
      vi.advanceTimersByTime(1000);

      expect(api.getAvailability).not.toHaveBeenCalled();
    });
  });

  describe('when live updates are not running', () => {
    it('re-reads availability on a slow timer', () => {
      isLive.set(false);
      setup(5000);
      flow.start(EVENT);
      api.getAvailability.mockClear();

      vi.advanceTimersByTime(4999);
      expect(api.getAvailability).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      expect(api.getAvailability).toHaveBeenCalledTimes(1);
      vi.advanceTimersByTime(5000);
      expect(api.getAvailability).toHaveBeenCalledTimes(2);
    });

    it('stays quiet while live updates are running', () => {
      isLive.set(true);
      setup(5000);
      flow.start(EVENT);
      api.getAvailability.mockClear();

      vi.advanceTimersByTime(60_000);

      expect(api.getAvailability).not.toHaveBeenCalled();
    });

    it('stops with the page', () => {
      isLive.set(false);
      setup(5000);
      flow.start(EVENT);
      api.getAvailability.mockClear();

      TestBed.resetTestingModule();
      vi.advanceTimersByTime(60_000);

      expect(api.getAvailability).not.toHaveBeenCalled();
    });
  });
});

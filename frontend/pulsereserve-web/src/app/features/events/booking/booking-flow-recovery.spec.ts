import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { ApiError } from '../../../core/api/api-error';
import { ReservationApiClient } from '../../../core/api/reservation-api.client';
import { Reservation } from '../../../core/api/reservation-api.models';
import { AuthService } from '../../../core/auth/auth.service';
import { LIVE_REFRESH_DEBOUNCE_MS } from '../../../core/live/live-refresh';
import { LiveNotice } from '../../../core/live/live.models';
import { LIVE_TIMING, LiveService } from '../../../core/live/live.service';
import { ServerClock } from '../../../core/time/server-clock';
import { BookingFlow, COUNTDOWN_TICKS } from './booking-flow';

const EVENT = '00000000-0000-4000-8000-0000000000e1';
const CUSTOMER = '00000000-0000-4000-8000-0000000000c1';
const RESERVATION = '00000000-0000-4000-8000-0000000000a1';
const seatId = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const held = (over: Partial<Reservation> = {}): Reservation => ({
  reservationId: RESERVATION,
  eventId: EVENT,
  customerId: CUSTOMER,
  status: 'Held',
  createdAtUtc: '2026-10-21T10:00:00Z',
  expiresAtUtc: '2026-10-21T10:10:00Z',
  totalAmount: 50,
  currency: 'USD',
  seats: [{ seatId: seatId(1), priceAmount: 50, currency: 'USD' }],
  ...over,
});

describe('BookingFlow finding a hold the server already has', () => {
  let api: {
    getAvailability: ReturnType<typeof vi.fn>;
    getReservation: ReturnType<typeof vi.fn>;
    getActiveReservation: ReturnType<typeof vi.fn>;
    holdSeats: ReturnType<typeof vi.fn>;
  };
  let notices: Subject<LiveNotice>;
  let flow: BookingFlow;

  function setup(signedIn = true) {
    TestBed.configureTestingModule({
      providers: [
        BookingFlow,
        { provide: ReservationApiClient, useValue: api },
        {
          provide: AuthService,
          useValue: {
            user: signal(
              signedIn ? { id: CUSTOMER, email: 'a@example.com', displayName: 'Ada' } : null,
            ),
            sessionExpired: signal(false),
          },
        },
        { provide: COUNTDOWN_TICKS, useValue: () => new Subject<number>().asObservable() },
        { provide: ServerClock, useValue: { now: () => Date.parse('2026-10-21T10:00:00Z') } },
        {
          provide: LiveService,
          useValue: {
            notices: notices.asObservable(),
            watchEvent: () => () => undefined,
            isLive: signal(true),
          },
        },
        { provide: LIVE_TIMING, useValue: { now: () => 1000 } },
        { provide: LIVE_REFRESH_DEBOUNCE_MS, useValue: 0 },
      ],
    });
    flow = TestBed.inject(BookingFlow);
  }

  beforeEach(() => {
    vi.useFakeTimers();
    sessionStorage.clear();
    notices = new Subject<LiveNotice>();
    api = {
      getAvailability: vi.fn(() => of({ eventId: EVENT, seats: [] })),
      getReservation: vi.fn(() => of(held())),
      getActiveReservation: vi.fn(() => of(null)),
      holdSeats: vi.fn(),
    };
  });

  afterEach(() => vi.useRealTimers());

  it('shows the live hold the server reports when this browser remembers none', () => {
    api.getActiveReservation.mockReturnValue(of(held()));
    setup();

    flow.start(EVENT);

    expect(api.getActiveReservation).toHaveBeenCalledWith(EVENT);
    expect(flow.reservation()?.reservationId).toBe(RESERVATION);
    expect(flow.phase()).toBe('held');
    expect(flow.countdown()?.kind).toBe('running');
    expect(flow.notice()?.text).toContain('already hold seats');
    expect(flow.locked()).toBe(true);
  });

  it('keeps the remembered reservation path when this browser does remember one', () => {
    sessionStorage.setItem(
      `pulsereserve.booking.${CUSTOMER}.${EVENT}`,
      JSON.stringify({ reservationId: RESERVATION }),
    );
    setup();

    flow.start(EVENT);

    expect(api.getReservation).toHaveBeenCalledWith(RESERVATION);
    expect(api.getActiveReservation).not.toHaveBeenCalled();
  });

  it('says nothing and shows nothing when there is no live hold', () => {
    setup();

    flow.start(EVENT);

    expect(flow.reservation()).toBeNull();
    expect(flow.phase()).toBe('none');
    expect(flow.notice()).toBeNull();
  });

  it('does not ask when nobody is signed in', () => {
    setup(false);

    flow.start(EVENT);

    expect(api.getActiveReservation).not.toHaveBeenCalled();
  });

  it('stays silent when the question cannot be answered', () => {
    api.getActiveReservation.mockReturnValue(
      throwError(() => new ApiError('network', 0, null, null, 'Could not reach the server.')),
    );
    setup();

    flow.start(EVENT);

    expect(flow.reservation()).toBeNull();
    expect(flow.notice()).toBeNull();
  });

  it('shows the existing hold instead of leaving the customer stuck on "release that hold first"', () => {
    setup();
    flow.start(EVENT);
    api.getAvailability.mockReturnValue(
      of({
        eventId: EVENT,
        seats: [{ seatId: seatId(1), status: 'Available', priceAmount: 50, currency: 'USD' }],
      }),
    );
    flow.refreshAvailability();
    flow.toggleSeat(seatId(1));
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
    api.getActiveReservation.mockReturnValue(of(held()));

    flow.hold();

    expect(flow.reservation()?.reservationId).toBe(RESERVATION);
    expect(flow.notice()?.text).toContain('This is your current hold');
  });

  it('never replaces a reservation that is already shown', () => {
    setup();
    flow.start(EVENT);
    api.getActiveReservation.mockReturnValue(of(held({ reservationId: 'someone-else' })));
    api.getActiveReservation.mockReturnValueOnce(of(held()));
    flow.restoreActiveHold();
    flow.restoreActiveHold();

    expect(flow.reservation()?.reservationId).toBe(RESERVATION);
  });

  it('picks up a hold made in another browser when a live notice about this event arrives', () => {
    setup();
    flow.start(EVENT);
    api.getActiveReservation.mockClear();
    api.getActiveReservation.mockReturnValue(of(held()));

    notices.next({
      kind: 'reservation',
      reservationId: RESERVATION,
      eventId: EVENT,
      status: 'Held',
      expiresAtUtc: '2026-10-21T10:10:00Z',
    });
    vi.advanceTimersByTime(10);

    expect(api.getActiveReservation).toHaveBeenCalledTimes(1);
    expect(flow.reservation()?.reservationId).toBe(RESERVATION);
  });

  it('ignores a live notice about another event', () => {
    setup();
    flow.start(EVENT);
    api.getActiveReservation.mockClear();

    notices.next({
      kind: 'reservation',
      reservationId: RESERVATION,
      eventId: 'another-event',
      status: 'Held',
      expiresAtUtc: '2026-10-21T10:10:00Z',
    });
    vi.advanceTimersByTime(10);

    expect(api.getActiveReservation).not.toHaveBeenCalled();
  });
});

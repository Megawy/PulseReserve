import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ReservationApiClient } from './reservation-api.client';
import { HoldSeatsResult } from './reservation-api.models';

const EVENT = '00000000-0000-4000-8000-0000000000e1';
const CUSTOMER = '00000000-0000-4000-8000-0000000000c1';
const RES = '00000000-0000-4000-8000-0000000000a1';
const SEAT = '00000000-0000-4000-8000-000000000001';

const reservation = {
  reservationId: RES,
  eventId: EVENT,
  customerId: CUSTOMER,
  status: 'Held',
  createdAtUtc: '2026-10-21T10:00:00Z',
  expiresAtUtc: '2026-10-21T10:10:00Z',
  totalAmount: 50,
  currency: 'USD',
  seats: [{ seatId: SEAT, priceAmount: 50, currency: 'USD' }],
};

describe('ReservationApiClient', () => {
  let client: ReservationApiClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    client = TestBed.inject(ReservationApiClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('gets availability from the real endpoint', () => {
    let result: unknown;
    client.getAvailability(EVENT).subscribe((r) => (result = r));
    const req = http.expectOne(`/api/events/${EVENT}/availability`);
    expect(req.request.method).toBe('GET');
    const body = {
      eventId: EVENT,
      seats: [{ seatId: SEAT, status: 'Held', priceAmount: 5, currency: 'USD' }],
    };
    req.flush(body);
    expect(result).toEqual(body);
  });

  it('asks for the caller’s own live hold of an event, and treats 404 as "none"', () => {
    let found: unknown = 'unset';
    client.getActiveReservation(EVENT).subscribe((r) => (found = r));
    const req = http.expectOne((r) => r.url === '/api/reservations/active');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('eventId')).toBe(EVENT);
    req.flush(reservation);
    expect(found).toEqual(reservation);

    let none: unknown = 'unset';
    client.getActiveReservation(EVENT).subscribe((r) => (none = r));
    http
      .expectOne((r) => r.url === '/api/reservations/active')
      .flush(
        { title: 'Reservation.NoActiveHold', status: 404 },
        { status: 404, statusText: 'Not Found' },
      );
    expect(none).toBeNull();
  });

  it('reads a 204 (no live hold) as "none"', () => {
    let result: unknown = 'unset';
    client.getActiveReservation(EVENT).subscribe((r) => (result = r));
    http
      .expectOne((r) => r.url === '/api/reservations/active')
      .flush(null, { status: 204, statusText: 'No Content' });
    expect(result).toBeNull();
  });

  it('still reports every other failure of that lookup', () => {
    let error: unknown;
    client.getActiveReservation(EVENT).subscribe({ error: (e: unknown) => (error = e) });
    http
      .expectOne((r) => r.url === '/api/reservations/active')
      .flush({ title: 'Server.Error', status: 500 }, { status: 500, statusText: 'Server Error' });
    expect(error).toBeDefined();
  });

  it('posts the DTO with the Idempotency-Key header and reports a fresh 201', () => {
    let result: HoldSeatsResult | undefined;
    client.holdSeats({ eventId: EVENT, seatIds: [SEAT] }, 'key-1').subscribe((r) => (result = r));
    const req = http.expectOne('/api/reservations');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(req.request.body).toEqual({ eventId: EVENT, seatIds: [SEAT] }); // no customer: the server uses the session
    req.flush(reservation, { status: 201, statusText: 'Created' });
    expect(result).toEqual({ reservation, replayed: false });
  });

  it('flags an Idempotent-Replayed response', () => {
    let result: HoldSeatsResult | undefined;
    client.holdSeats({ eventId: EVENT, seatIds: [SEAT] }, 'key-1').subscribe((r) => (result = r));
    http
      .expectOne('/api/reservations')
      .flush(reservation, { headers: { 'Idempotent-Replayed': 'true' } });
    expect(result?.replayed).toBe(true);
  });

  it('reads a reservation without sending any customer identifier', () => {
    client.getReservation(RES).subscribe();
    const req = http.expectOne(`/api/reservations/${RES}`);
    expect(req.request.method).toBe('GET');
    req.flush(reservation);
  });

  it('releases with POST and no customer identifier', () => {
    let status = '';
    client.releaseReservation(RES).subscribe((r) => (status = r.status));
    const req = http.expectOne(`/api/reservations/${RES}/release`);
    expect(req.request.method).toBe('POST');
    req.flush({ ...reservation, status: 'Released' });
    expect(status).toBe('Released');
  });
});

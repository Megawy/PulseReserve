/*
 * Models for the Reservation endpoints (ReservationEndpoints.cs, ReservationResponse.cs,
 * GetEventAvailabilityHandler.cs). Unlike Catalog responses, these use plain GUID strings.
 * Timestamps are ISO-8601 UTC strings and are parsed defensively by the consumers.
 */

/** Seat states the availability endpoint can return (SeatStatus). Kept open for forward compatibility. */
export type SeatAvailabilityStatus = 'Available' | 'Held' | (string & {});

export interface SeatAvailability {
  readonly seatId: string;
  readonly status: SeatAvailabilityStatus;
  readonly priceAmount: number;
  readonly currency: string;
}

export interface EventAvailability {
  readonly eventId: string;
  readonly seats: readonly SeatAvailability[];
}

/** Reservation lifecycle (ReservationStatus). Kept open for forward compatibility. */
export type ReservationStatus = 'Held' | 'Released' | 'Expired' | (string & {});

export interface ReservedSeat {
  readonly seatId: string;
  readonly priceAmount: number;
  readonly currency: string;
}

export interface Reservation {
  readonly reservationId: string;
  readonly eventId: string;
  readonly customerId: string;
  readonly status: ReservationStatus;
  readonly createdAtUtc: string;
  readonly expiresAtUtc: string;
  readonly totalAmount: number;
  readonly currency: string;
  readonly seats: readonly ReservedSeat[];
}

/** Body of POST /api/reservations. There is no customer field: the server derives ownership from the session. */
export interface HoldSeatsRequest {
  readonly eventId: string;
  readonly seatIds: readonly string[];
}

export interface HoldSeatsResult {
  readonly reservation: Reservation;
  /** True when the server answered 200 + Idempotent-Replayed: an earlier identical request had already succeeded. */
  readonly replayed: boolean;
}

/** Upper bound enforced by the server (ReservationPolicy.MaxSeatsPerHold). The server stays authoritative. */
export const MAX_SEATS_PER_HOLD = 8;

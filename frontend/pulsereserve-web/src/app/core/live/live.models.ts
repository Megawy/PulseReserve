/**
 * Wire contract of the real-time hub (`/hubs/live`, backend doc 28). Every message is a HINT: it says what changed so the page can
 * re-read the authoritative state from the REST API. Nothing here ever states that a payment succeeded or that a refund happened.
 */

/** The administrator areas a message can name. */
export type LiveArea =
  'overview' | 'orders' | 'refunds' | 'exceptions' | 'anomalies' | 'tasks' | 'outbox';

/** Fields every server message carries: the process it came from and its order within that process. */
export interface LiveEnvelope {
  readonly epoch: string;
  readonly seq: number;
}

export interface SeatDeltaWire {
  readonly seatId: string;
  /** Effective status read from the database just before the message was sent: Available, Held or Sold. */
  readonly status: string;
}

export interface AvailabilityWire extends LiveEnvelope {
  readonly eventId: string;
  readonly seats: readonly SeatDeltaWire[];
}

export interface ReservationWire extends LiveEnvelope {
  readonly reservationId: string;
  readonly eventId: string;
  readonly status: string;
  readonly expiresAtUtc: string;
}

export interface OrderWire extends LiveEnvelope {
  readonly orderId: string;
  readonly reservationId: string;
  readonly status: string;
  readonly paymentStatus: string | null;
  readonly ticketCount: number;
}

export interface AdminWire extends LiveEnvelope {
  readonly area: string;
}

export interface ResyncWire extends LiveEnvelope {
  readonly reason: string;
}

export interface HelloWire {
  readonly epoch: string;
  readonly serverTimeUtc: string;
  readonly authenticated: boolean;
  readonly admin: boolean;
  readonly maxConnectionSeconds: number;
}

/** Why a page should re-read what it shows. */
export type ResyncReason =
  | 'connected' // the connection was (re)established after a gap
  | 'subscribed' // an event subscription became active after the page had already loaded
  | 'epoch-changed' // the server process changed (restart or another instance): earlier baselines are gone
  | 'server' // the server said notifications may have been missed
  | 'identity-changed'; // sign-in or sign-out: what the connection may hear changed

/** What consumers receive. `at` is a monotonic millisecond reading (`performance.now()`) taken when the baseline was lost. */
export type LiveNotice =
  | {
      readonly kind: 'availability';
      readonly eventId: string;
      readonly seats: readonly SeatDeltaWire[];
    }
  | {
      readonly kind: 'reservation';
      readonly reservationId: string;
      readonly eventId: string;
      readonly status: string;
      readonly expiresAtUtc: string;
    }
  | {
      readonly kind: 'order';
      readonly orderId: string;
      readonly reservationId: string;
      readonly status: string;
      readonly paymentStatus: string | null;
      readonly ticketCount: number;
    }
  | { readonly kind: 'admin'; readonly area: LiveArea }
  | { readonly kind: 'resync'; readonly reason: ResyncReason; readonly at: number };

/**
 * `off`: not started. `connecting`: first attempt. `live`: connected and the server identified this connection.
 * `reconnecting`: the connection dropped and the client is retrying. `unavailable`: the server refuses live connections
 * (disabled, or this origin is not allowed); pages keep working with their normal refresh and retry only rarely.
 */
export type LiveStatus = 'off' | 'connecting' | 'live' | 'reconnecting' | 'unavailable';

export const LIVE_AREAS: readonly LiveArea[] = [
  'overview',
  'orders',
  'refunds',
  'exceptions',
  'anomalies',
  'tasks',
  'outbox',
];

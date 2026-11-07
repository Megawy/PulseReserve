/*
 * Models for the Order/Payment endpoints (OrderEndpoints.cs). Everything shown to the customer (lines, unit prices,
 * total, currency, status) is server-provided; the client never sends or computes an authoritative amount.
 */

/** PendingPayment, Paid, Cancelled, Expired, RefundRequired, Refunded. Kept open for forward compatibility. */
export type OrderStatus =
  | 'PendingPayment'
  | 'Paid'
  | 'Cancelled'
  | 'Expired'
  | 'RefundRequired'
  | 'Refunded'
  | (string & {});

/** Initiated, Succeeded, Failed, Cancelled. */
export type PaymentAttemptStatus =
  'Initiated' | 'Succeeded' | 'Failed' | 'Cancelled' | (string & {});

export interface OrderLine {
  readonly seatId: string;
  readonly quantity: number;
  readonly unitPrice: number;
  readonly lineTotal: number;
}

export interface PaymentAttempt {
  readonly attemptId: string;
  readonly attemptNumber: number;
  readonly status: PaymentAttemptStatus;
  readonly provider: string;
  /** True for the development/test simulator: no real money is ever involved. */
  readonly isSimulated: boolean;
  /** Present only while a simulated attempt is in flight, and only for the owner. */
  readonly simulatorToken: string | null;
  readonly createdAtUtc: string;
  readonly completedAtUtc: string | null;
  /**
   * The provider-hosted payment page, present only while a REAL provider's attempt is in flight and only for the owner.
   * It is never built or guessed client-side, and a browser return from it proves nothing: only the server's order
   * status does.
   */
  readonly checkoutUrl?: string | null;
}

export interface Order {
  readonly orderId: string;
  readonly reservationId: string;
  readonly eventId: string;
  readonly status: OrderStatus;
  readonly currency: string;
  readonly totalAmount: number;
  readonly createdAtUtc: string;
  readonly paymentDeadlineUtc: string;
  readonly lines: readonly OrderLine[];
  readonly payment: PaymentAttempt | null;
}

/** A purchased seat's ticket. `ticketId` is the unguessable public identifier. Issued shortly after payment. */
export interface Ticket {
  readonly ticketId: string;
  readonly seatId: string;
  readonly eventId: string;
  readonly status: string;
  readonly issuedAtUtc: string;
}

export interface OrderResult {
  readonly order: Order;
  /** True when the server answered 200 + Idempotent-Replayed: the same request had already succeeded. */
  readonly replayed: boolean;
}

export type SimulatedOutcome = 'succeed' | 'fail';

/** One payment method the customer may choose for an order: enabled on the server and able to charge its currency. */
export interface PaymentOption {
  readonly provider: string;
  readonly displayName: string;
  readonly isSimulated: boolean;
}

/** Why an order cannot be paid right now (customer-safe; per order, unrelated to what a provider can do). */
export type PaymentIneligibility =
  'OrderHeldForReview' | 'OrderNotPayable' | 'NoPaymentMethod' | (string & {});

/**
 * `GET /api/orders/{id}/payment-options`. When `eligible` is false, `reason` says why and `providers` is empty: no method is
 * offered that the server would refuse. A server that does not send `eligible` is treated as eligible.
 */
export interface PaymentOptions {
  readonly currency: string;
  readonly providers: readonly PaymentOption[];
  readonly eligible?: boolean;
  readonly reason?: PaymentIneligibility | null;
}

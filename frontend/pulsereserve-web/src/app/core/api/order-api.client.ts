import { HttpClient, HttpHeaders, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { API_BASE_URL } from '../config/app-config';
import { Order, OrderResult, PaymentOptions, SimulatedOutcome, Ticket } from './order-api.models';

const IDEMPOTENCY_KEY_HEADER = 'Idempotency-Key';
const IDEMPOTENT_REPLAYED_HEADER = 'Idempotent-Replayed';

/**
 * Client for the Order endpoints (all need a signed-in session; mutations carry the antiforgery header via
 * Angular's XSRF support):
 *   POST /api/orders                              { reservationId }  + Idempotency-Key
 *   GET  /api/orders/{id}                         owner only
 *   GET  /api/orders/by-reservation/{reservationId}
 *   POST /api/orders/{id}/cancel
 *   GET  /api/orders/{id}/payment-options         the providers the customer may choose for this order
 *   POST /api/orders/{id}/payments                + Idempotency-Key, body { provider } (a NAME only)
 *   POST /api/dev/payment-simulator/complete      development/test only (absent elsewhere)
 * The request bodies carry no amounts or prices.
 */
@Injectable({ providedIn: 'root' })
export class OrderApiClient {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_BASE_URL);

  createOrder(reservationId: string, idempotencyKey: string): Observable<OrderResult> {
    return this.http
      .post<Order>(
        `${this.base}/orders`,
        { reservationId },
        {
          headers: new HttpHeaders({ [IDEMPOTENCY_KEY_HEADER]: idempotencyKey }),
          observe: 'response',
        },
      )
      .pipe(map(toResult));
  }

  /** The signed-in customer's own orders, newest first. */
  listOrders(): Observable<Order[]> {
    return this.http.get<Order[]>(`${this.base}/orders`);
  }

  getOrder(orderId: string): Observable<Order> {
    return this.http.get<Order>(`${this.base}/orders/${encodeURIComponent(orderId)}`);
  }

  getOrderByReservation(reservationId: string): Observable<Order> {
    return this.http.get<Order>(
      `${this.base}/orders/by-reservation/${encodeURIComponent(reservationId)}`,
    );
  }

  /** Owner only. An empty list means the tickets have not been issued yet. */
  getTickets(orderId: string): Observable<Ticket[]> {
    return this.http.get<Ticket[]>(`${this.base}/orders/${encodeURIComponent(orderId)}/tickets`);
  }

  cancelOrder(orderId: string): Observable<Order> {
    return this.http.post<Order>(`${this.base}/orders/${encodeURIComponent(orderId)}/cancel`, null);
  }

  paymentOptions(orderId: string): Observable<PaymentOptions> {
    return this.http.get<PaymentOptions>(
      `${this.base}/orders/${encodeURIComponent(orderId)}/payment-options`,
    );
  }

  /**
   * Starts a payment. The body names at most the PROVIDER (one of the options the server listed); the amount, currency
   * and everything else come from the persisted order. Retrying with the same key returns the same attempt.
   */
  startPayment(
    orderId: string,
    idempotencyKey: string,
    provider?: string,
  ): Observable<OrderResult> {
    return this.http
      .post<Order>(
        `${this.base}/orders/${encodeURIComponent(orderId)}/payments`,
        provider ? { provider } : null,
        {
          headers: new HttpHeaders({ [IDEMPOTENCY_KEY_HEADER]: idempotencyKey }),
          observe: 'response',
        },
      )
      .pipe(map(toResult));
  }

  /** DEVELOPMENT/TEST ONLY: reports the simulator's verdict for an in-flight attempt. */
  completeSimulatedPayment(
    orderId: string,
    attemptId: string,
    simulatorToken: string,
    outcome: SimulatedOutcome,
  ): Observable<Order> {
    return this.http.post<Order>(`${this.base}/dev/payment-simulator/complete`, {
      orderId,
      attemptId,
      simulatorToken,
      outcome,
    });
  }
}

function toResult(response: HttpResponse<Order>): OrderResult {
  if (!response.body) {
    throw new Error('Empty order response.');
  }
  return {
    order: response.body,
    replayed: response.headers.get(IDEMPOTENT_REPLAYED_HEADER) === 'true',
  };
}

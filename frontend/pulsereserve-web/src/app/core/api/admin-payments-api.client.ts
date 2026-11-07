import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../config/app-config';
import {
  AuditEntry,
  OperationsOrder,
  OperationsProvider,
  ManualRefundRequest,
  OperationsRefund,
  OutboxEvent,
  FinancialTransition,
  PaymentAnomaly,
  PaymentException,
  ResolvePaymentAnomalyRequest,
  ResolvePaymentExceptionRequest,
  ReconciliationTask,
  WorkflowRunResult,
  WorkflowSummary,
} from './admin-payments.models';

/**
 * Client for `/api/admin/payments/*`. Every route requires the Admin policy on the server (401 anonymous, 403
 * non-admin); mutations also need the antiforgery token, which Angular's XSRF support adds. Lists are bounded by a
 * `limit` (1-200, newest first) and filtered by `state`/`status`; the API has no paging or free-text search.
 */
@Injectable({ providedIn: 'root' })
export class AdminPaymentsApiClient {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(API_BASE_URL)}/admin/payments`;

  overview(): Observable<WorkflowSummary> {
    return this.http.get<WorkflowSummary>(`${this.base}/overview`);
  }

  /** Configured providers and what the system can do with each. Read-only. */
  providers(): Observable<OperationsProvider[]> {
    return this.http.get<OperationsProvider[]>(`${this.base}/providers`);
  }

  tasks(state: string | null, limit: number): Observable<ReconciliationTask[]> {
    return this.http.get<ReconciliationTask[]>(`${this.base}/reconciliation-tasks`, {
      params: query({ state, limit }),
    });
  }

  refunds(state: string | null, limit: number): Observable<OperationsRefund[]> {
    return this.http.get<OperationsRefund[]>(`${this.base}/refunds`, {
      params: query({ state, limit }),
    });
  }

  outbox(state: string | null, limit: number): Observable<OutboxEvent[]> {
    return this.http.get<OutboxEvent[]>(`${this.base}/outbox-events`, {
      params: query({ state, limit }),
    });
  }

  orders(status: string | null, limit: number): Observable<OperationsOrder[]> {
    return this.http.get<OperationsOrder[]>(`${this.base}/orders`, {
      params: query({ status, limit }),
    });
  }

  /** Signature-verified success events that were not applied, newest first; `state` is `Open` or `Resolved`. */
  anomalies(state: string | null, limit: number): Observable<PaymentAnomaly[]> {
    return this.http.get<PaymentAnomaly[]>(`${this.base}/anomalies`, {
      params: query({ state, limit }),
    });
  }

  /** Records an administrator's statement after investigating. Calls no provider and changes no order, attempt or refund. */
  resolveAnomaly(id: string, request: ResolvePaymentAnomalyRequest): Observable<PaymentAnomaly> {
    return this.http.post<PaymentAnomaly>(
      `${this.base}/anomalies/${encodeURIComponent(id)}/resolve`,
      request,
    );
  }

  /** The append-only financial transition history, newest first, for one order and/or entity type. */
  history(
    orderId: string | null,
    entityType: string | null,
    limit: number,
  ): Observable<FinancialTransition[]> {
    return this.http.get<FinancialTransition[]>(`${this.base}/history`, {
      params: query({ orderId, entityType, limit }),
    });
  }

  /** Verified successes for attempts recorded as failed, newest first; `state` is `Open` or `Resolved`. */
  exceptions(state: string | null, limit: number): Observable<PaymentException[]> {
    return this.http.get<PaymentException[]>(`${this.base}/exceptions`, {
      params: query({ state, limit }),
    });
  }

  /**
   * Records an administrator's statement that the money was returned outside this system. Calls no provider and changes no
   * order, attempt or refund; 409 when the amount/currency differ, or it was already resolved with different details.
   */
  resolveException(
    id: string,
    request: ResolvePaymentExceptionRequest,
  ): Observable<PaymentException> {
    return this.http.post<PaymentException>(
      `${this.base}/exceptions/${encodeURIComponent(id)}/resolve`,
      request,
    );
  }

  audit(limit: number): Observable<AuditEntry[]> {
    return this.http.get<AuditEntry[]>(`${this.base}/audit-log`, { params: query({ limit }) });
  }

  /** 204 on success; 404 unknown id; 409 `Operations.InvalidState` when the server does not consider it eligible. */
  retryTask(id: string): Observable<void> {
    return this.http.post<void>(
      `${this.base}/reconciliation-tasks/${encodeURIComponent(id)}/retry`,
      null,
    );
  }

  retryOutbox(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/outbox-events/${encodeURIComponent(id)}/retry`, null);
  }

  retryRefund(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/refunds/${encodeURIComponent(id)}/retry`, null);
  }

  /** Records a refund performed in the provider's dashboard (providers without API refunds only); not provider-confirmed. */
  recordManualRefund(id: string, request: ManualRefundRequest): Observable<OperationsRefund> {
    return this.http.post<OperationsRefund>(
      `${this.base}/refunds/${encodeURIComponent(id)}/manual-completion`,
      request,
    );
  }

  /** Only for an order already in RefundRequired (or Refunded); idempotent: returns the existing refund. */
  requestRefund(orderId: string): Observable<OperationsRefund> {
    return this.http.post<OperationsRefund>(
      `${this.base}/orders/${encodeURIComponent(orderId)}/refund`,
      null,
    );
  }

  /** Runs one worker pass now (reconciliation, refunds, outbox) and returns what it did. */
  processNow(): Observable<WorkflowRunResult> {
    return this.http.post<WorkflowRunResult>(`${this.base}/process-now`, null);
  }
}

function query(values: Record<string, string | number | null>): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== null && value !== '') {
      params = params.set(key, String(value));
    }
  }
  return params;
}

import { Component, DestroyRef, Injector, OnInit, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import {
  OperationsOrder,
  OperationsRefund,
  REFUND_STATES,
} from '../../../core/api/admin-payments.models';
import { ApiError, toApiError } from '../../../core/api/api-error';
import { formatDateTime, formatMoney, shortId } from '../../../shared/format';
import { refundReasonLabel, statusView } from '../../../shared/presentation';
import { Alert } from '../../../shared/ui/alert/alert';
import { ConfirmDialog } from '../../../shared/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '../../../shared/ui/empty-state/empty-state';
import { ErrorState } from '../../../shared/ui/error-state/error-state';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { createActionFlow, createOpsList, createProviderDirectory, restoreFocus } from './ops-flow';
import {
  canRecordManualRefund,
  canRequestRefund,
  canRetryRefund,
  isRefundComplete,
  manualRefundGuidance,
  providerCannotRefund,
  providerLabel,
  refundGuidance,
  refundRequestMessage,
  refundNeedsManualAction,
  refundRetryConsequence,
  shortError,
} from './ops-rules';
import { FilterOption, OpsToolbar } from './ops-toolbar';

type OrdersState =
  | { kind: 'loading' }
  | { kind: 'ready'; orders: readonly OperationsOrder[] }
  | { kind: 'error'; error: ApiError };

/**
 * Refund operations. Two things live here: orders that need a refund but have none yet (a late payment the seats could
 * not honour) and the refund records themselves. Nothing is called a completed refund unless the backend reports the
 * provider-confirmed terminal state; everything before that is shown as in progress.
 */
@Component({
  selector: 'app-admin-refunds',
  imports: [Alert, ConfirmDialog, EmptyState, ErrorState, OpsToolbar, StatusBadge],
  template: `
    <section class="stack" aria-labelledby="awaiting-heading">
      <div class="stack stack--tight">
        <h2 id="awaiting-heading" tabindex="-1">Orders awaiting a refund</h2>
        <p class="muted">
          These orders were paid after their seats could no longer be kept. A refund is normally
          created automatically; one can be requested here if it has not been. Requesting is safe to
          repeat: an order never gets two refunds.
        </p>
      </div>

      @switch (orders().kind) {
        @case ('loading') {
          <p class="visually-hidden" role="status">Loading orders awaiting a refund…</p>
          <div class="panel stack" aria-hidden="true">
            <span class="skeleton skeleton--line"></span>
            <span class="skeleton skeleton--line skeleton--medium"></span>
          </div>
        }
        @case ('error') {
          @if (ordersError(); as err) {
            <app-error-state
              heading="We could not load orders awaiting a refund"
              [message]="err.message"
              [retryable]="err.retryable"
              (retry)="loadOrders()"
            />
          }
        }
        @default {
          @if (awaiting().length === 0) {
            <app-empty-state
              icon="✓"
              heading="No orders are waiting for a refund"
              message="Orders appear here only when a payment arrived after the seats were lost."
            />
          } @else {
            <div class="table-wrap">
              <table class="table table--stack">
                <caption class="visually-hidden">
                  Orders awaiting a refund
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Order</th>
                    <th scope="col" class="num">Amount</th>
                    <th scope="col">Refund</th>
                    <th scope="col">Updated</th>
                    <th scope="col"><span class="visually-hidden">Action</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (order of awaiting(); track order.orderId) {
                    <tr>
                      <td data-label="Order">
                        <code [title]="order.orderId">{{ short(order.orderId) }}</code>
                        <span class="badge">{{
                          label(order.paymentProvider, order.paymentIsSimulated)
                        }}</span>
                      </td>
                      <td data-label="Amount" class="num">
                        {{ money(order.totalAmount, order.currency) }}
                      </td>
                      <td data-label="Refund">
                        @if (order.refundState) {
                          <app-status-badge kind="refund" [value]="order.refundState" />
                        } @else {
                          <span class="subtle">Not requested</span>
                        }
                      </td>
                      <td data-label="Updated">{{ when(order.updatedAtUtc) }}</td>
                      <td class="actions-cell">
                        @if (canRequest(order)) {
                          <button
                            type="button"
                            class="btn btn--secondary btn--sm"
                            (click)="requestFlow.ask(order)"
                          >
                            Request refund<span class="visually-hidden">
                              for order {{ short(order.orderId) }}</span
                            >
                          </button>
                        } @else {
                          <span class="subtle">Refund already created</span>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      }
    </section>

    <section class="stack section" aria-labelledby="refunds-heading">
      <div class="stack stack--tight">
        <h2 id="refunds-heading" tabindex="-1">Refund records</h2>
        <p class="muted">
          <strong>Refunded (provider confirmed)</strong> means the provider confirmed the refund.
          <strong>Refunded (manually recorded)</strong> means an administrator recorded that it was
          done in the provider's dashboard; the provider did not confirm it to this app. Every other
          state is still in progress or needs a person.
        </p>
      </div>

      @if (manualCount() > 0) {
        <app-alert tone="warning" heading="Refunds that only a person can complete">
          {{ manualCount() }} unfinished
          {{ manualCount() === 1 ? 'refund belongs' : 'refunds belong' }} to a provider that cannot
          refund through the API ({{ manualProviders() }}). Refund them in the provider's dashboard,
          then record that here. This app cannot confirm those refunds with the provider; an order
          stays on "Refund pending" until you record one, and a recorded refund is labelled as
          manually recorded.
        </app-alert>
      }

      <app-ops-toolbar
        id="refunds"
        filterLabel="State"
        noun="refunds"
        [options]="options"
        [filter]="list.filter()"
        [query]="list.query()"
        [limit]="list.limit()"
        [shown]="list.visible().length"
        [loaded]="list.loadedCount()"
        (filterChange)="list.setFilter($event)"
        (queryChange)="list.setQuery($event)"
        (limitChange)="list.setLimit($event)"
        (refresh)="reloadAll()"
      />

      @switch (list.state().kind) {
        @case ('loading') {
          <p class="visually-hidden" role="status">Loading refunds…</p>
          <div class="panel stack" aria-hidden="true">
            @for (n of [1, 2, 3, 4]; track n) {
              <span class="skeleton skeleton--line"></span>
            }
          </div>
        }
        @case ('error') {
          @if (listError(); as err) {
            <app-error-state
              heading="We could not load refunds"
              [message]="err.message"
              [retryable]="err.retryable"
              (retry)="list.reload()"
            />
          }
        }
        @default {
          @if (list.visible().length === 0) {
            <app-empty-state
              icon="✓"
              [heading]="list.loadedCount() === 0 ? 'No refunds' : 'No refunds match your search'"
              [message]="
                list.loadedCount() === 0
                  ? 'Nothing matches this state filter.'
                  : 'Clear the search box to see all loaded refunds.'
              "
            />
          } @else {
            <div class="table-wrap">
              <table class="table table--stack">
                <caption class="visually-hidden">
                  Refunds, newest first
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Order</th>
                    <th scope="col" class="num">Amount</th>
                    <th scope="col">State</th>
                    <th scope="col">Reason</th>
                    <th scope="col" class="num">Submission</th>
                    <th scope="col">Last error</th>
                    <th scope="col">Updated</th>
                    <th scope="col"><span class="visually-hidden">Action</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (refund of list.visible(); track refund.refundId) {
                    <tr>
                      <td data-label="Order">
                        <span class="cell">
                          <code [title]="refund.orderId">{{ short(refund.orderId) }}</code>
                          <span class="badge">{{
                            label(refund.provider, refund.isSimulated)
                          }}</span>
                        </span>
                      </td>
                      <td data-label="Amount" class="num">
                        {{ money(refund.amount, refund.currency) }}
                      </td>
                      <td data-label="State">
                        <span class="cell">
                          <app-status-badge kind="refund" [value]="refund.state" />
                          @if (complete(refund)) {
                            <span class="subtle">Confirmed {{ when(refund.completedAtUtc) }}</span>
                          }
                          @if (refund.state === 'ManuallyRecorded') {
                            <span class="subtle">
                              Recorded manually {{ when(refund.manuallyRecordedAtUtc ?? null) }} by
                              administrator
                              <code [title]="refund.manuallyRecordedBy ?? ''">{{
                                short(refund.manuallyRecordedBy ?? '')
                              }}</code
                              >. Not confirmed by {{ refund.provider }}.
                            </span>
                            @if (refund.manualReference) {
                              <span class="subtle"
                                >Reference: <code>{{ refund.manualReference }}</code></span
                              >
                            }
                          }
                        </span>
                      </td>
                      <td data-label="Reason">{{ reason(refund.reason) }}</td>
                      <td data-label="Submission" class="num">
                        #{{ refund.submissionNumber }} · {{ refund.attempts }} attempt(s)
                      </td>
                      <td data-label="Last error">
                        @if (error(refund.lastError); as message) {
                          <span [title]="refund.lastError">{{ message }}</span>
                        } @else {
                          <span class="subtle">—</span>
                        }
                      </td>
                      <td data-label="Updated">{{ when(refund.updatedAtUtc) }}</td>
                      <td class="actions-cell">
                        @if (canRecordManual(refund)) {
                          <span class="subtle">{{ manualGuidance(refund.provider) }}</span>
                          <button
                            type="button"
                            class="btn btn--secondary btn--sm"
                            (click)="askManual(refund)"
                          >
                            Record manual refund<span class="visually-hidden">
                              for order {{ short(refund.orderId) }}</span
                            >
                          </button>
                        } @else if (manualOnly(refund)) {
                          <span class="subtle">{{ manualGuidance(refund.provider) }}</span>
                        } @else if (canRetry(refund.state)) {
                          <button
                            type="button"
                            class="btn btn--secondary btn--sm"
                            (click)="retryFlow.ask(refund)"
                          >
                            Retry<span class="visually-hidden">
                              refund for order {{ short(refund.orderId) }}</span
                            >
                          </button>
                        } @else {
                          <span class="subtle">{{ guidance(refund.state) }}</span>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      }
    </section>

    <app-confirm-dialog
      [open]="requestFlow.pending() !== null"
      heading="Refund this order?"
      [message]="
        requestCannotRefund()
          ? 'This records a refund for the full order amount. The payment provider cannot refund through the API, so it will wait for a person to refund it in the provider dashboard.'
          : 'This creates a refund for the full order amount and sends it to the payment provider.'
      "
      confirmLabel="Request refund"
      busyLabel="Requesting…"
      tone="danger"
      [busy]="requestFlow.busy()"
      (confirmed)="requestFlow.confirm()"
      (cancelled)="requestFlow.cancel()"
    >
      @if (requestFlow.pending(); as order) {
        <dl class="facts dialog-facts">
          <dt>Order</dt>
          <dd>
            <code>{{ order.orderId }}</code>
          </dd>
          <dt>Refund amount</dt>
          <dd>
            <strong>{{ money(order.totalAmount, order.currency) }}</strong>
            @if (order.paymentIsSimulated) {
              (simulated payment: no real money moves)
            }
          </dd>
        </dl>
        <ul class="consequences">
          @if (requestCannotRefund()) {
            <li>
              Nothing is sent to {{ order.paymentProvider }}. The refund appears as "Needs review"
              until someone refunds the payment in the {{ order.paymentProvider }} dashboard.
            </li>
            <li>
              This app cannot confirm that refund. Once it is done there, record it here; until then
              the order stays on "Refund pending".
            </li>
          } @else {
            <li>The order is marked refunded only after the provider confirms the refund.</li>
            <li>Until then the refund stays in progress, and it may take several worker passes.</li>
          }
          <li>Requesting again never creates a second refund.</li>
          <li>The request is recorded in the audit log with your administrator id.</li>
        </ul>
      }
      @if (requestFlow.error(); as message) {
        <app-alert tone="error">{{ message }}</app-alert>
      }
    </app-confirm-dialog>

    <app-confirm-dialog
      [open]="manual() !== null"
      heading="Record a manual refund?"
      message="Use this only after you have already refunded this payment in the provider's dashboard."
      confirmLabel="Record manual refund"
      busyLabel="Recording…"
      tone="danger"
      [busy]="manualBusy()"
      [confirmDisabled]="!manualConfirmed() || manualConflict()"
      (confirmed)="submitManual()"
      (cancelled)="cancelManual()"
    >
      @if (manual(); as refund) {
        <dl class="facts dialog-facts">
          <dt>Order</dt>
          <dd>
            <code>{{ refund.orderId }}</code>
          </dd>
          <dt>Provider</dt>
          <dd>{{ refund.provider }}</dd>
          <dt>Refund amount</dt>
          <dd>
            <strong>{{ money(refund.amount, refund.currency) }}</strong> (the full order amount; it
            cannot be changed)
          </dd>
        </dl>
        <ul class="consequences">
          <li>
            Nothing is sent to {{ refund.provider }}. This app has not checked that the refund
            happened, and {{ refund.provider }} has not confirmed it.
          </li>
          <li>
            The refund is recorded as <strong>manually recorded</strong>, the order becomes
            refunded, and your administrator id and the time are saved in the audit log.
          </li>
          <li>It cannot be undone or recorded twice.</li>
        </ul>
        <div class="field">
          <label class="field__label" for="manual-reference"
            >{{ refund.provider }} reference (optional)</label
          >
          <input
            id="manual-reference"
            class="input"
            type="text"
            maxlength="200"
            autocomplete="off"
            [value]="manualReference()"
            [disabled]="manualBusy()"
            (input)="manualReference.set($any($event.target).value)"
          />
        </div>
        <div class="field">
          <label class="field__label" for="manual-note">Note (optional)</label>
          <input
            id="manual-note"
            class="input"
            type="text"
            maxlength="300"
            autocomplete="off"
            [value]="manualNote()"
            [disabled]="manualBusy()"
            (input)="manualNote.set($any($event.target).value)"
          />
          <span class="field__hint">Do not enter card numbers or other payment details.</span>
        </div>
        <label class="checkbox">
          <input
            type="checkbox"
            [checked]="manualConfirmed()"
            [disabled]="manualBusy() || manualConflict()"
            (change)="manualConfirmed.set($any($event.target).checked)"
          />
          <span
            >I have refunded {{ money(refund.amount, refund.currency) }} for this payment in the
            {{ refund.provider }} dashboard.</span
          >
        </label>
      }
      @if (manualError(); as message) {
        <app-alert tone="error">{{ message }}</app-alert>
      }
    </app-confirm-dialog>

    <app-confirm-dialog
      [open]="retryFlow.pending() !== null"
      heading="Retry this refund?"
      message="Retrying never marks the refund complete. Only a provider confirmation does."
      confirmLabel="Retry refund"
      busyLabel="Retrying…"
      tone="danger"
      [busy]="retryFlow.busy()"
      (confirmed)="retryFlow.confirm()"
      (cancelled)="retryFlow.cancel()"
    >
      @if (retryFlow.pending(); as refund) {
        <dl class="facts dialog-facts">
          <dt>Order</dt>
          <dd>
            <code>{{ refund.orderId }}</code>
          </dd>
          <dt>Refund amount</dt>
          <dd>
            <strong>{{ money(refund.amount, refund.currency) }}</strong>
            @if (refund.isSimulated) {
              (simulated: no real money moves)
            }
          </dd>
          <dt>Current state</dt>
          <dd>{{ stateLabel(refund.state) }}</dd>
        </dl>
        <p class="subtle">{{ consequence(refund) }}</p>
      }
      @if (retryFlow.error(); as message) {
        <app-alert tone="error">{{ message }}</app-alert>
      }
    </app-confirm-dialog>
  `,
  styles: `
    .section {
      margin-top: var(--space-8);
    }
    .cell {
      display: grid;
      gap: var(--space-1);
    }
    .dialog-facts {
      margin-block: var(--space-4) var(--space-2);
    }
    .consequences {
      margin: var(--space-3) 0 0;
      padding-left: var(--space-5);
      color: var(--foreground-muted);
      font-size: var(--font-size-sm);
    }
  `,
})
export class Refunds implements OnInit {
  private readonly api = inject(AdminPaymentsApiClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private ordersSub: Subscription | null = null;

  protected readonly options: readonly FilterOption[] = REFUND_STATES.map((value) => ({
    value,
    label: statusView('refund', value).label,
  }));

  protected readonly orders = signal<OrdersState>({ kind: 'loading' });
  protected readonly awaiting = computed(() => {
    const o = this.orders();
    return o.kind === 'ready' ? o.orders : [];
  });
  protected readonly ordersError = computed(() => {
    const o = this.orders();
    return o.kind === 'error' ? o.error : null;
  });

  protected readonly list = createOpsList<OperationsRefund>({
    allowed: REFUND_STATES,
    fetch: (filter, limit) => this.api.refunds(filter, limit),
    liveAreas: ['refunds'],
    searchText: (r) =>
      [r.refundId, r.orderId, r.reason, refundReasonLabel(r.reason), r.state, r.lastError].join(
        ' ',
      ),
  });

  protected readonly requestFlow = createActionFlow<OperationsOrder, OperationsRefund>({
    run: (order) => this.api.requestRefund(order.orderId),
    success: (_order, refund) => refundRequestMessage(refund),
    done: () => this.reloadAll('awaiting-heading'),
  });

  protected readonly retryFlow = createActionFlow<OperationsRefund, void>({
    run: (refund) => this.api.retryRefund(refund.refundId),
    success: () =>
      'Refund retry started. It is not complete until the provider confirms it; watch the state in the list.',
    done: () => this.reloadAll('refunds-heading'),
  });

  private readonly toast = inject(ToastService);
  private manualSub: Subscription | null = null;
  protected readonly manual = signal<OperationsRefund | null>(null);
  protected readonly manualBusy = signal(false);
  protected readonly manualError = signal<string | null>(null);
  protected readonly manualConflict = signal(false);
  protected readonly manualConfirmed = signal(false);
  protected readonly manualReference = signal('');
  protected readonly manualNote = signal('');

  protected askManual(refund: OperationsRefund): void {
    this.manualError.set(null);
    this.manualConflict.set(false);
    this.manualConfirmed.set(false);
    this.manualReference.set('');
    this.manualNote.set('');
    this.manual.set(refund);
  }

  protected cancelManual(): void {
    if (!this.manualBusy()) {
      this.manual.set(null);
      this.manualError.set(null);
    }
  }

  /** Nothing is called recorded until the server answers; a second click while a request is in flight does nothing. */
  protected submitManual(): void {
    const refund = this.manual();
    if (refund === null || this.manualBusy() || !this.manualConfirmed() || this.manualConflict()) {
      return;
    }
    this.manualBusy.set(true);
    this.manualError.set(null);
    this.manualSub = this.api
      .recordManualRefund(refund.refundId, {
        confirmedPerformedExternally: true,
        amount: refund.amount,
        currency: refund.currency,
        externalReference: this.manualReference().trim() || null,
        note: this.manualNote().trim() || null,
      })
      .subscribe({
        next: () => {
          this.manualBusy.set(false);
          this.manual.set(null);
          this.toast.success(
            'Manual refund recorded. It is marked as manually recorded: the provider has not confirmed it.',
          );
          this.reloadAll('refunds-heading');
        },
        error: (e: unknown) => {
          const err = toApiError(e);
          this.manualBusy.set(false);
          if (err.status === 409 || err.status === 404) {
            this.manualConflict.set(true);
            this.manualError.set(
              err.status === 404
                ? 'This refund no longer exists. Nothing was recorded.'
                : `${err.message} Nothing new was recorded; the list has been refreshed.`,
            );
            this.reloadAll();
          } else if (err.status === 401) {
            this.manual.set(null);
            this.toast.error('Your session has ended. Sign in again to continue.');
          } else if (err.status === 403) {
            this.manualError.set(
              err.code === 'Csrf.Invalid'
                ? 'The security token was rejected. Reload the page and try again.'
                : 'The server refused this action: administrator access is required.',
            );
          } else {
            this.manualError.set(`${err.message} Nothing was recorded; you can try again.`);
          }
        },
      });
  }

  protected readonly canRecordManual = (refund: OperationsRefund) =>
    canRecordManualRefund(refund, this.directory.providers());

  protected readonly canRequest = canRequestRefund;
  private readonly directory = createProviderDirectory(() => this.api.providers());
  protected readonly label = providerLabel;
  protected readonly requestCannotRefund = computed(() =>
    providerCannotRefund(this.requestFlow.pending()?.paymentProvider, this.directory.providers()),
  );
  protected readonly manualOnly = (refund: OperationsRefund) =>
    refundNeedsManualAction(refund, this.directory.providers());
  protected readonly manualGuidance = (provider: string) => manualRefundGuidance(provider);
  private readonly manualRefunds = computed(() => {
    const s = this.list.state();
    return s.kind === 'ready' ? s.items.filter((r) => this.manualOnly(r)) : [];
  });
  protected readonly manualCount = computed(() => this.manualRefunds().length);
  protected readonly manualProviders = computed(() =>
    [...new Set(this.manualRefunds().map((r) => r.provider))].join(', '),
  );
  protected readonly canRetry = canRetryRefund;
  protected readonly complete = isRefundComplete;
  protected readonly guidance = refundGuidance;
  protected readonly consequence = refundRetryConsequence;
  protected readonly short = shortId;
  protected readonly when = formatDateTime;
  protected readonly money = formatMoney;
  protected readonly error = shortError;
  protected readonly reason = refundReasonLabel;
  protected readonly stateLabel = (state: string) => statusView('refund', state).label;

  protected listError() {
    const s = this.list.state();
    return s.kind === 'error' ? s.error : null;
  }

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => {
      this.ordersSub?.unsubscribe();
      this.manualSub?.unsubscribe();
    });
    this.directory.load();
    this.loadOrders();
  }

  protected loadOrders(onLoaded?: () => void): void {
    this.ordersSub?.unsubscribe();
    this.orders.set({ kind: 'loading' });
    this.ordersSub = this.api.orders('RefundRequired', 100).subscribe({
      next: (orders) => {
        this.orders.set({ kind: 'ready', orders });
        onLoaded?.();
      },
      error: (error: unknown) => {
        this.orders.set({ kind: 'error', error: toApiError(error) });
        onLoaded?.();
      },
    });
  }

  /**
   * Reloads both lists. With a `headingId`, focus moves to that heading once each list has finished loading, if the
   * row that opened the dialog is gone by then (whichever list finishes last decides).
   */
  protected reloadAll(headingId?: string): void {
    const settle = headingId ? () => restoreFocus(this.injector, headingId) : undefined;
    this.loadOrders(settle);
    this.list.reload(settle);
  }
}

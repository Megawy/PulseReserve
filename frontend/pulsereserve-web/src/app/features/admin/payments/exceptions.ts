import { Component, DestroyRef, Injector, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import {
  PAYMENT_EXCEPTION_STATES,
  PaymentException,
} from '../../../core/api/admin-payments.models';
import { toApiError } from '../../../core/api/api-error';
import { formatDateTime, formatMoney, shortId } from '../../../shared/format';
import { statusView } from '../../../shared/presentation';
import { Alert } from '../../../shared/ui/alert/alert';
import { ConfirmDialog } from '../../../shared/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '../../../shared/ui/empty-state/empty-state';
import { ErrorState } from '../../../shared/ui/error-state/error-state';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { createOpsList, restoreFocus } from './ops-flow';
import { canResolveException, exceptionOrderNote, providerLabel, shortError } from './ops-rules';
import { FilterOption, OpsToolbar } from './ops-toolbar';

/**
 * Payment exceptions: a provider success that was verified (signature, amount, currency) for a payment attempt this system
 * had already recorded as failed. The page keeps two groups of facts apart (what this system recorded, what the provider
 * reported) and states plainly what has NOT happened: the order is not paid, no ticket exists, and no refund was created or
 * confirmed. The one action is the server's own (`nextAction`): record that the money was returned outside this system.
 */
@Component({
  selector: 'app-admin-exceptions',
  imports: [Alert, ConfirmDialog, EmptyState, ErrorState, OpsToolbar, StatusBadge],
  template: `
    <section class="stack" aria-labelledby="exceptions-heading">
      <div class="stack stack--tight">
        <h2 id="exceptions-heading" tabindex="-1">Payment exceptions</h2>
        <p class="muted">
          An exception is a payment the provider reported as <strong>successful</strong> (and this
          system verified) for an attempt it had already recorded as <strong>failed</strong>. The
          two records disagree, so nothing is decided automatically: the failed attempt stays as it
          was, <strong>the order is not paid, no tickets are issued</strong> and no refund is
          created or claimed. An order with an exception can never be paid again (resolving it does
          not reopen it): the customer starts a new order.
        </p>
        <p class="muted">
          The next step is a refund made in the provider's dashboard, then recorded here. That
          record is an administrator's statement; the provider never confirms it to this app.
        </p>
      </div>

      <app-ops-toolbar
        id="exceptions"
        filterLabel="State"
        noun="exceptions"
        [unresolvedFirst]="true"
        [options]="options"
        [filter]="list.filter()"
        [query]="list.query()"
        [limit]="list.limit()"
        [shown]="list.visible().length"
        [loaded]="list.loadedCount()"
        (filterChange)="list.setFilter($event)"
        (queryChange)="list.setQuery($event)"
        (limitChange)="list.setLimit($event)"
        (refresh)="list.reload()"
      />

      @switch (list.state().kind) {
        @case ('loading') {
          <p class="visually-hidden" role="status">Loading payment exceptions…</p>
          <div class="panel stack" aria-hidden="true">
            @for (n of [1, 2, 3]; track n) {
              <span class="skeleton skeleton--line"></span>
            }
          </div>
        }
        @case ('error') {
          @if (errorOf(); as err) {
            <app-error-state
              heading="We could not load payment exceptions"
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
              [heading]="
                list.loadedCount() === 0
                  ? 'No payment exceptions'
                  : 'No exceptions match your search'
              "
              [message]="
                list.loadedCount() === 0
                  ? 'Nothing matches this state filter. An exception appears only when a verified provider success contradicts a recorded failure.'
                  : 'Clear the search box to see all loaded exceptions.'
              "
            />
          } @else {
            <div class="table-wrap">
              <table class="table table--stack">
                <caption class="visually-hidden">
                  Payment exceptions, open ones first and oldest first
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Payment</th>
                    <th scope="col">This system recorded</th>
                    <th scope="col">The provider reported</th>
                    <th scope="col">Order</th>
                    <th scope="col">Status</th>
                    <th scope="col"><span class="visually-hidden">Action</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (item of list.visible(); track item.exceptionId) {
                    <tr>
                      <td data-label="Payment">
                        <span class="cell">
                          <strong>{{ money(item.amount, item.currency) }}</strong>
                          <span class="subtle">
                            <span class="badge">{{ label(item) }}</span>
                            attempt
                            <code [title]="item.attemptId">{{ short(item.attemptId) }}</code>
                          </span>
                        </span>
                      </td>
                      <td data-label="This system recorded">
                        <span class="cell">
                          <span
                            >Attempt <strong>{{ attemptLabel(item) }}</strong></span
                          >
                          @if (error(item.originalFailureCode); as code) {
                            <span class="subtle">Reason on record: {{ code }}</span>
                          }
                          @if (item.originalVerdictAtUtc) {
                            <span class="subtle">{{ when(item.originalVerdictAtUtc) }}</span>
                          }
                          <span class="subtle">Kept exactly as recorded.</span>
                        </span>
                      </td>
                      <td data-label="The provider reported">
                        <span class="cell">
                          <span
                            ><strong>{{
                              item.isSimulated ? 'Simulated success' : 'Success'
                            }}</strong>
                            for {{ money(item.amount, item.currency) }}</span
                          >
                          <span class="subtle">
                            {{
                              item.isSimulated
                                ? 'Simulator: no real money moved.'
                                : 'Signature, amount and currency matched the order.'
                            }}
                          </span>
                          <span class="subtle">Seen {{ when(item.detectedAtUtc) }}</span>
                        </span>
                      </td>
                      <td data-label="Order">
                        <span class="cell">
                          <span class="cluster">
                            <code [title]="item.orderId">{{ short(item.orderId) }}</code>
                            <app-status-badge
                              kind="order"
                              [value]="item.orderStatus"
                              [simulated]="item.isSimulated"
                            />
                          </span>
                          <span class="subtle">{{ orderNote(item) }}</span>
                          <span class="subtle"
                            >Fulfilment: <strong>not applied</strong>. No tickets from this
                            payment.</span
                          >
                          @if (item.orderRefundState) {
                            <span class="subtle"
                              >The order's own refund:
                              <app-status-badge kind="refund" [value]="item.orderRefundState"
                            /></span>
                          }
                        </span>
                      </td>
                      <td data-label="Status">
                        <span class="cell">
                          <app-status-badge kind="exception" [value]="item.state" />
                          @if (item.state === 'Resolved') {
                            <span class="subtle">
                              Recorded {{ when(item.resolvedAtUtc) }} by administrator
                              <code [title]="item.resolvedBy ?? ''">{{
                                short(item.resolvedBy ?? '')
                              }}</code
                              >. The provider did not confirm it.
                            </span>
                            @if (item.resolutionReference) {
                              <span class="subtle"
                                >Reference: <code>{{ item.resolutionReference }}</code></span
                              >
                            }
                          } @else {
                            <span class="subtle"
                              >Refund required. This app does not refund this payment.</span
                            >
                          }
                        </span>
                      </td>
                      <td class="actions-cell">
                        @if (canResolve(item)) {
                          <span class="subtle"
                            >Refund it in the {{ label(item) }} dashboard first.</span
                          >
                          <button
                            type="button"
                            class="btn btn--secondary btn--sm"
                            (click)="ask(item)"
                          >
                            Record refund<span class="visually-hidden">
                              for order {{ short(item.orderId) }}</span
                            >
                          </button>
                        } @else {
                          <span class="subtle">Nothing more to do here.</span>
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
      [open]="pending() !== null"
      heading="Record that this payment was refunded?"
      message="Use this only after you have already returned the money in the provider's dashboard."
      confirmLabel="Record refund"
      busyLabel="Recording…"
      tone="danger"
      [busy]="busy()"
      [confirmDisabled]="!confirmed() || conflict()"
      (confirmed)="submit()"
      (cancelled)="cancel()"
    >
      @if (pending(); as item) {
        <dl class="facts dialog-facts">
          <dt>Order</dt>
          <dd>
            <code>{{ item.orderId }}</code>
          </dd>
          <dt>Provider</dt>
          <dd>{{ label(item) }}</dd>
          <dt>Amount</dt>
          <dd>
            <strong>{{ money(item.amount, item.currency) }}</strong> (the payment the provider
            reported; it cannot be changed)
            @if (item.isSimulated) {
              (simulated: no real money moved)
            }
          </dd>
        </dl>
        <ul class="consequences">
          <li>
            Nothing is sent to {{ label(item) }}. This app cannot check that the refund happened,
            and {{ label(item) }} has not confirmed it.
          </li>
          <li>
            The exception becomes <strong>Resolved (administrator statement)</strong>. The failed
            attempt, the order and its seats are not changed, and no refund record is created.
          </li>
          <li>
            Your administrator id and the time are saved in the audit log. It cannot be undone or
            recorded twice.
          </li>
        </ul>
        <div class="field">
          <label class="field__label" for="exception-reference"
            >{{ label(item) }} reference (optional)</label
          >
          <input
            id="exception-reference"
            class="input"
            type="text"
            maxlength="200"
            autocomplete="off"
            [value]="reference()"
            [disabled]="busy()"
            (input)="reference.set($any($event.target).value)"
          />
        </div>
        <div class="field">
          <label class="field__label" for="exception-note">Note (optional)</label>
          <input
            id="exception-note"
            class="input"
            type="text"
            maxlength="300"
            autocomplete="off"
            [value]="note()"
            [disabled]="busy()"
            (input)="note.set($any($event.target).value)"
          />
          <span class="field__hint">Do not enter card numbers or other payment details.</span>
        </div>
        <label class="checkbox">
          <input
            type="checkbox"
            [checked]="confirmed()"
            [disabled]="busy() || conflict()"
            (change)="confirmed.set($any($event.target).checked)"
          />
          <span
            >I have refunded {{ money(item.amount, item.currency) }} for this payment in the
            {{ label(item) }} dashboard.</span
          >
        </label>
      }
      @if (error$(); as message) {
        <app-alert tone="error">{{ message }}</app-alert>
      }
    </app-confirm-dialog>
  `,
  styles: `
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
export class Exceptions {
  private readonly api = inject(AdminPaymentsApiClient);
  private readonly toast = inject(ToastService);
  private readonly injector = inject(Injector);
  private sub: Subscription | null = null;

  protected readonly options: readonly FilterOption[] = PAYMENT_EXCEPTION_STATES.map((value) => ({
    value,
    label: statusView('exception', value).label,
  }));

  protected readonly list = createOpsList<PaymentException>({
    allowed: PAYMENT_EXCEPTION_STATES,
    fetch: (filter, limit) => this.api.exceptions(filter, limit),
    liveAreas: ['exceptions'],
    searchText: (e) =>
      [
        e.exceptionId,
        e.orderId,
        e.attemptId,
        e.provider,
        e.state,
        e.orderStatus,
        e.originalFailureCode,
      ].join(' '),
  });

  protected readonly pending = signal<PaymentException | null>(null);
  protected readonly busy = signal(false);
  protected readonly error$ = signal<string | null>(null);
  protected readonly conflict = signal(false);
  protected readonly confirmed = signal(false);
  protected readonly reference = signal('');
  protected readonly note = signal('');

  protected readonly canResolve = canResolveException;
  protected readonly orderNote = exceptionOrderNote;
  protected readonly short = shortId;
  protected readonly when = formatDateTime;
  protected readonly money = formatMoney;
  protected readonly error = shortError;
  protected readonly label = (e: PaymentException) => providerLabel(e.provider, e.isSimulated);
  protected readonly attemptLabel = (e: PaymentException) =>
    statusView('payment', e.originalAttemptStatus, false).label;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.sub?.unsubscribe());
  }

  protected errorOf() {
    const s = this.list.state();
    return s.kind === 'error' ? s.error : null;
  }

  protected ask(item: PaymentException): void {
    this.error$.set(null);
    this.conflict.set(false);
    this.confirmed.set(false);
    this.reference.set('');
    this.note.set('');
    this.pending.set(item);
  }

  protected cancel(): void {
    if (!this.busy()) {
      this.pending.set(null);
      this.error$.set(null);
    }
  }

  /** Nothing is called resolved until the server answers; a second click while a request is in flight does nothing. */
  protected submit(): void {
    const item = this.pending();
    if (item === null || this.busy() || !this.confirmed() || this.conflict()) {
      return;
    }
    this.busy.set(true);
    this.error$.set(null);
    this.sub = this.api
      .resolveException(item.exceptionId, {
        refundPerformedExternally: true,
        amount: item.amount,
        currency: item.currency,
        externalReference: this.reference().trim() || null,
        note: this.note().trim() || null,
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.pending.set(null);
          this.toast.success(
            'Recorded as resolved by your statement. The provider has not confirmed any refund.',
          );
          this.list.reload(() => restoreFocus(this.injector, 'exceptions-heading'));
        },
        error: (e: unknown) => {
          const err = toApiError(e);
          this.busy.set(false);
          if (err.status === 409 || err.status === 404) {
            this.conflict.set(true);
            this.error$.set(
              err.status === 404
                ? 'This exception no longer exists. Nothing was recorded.'
                : `${err.message} Nothing new was recorded; the list has been refreshed.`,
            );
            this.list.reload();
          } else if (err.status === 401) {
            this.pending.set(null);
            this.toast.error('Your session has ended. Sign in again to continue.');
          } else if (err.status === 403) {
            this.error$.set(
              err.code === 'Csrf.Invalid'
                ? 'The security token was rejected. Reload the page and try again.'
                : 'The server refused this action: administrator access is required.',
            );
          } else {
            this.error$.set(`${err.message} Nothing was recorded; you can try again.`);
          }
        },
      });
  }
}

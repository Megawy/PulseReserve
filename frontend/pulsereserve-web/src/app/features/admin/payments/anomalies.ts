import { Component, DestroyRef, Injector, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import {
  ANOMALY_RESOLUTIONS,
  AnomalyResolution,
  PAYMENT_EXCEPTION_STATES,
  PaymentAnomaly,
} from '../../../core/api/admin-payments.models';
import { toApiError } from '../../../core/api/api-error';
import { formatDateTime, formatDuration, formatMinorUnits, shortId } from '../../../shared/format';
import { anomalyKindLabel, anomalyResolutionLabel, statusView } from '../../../shared/presentation';
import { Alert } from '../../../shared/ui/alert/alert';
import { ConfirmDialog } from '../../../shared/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '../../../shared/ui/empty-state/empty-state';
import { ErrorState } from '../../../shared/ui/error-state/error-state';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { createOpsList, restoreFocus } from './ops-flow';
import { canInvestigateAnomaly } from './ops-rules';
import { FilterOption, OpsToolbar } from './ops-toolbar';

/**
 * Payment anomalies: a success event whose SIGNATURE was verified but that this system did not apply, because its amount,
 * currency, reference or association disagree with its own records. A valid signature proves who sent the event, not that its
 * contents are right, so nothing was paid, ticketed, sold or refunded because of it. The page keeps what the event CLAIMED (untrusted)
 * apart from what this system's records SAY, and the one action is the server's own: record the outcome of an investigation as an
 * administrator statement. That statement is not provider verification and changes no order, attempt or refund.
 */
@Component({
  selector: 'app-admin-anomalies',
  imports: [Alert, ConfirmDialog, EmptyState, ErrorState, OpsToolbar, StatusBadge],
  template: `
    <section class="stack" aria-labelledby="anomalies-heading">
      <div class="stack stack--tight">
        <h2 id="anomalies-heading" tabindex="-1">Payment anomalies</h2>
        <p class="muted">
          An anomaly is a payment event that carried a <strong>valid provider signature</strong> but
          disagreed with this system's records (for example a different amount, or a payment for an
          attempt it does not know). A valid signature proves who sent it, not that its contents are
          right, so
          <strong
            >nothing was paid, no tickets were issued, and no refund was made or created</strong
          >
          because of it.
        </p>
        <p class="muted">
          Compare the event with the provider's dashboard before recording the outcome. Recording it
          is your statement; the provider does not confirm it to this app, and no order, attempt or
          refund is changed.
        </p>
      </div>

      <app-ops-toolbar
        id="anomalies"
        filterLabel="State"
        noun="anomalies"
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
          <p class="visually-hidden" role="status">Loading payment anomalies…</p>
          <div class="panel stack" aria-hidden="true">
            @for (n of [1, 2, 3]; track n) {
              <span class="skeleton skeleton--line"></span>
            }
          </div>
        }
        @case ('error') {
          @if (errorOf(); as err) {
            <app-error-state
              heading="We could not load payment anomalies"
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
                list.loadedCount() === 0 ? 'No payment anomalies' : 'No anomalies match your search'
              "
              [message]="
                list.loadedCount() === 0
                  ? 'Nothing matches this state filter. An anomaly appears only when a signature-verified success event could not be applied.'
                  : 'Clear the search box to see all loaded anomalies.'
              "
            />
          } @else {
            <div class="table-wrap">
              <table class="table table--stack">
                <caption class="visually-hidden">
                  Payment anomalies, open ones first and oldest first
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Event</th>
                    <th scope="col">The event claimed (untrusted)</th>
                    <th scope="col">This system's records say</th>
                    <th scope="col">Effect</th>
                    <th scope="col">Status</th>
                    <th scope="col"><span class="visually-hidden">Action</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (item of list.visible(); track item.anomalyId) {
                    <tr>
                      <td data-label="Event">
                        <span class="cell">
                          <strong>{{ kind(item.kind) }}</strong>
                          <span class="subtle"
                            ><span class="badge">{{ item.provider }}</span>
                            @if (item.providerEventId) {
                              event
                              <code [title]="item.providerEventId">{{ item.providerEventId }}</code>
                            }
                          </span>
                          <span class="subtle">Seen {{ when(item.detectedAtUtc) }}</span>
                          @if (item.state === 'Open') {
                            <span class="subtle"
                              ><strong>Waiting {{ duration(item.ageSeconds) }}</strong></span
                            >
                          }
                        </span>
                      </td>
                      <td data-label="The event claimed (untrusted)">
                        <span class="cell">
                          <span
                            >Amount
                            <strong>{{
                              minor(item.receivedAmountMinor, item.receivedCurrency)
                            }}</strong></span
                          >
                          @if (item.claimedAttemptId) {
                            <span class="subtle"
                              >Attempt
                              <code [title]="item.claimedAttemptId">{{
                                short(item.claimedAttemptId)
                              }}</code></span
                            >
                          }
                          @if (item.claimedOrderId) {
                            <span class="subtle"
                              >Order
                              <code [title]="item.claimedOrderId">{{
                                short(item.claimedOrderId)
                              }}</code></span
                            >
                          }
                          <span class="subtle">Taken from the event as received.</span>
                        </span>
                      </td>
                      <td data-label="This system's records say">
                        <span class="cell">
                          @if (item.orderId) {
                            <span
                              >Order
                              <code [title]="item.orderId">{{ short(item.orderId) }}</code>
                              expects
                              <strong>{{
                                minor(item.expectedAmountMinor, item.expectedCurrency)
                              }}</strong></span
                            >
                            <span class="subtle"
                              >Attempt was {{ item.attemptStatusAtDetection ?? 'unknown' }}; order
                              was {{ item.orderStatusAtDetection ?? 'unknown' }}
                              @if (item.orderStatus) {
                                , now {{ item.orderStatus }}
                              }
                              .</span
                            >
                          } @else {
                            <span>No local payment was matched.</span>
                            <span class="subtle"
                              >This event is not linked to any order or attempt.</span
                            >
                          }
                        </span>
                      </td>
                      <td data-label="Effect">
                        <span class="cell">
                          <strong>Not applied</strong>
                          <span class="subtle"
                            >No order was paid, no ticket issued, no refund made or created.</span
                          >
                        </span>
                      </td>
                      <td data-label="Status">
                        <span class="cell">
                          <app-status-badge kind="exception" [value]="item.state" />
                          @if (item.state === 'Resolved') {
                            <span class="subtle">
                              {{ resolutionLabel(item.resolution) }}. Recorded
                              {{ when(item.resolvedAtUtc) }} by administrator
                              <code [title]="item.resolvedBy ?? ''">{{
                                short(item.resolvedBy)
                              }}</code
                              >. The provider did not confirm it.
                            </span>
                            @if (item.resolutionReference) {
                              <span class="subtle"
                                >Reference: <code>{{ item.resolutionReference }}</code></span
                              >
                            }
                          } @else {
                            <span class="subtle">Nobody has recorded an outcome yet.</span>
                          }
                        </span>
                      </td>
                      <td class="actions-cell">
                        @if (canInvestigate(item)) {
                          <span class="subtle"
                            >Check it in the {{ item.provider }} dashboard first.</span
                          >
                          <button
                            type="button"
                            class="btn btn--secondary btn--sm"
                            (click)="ask(item)"
                          >
                            Record outcome<span class="visually-hidden">
                              for event {{ item.providerEventId ?? short(item.anomalyId) }}</span
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
      heading="Record the outcome of this investigation?"
      message="Use this only after you compared the event with the provider's dashboard."
      confirmLabel="Record outcome"
      busyLabel="Recording…"
      tone="danger"
      [busy]="busy()"
      [confirmDisabled]="resolution() === null || !confirmed() || conflict()"
      (confirmed)="submit()"
      (cancelled)="cancel()"
    >
      @if (pending(); as item) {
        <dl class="facts dialog-facts">
          <dt>Provider</dt>
          <dd>{{ item.provider }}</dd>
          <dt>Event</dt>
          <dd>
            <code>{{ item.providerEventId ?? item.anomalyId }}</code>
          </dd>
          <dt>The event claimed</dt>
          <dd>{{ minor(item.receivedAmountMinor, item.receivedCurrency) }} (untrusted)</dd>
        </dl>
        <ul class="consequences">
          <li>
            Nothing is sent to {{ item.provider }}, and the provider has not confirmed your
            conclusion.
          </li>
          <li>
            The order, the payment attempt, the refund records, the seats and the tickets are not
            changed. Recording an outcome does not pay or refund anything.
          </li>
          <li>
            Your administrator id and the time are saved in the audit log. It cannot be undone or
            recorded twice.
          </li>
        </ul>
        <fieldset class="field outcomes" [disabled]="busy() || conflict()">
          <legend class="field__label">What did you conclude?</legend>
          @for (option of resolutions; track option) {
            <label class="checkbox">
              <input
                type="radio"
                name="anomaly-resolution"
                [value]="option"
                [checked]="resolution() === option"
                (change)="resolution.set(option)"
              />
              <span>{{ resolutionLabel(option) }}</span>
            </label>
          }
        </fieldset>
        <div class="field">
          <label class="field__label" for="anomaly-reference">Reference (optional)</label>
          <input
            id="anomaly-reference"
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
          <label class="field__label" for="anomaly-note">Note (optional)</label>
          <input
            id="anomaly-note"
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
            >I compared this event with the {{ item.provider }} dashboard and the conclusion above
            is mine.</span
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
    .outcomes {
      margin: var(--space-3) 0 0;
      padding: 0;
      border: 0;
    }
  `,
})
export class Anomalies {
  private readonly api = inject(AdminPaymentsApiClient);
  private readonly toast = inject(ToastService);
  private readonly injector = inject(Injector);
  private sub: Subscription | null = null;

  protected readonly options: readonly FilterOption[] = PAYMENT_EXCEPTION_STATES.map((value) => ({
    value,
    label: statusView('exception', value).label,
  }));
  protected readonly resolutions = ANOMALY_RESOLUTIONS;

  protected readonly list = createOpsList<PaymentAnomaly>({
    allowed: PAYMENT_EXCEPTION_STATES,
    fetch: (filter, limit) => this.api.anomalies(filter, limit),
    liveAreas: ['anomalies'],
    searchText: (a) =>
      [
        a.anomalyId,
        a.providerEventId,
        a.provider,
        a.kind,
        a.reasonCode,
        a.state,
        a.orderId,
        a.claimedOrderId,
        a.claimedAttemptId,
      ].join(' '),
  });

  protected readonly pending = signal<PaymentAnomaly | null>(null);
  protected readonly busy = signal(false);
  protected readonly error$ = signal<string | null>(null);
  protected readonly conflict = signal(false);
  protected readonly confirmed = signal(false);
  protected readonly resolution = signal<AnomalyResolution | null>(null);
  protected readonly reference = signal('');
  protected readonly note = signal('');

  protected readonly canInvestigate = canInvestigateAnomaly;
  protected readonly kind = anomalyKindLabel;
  protected readonly resolutionLabel = anomalyResolutionLabel;
  protected readonly short = shortId;
  protected readonly when = formatDateTime;
  protected readonly minor = formatMinorUnits;
  protected readonly duration = formatDuration;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.sub?.unsubscribe());
  }

  protected errorOf() {
    const s = this.list.state();
    return s.kind === 'error' ? s.error : null;
  }

  protected ask(item: PaymentAnomaly): void {
    this.error$.set(null);
    this.conflict.set(false);
    this.confirmed.set(false);
    this.resolution.set(null);
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
    const chosen = this.resolution();
    if (item === null || chosen === null || this.busy() || !this.confirmed() || this.conflict()) {
      return;
    }
    this.busy.set(true);
    this.error$.set(null);
    this.sub = this.api
      .resolveAnomaly(item.anomalyId, {
        investigationConfirmed: true,
        resolution: chosen,
        externalReference: this.reference().trim() || null,
        note: this.note().trim() || null,
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.pending.set(null);
          this.toast.success(
            'Outcome recorded as your statement. The provider has not confirmed it, and nothing else was changed.',
          );
          this.list.reload(() => restoreFocus(this.injector, 'anomalies-heading'));
        },
        error: (e: unknown) => {
          const err = toApiError(e);
          this.busy.set(false);
          if (err.status === 409 || err.status === 404) {
            this.conflict.set(true);
            this.error$.set(
              err.status === 404
                ? 'This anomaly no longer exists. Nothing was recorded.'
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
          } else if (err.status === 429) {
            this.error$.set(err.message);
          } else {
            this.error$.set(`${err.message} Nothing was recorded; you can try again.`);
          }
        },
      });
  }
}

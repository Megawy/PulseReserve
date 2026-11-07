import { Component, DestroyRef, Injector, OnInit, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import {
  REFUND_STATES,
  WORK_STATES,
  WorkflowRunResult,
  WorkflowSummary,
} from '../../../core/api/admin-payments.models';
import { ApiError, toApiError } from '../../../core/api/api-error';
import { createLiveRefresh } from '../../../core/live/live-refresh';
import { formatDuration } from '../../../shared/format';
import { Alert } from '../../../shared/ui/alert/alert';
import { ErrorState } from '../../../shared/ui/error-state/error-state';
import { StatTile } from '../../../shared/ui/stat-tile/stat-tile';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { restoreFocus } from './ops-flow';

type State =
  | { kind: 'loading' }
  | { kind: 'ready'; summary: WorkflowSummary }
  | { kind: 'error'; error: ApiError };

type RunState =
  | { kind: 'idle' }
  | { kind: 'running' }
  | { kind: 'done'; result: WorkflowRunResult }
  | { kind: 'error'; message: string };

/** Live counts straight from `GET /api/admin/payments/overview`, plus the "process now" action. No derived metrics. */
@Component({
  selector: 'app-admin-overview',
  imports: [Alert, ErrorState, StatTile, StatusBadge],
  template: `
    @switch (state().kind) {
      @case ('loading') {
        <p class="visually-hidden" role="status">Loading operations overview…</p>
        <ul class="tiles" aria-hidden="true">
          @for (n of [1, 2, 3, 4, 5, 6, 7]; track n) {
            <li class="tile">
              <span class="skeleton skeleton--line skeleton--medium"></span>
              <span class="skeleton skeleton--title"></span>
            </li>
          }
        </ul>
      }
      @case ('error') {
        @if (error(); as err) {
          <app-error-state
            heading="We could not load the overview"
            [message]="err.message"
            [retryable]="true"
            (retry)="load()"
          />
        }
      }
      @default {
        @if (summary(); as s) {
          <section class="stack" aria-labelledby="needs-heading">
            <h2 id="needs-heading">Needs attention</h2>
            <ul class="tiles">
              <li>
                <app-stat-tile
                  label="Reconciliation needs review"
                  [value]="s.reconciliationNeedsReview"
                  hint="Automatic retries exhausted"
                  [attention]="s.reconciliationNeedsReview > 0"
                  [link]="['/admin/payments/reconciliation']"
                  [queryParams]="{ state: 'NeedsReview' }"
                />
              </li>
              <li>
                <app-stat-tile
                  label="Refunds needing attention"
                  [value]="s.refundsNeedingAttention"
                  hint="Provider refused or unconfirmed"
                  [attention]="s.refundsNeedingAttention > 0"
                  [link]="['/admin/payments/refunds']"
                />
              </li>
              <li>
                <app-stat-tile
                  label="Payment exceptions"
                  [value]="s.paymentExceptionsOpen ?? 0"
                  [hint]="exceptionHint(s)"
                  [attention]="(s.paymentExceptionsOpen ?? 0) > 0"
                  [link]="['/admin/payments/exceptions']"
                  [queryParams]="{ state: 'Open' }"
                />
              </li>
              <li>
                <app-stat-tile
                  label="Payment anomalies"
                  [value]="s.paymentAnomaliesOpen ?? 0"
                  [hint]="anomalyHint(s)"
                  [attention]="(s.paymentAnomaliesOpen ?? 0) > 0"
                  [link]="['/admin/payments/anomalies']"
                  [queryParams]="{ state: 'Open' }"
                />
              </li>
              <li>
                <app-stat-tile
                  label="Outbox needs review"
                  [value]="s.outboxNeedsReview"
                  hint="Automatic retries exhausted"
                  [attention]="s.outboxNeedsReview > 0"
                  [link]="['/admin/payments/outbox']"
                  [queryParams]="{ state: 'NeedsReview' }"
                />
              </li>
              <li>
                <app-stat-tile
                  label="Orders awaiting a refund"
                  [value]="s.ordersAwaitingRefund"
                  hint="Paid after the seats were gone"
                  [attention]="s.ordersAwaitingRefund > 0"
                  [link]="['/admin/payments/refunds']"
                />
              </li>
            </ul>
          </section>

          <section class="stack section" aria-labelledby="flow-heading">
            <h2 id="flow-heading">In progress</h2>
            <ul class="tiles">
              <li>
                <app-stat-tile
                  label="Reconciliation queued"
                  [value]="s.reconciliationPending"
                  hint="Handled automatically"
                  [link]="['/admin/payments/reconciliation']"
                  [queryParams]="{ state: 'Pending' }"
                />
              </li>
              <li>
                <app-stat-tile
                  label="Refunds open"
                  [value]="s.refundsOpen"
                  hint="Not yet provider-confirmed"
                  [link]="['/admin/payments/refunds']"
                />
              </li>
              <li>
                <app-stat-tile
                  label="Outbox queued"
                  [value]="s.outboxPending"
                  hint="Delivered at least once"
                  [link]="['/admin/payments/outbox']"
                  [queryParams]="{ state: 'Pending' }"
                />
              </li>
            </ul>
          </section>

          <section class="stack section" aria-labelledby="states-heading">
            <h2 id="states-heading">Everything, by state</h2>
            <div class="breakdowns">
              <div class="panel">
                <h3 class="panel__title">Reconciliation tasks</h3>
                <dl class="facts">
                  @for (st of workStates; track st) {
                    <dt><app-status-badge kind="work" [value]="st" /></dt>
                    <dd class="num">{{ s.tasksByState[st] ?? 0 }}</dd>
                  }
                </dl>
              </div>
              <div class="panel">
                <h3 class="panel__title">Refunds</h3>
                <dl class="facts">
                  @for (st of refundStates; track st) {
                    <dt><app-status-badge kind="refund" [value]="st" /></dt>
                    <dd class="num">{{ s.refundsByState[st] ?? 0 }}</dd>
                  }
                </dl>
              </div>
              <div class="panel">
                <h3 class="panel__title">Outbox events</h3>
                <dl class="facts">
                  @for (st of workStates; track st) {
                    <dt><app-status-badge kind="work" [value]="st" /></dt>
                    <dd class="num">{{ s.outboxByState[st] ?? 0 }}</dd>
                  }
                </dl>
              </div>
            </div>
          </section>
        }
      }
    }

    <section class="panel section" aria-labelledby="run-heading">
      <h2 id="run-heading" class="panel__title">Process now</h2>
      <p class="muted">
        Runs one pass of reconciliation, refunds and outbox delivery immediately. The background
        worker does the same on a timer, so this only speeds things up; it never skips the provider
        checks or the review states.
      </p>
      <div class="cluster">
        <button
          type="button"
          id="process-now-button"
          class="btn btn--primary"
          [disabled]="run().kind === 'running'"
          [attr.aria-busy]="run().kind === 'running'"
          (click)="processNow()"
        >
          {{ run().kind === 'running' ? 'Processing…' : 'Process now' }}
        </button>
      </div>

      @switch (run().kind) {
        @case ('done') {
          @if (result(); as r) {
            <div class="run-result stack">
              <app-alert
                [tone]="failures() > 0 ? 'warning' : 'success'"
                [heading]="failures() > 0 ? 'Pass finished with failures' : 'Pass finished'"
              >
                The server reported {{ processed() }} item(s) processed and {{ failures() }} failed.
                @if (failures() > 0) {
                  Failed items stay queued or move to review; check the lists below.
                }
              </app-alert>
              <dl class="facts">
                <dt>Tasks discovered</dt>
                <dd class="num">{{ r.tasksDiscovered }}</dd>
                <dt>Tasks processed / failed</dt>
                <dd class="num">{{ r.tasksProcessed }} / {{ r.tasksFailed }}</dd>
                <dt>Refunds processed / failed</dt>
                <dd class="num">{{ r.refundsProcessed }} / {{ r.refundsFailed }}</dd>
                <dt>Outbox processed / failed</dt>
                <dd class="num">{{ r.outboxProcessed }} / {{ r.outboxFailed }}</dd>
              </dl>
            </div>
          }
        }
        @case ('error') {
          <app-alert tone="error" heading="The pass could not run">{{ runMessage() }}</app-alert>
        }
      }
    </section>
  `,
  styles: `
    .section {
      margin-top: var(--space-8);
    }
    .breakdowns {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr));
      gap: var(--space-4);
    }
    .tiles > li {
      display: contents;
    }
    .run-result {
      margin-top: var(--space-4);
    }
    .panel .cluster {
      margin-top: var(--space-4);
    }
  `,
})
export class Overview implements OnInit {
  private readonly api = inject(AdminPaymentsApiClient);
  private readonly toast = inject(ToastService);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private sub: Subscription | null = null;
  private runSub: Subscription | null = null;
  /** The counts are re-read when the server says the overview changed (or a gap was announced); the tiles keep their last values meanwhile. */
  private readonly liveRefresh = createLiveRefresh({
    matches: (n) => n.kind === 'admin' && n.area === 'overview',
    refresh: () => this.load(true),
  });

  protected readonly workStates = WORK_STATES;

  /** What an operator needs from the tile: how long the longest-waiting item has been waiting (server clock), not just a count. */
  protected anomalyHint(s: WorkflowSummary): string {
    const age = s.oldestOpenPaymentAnomalyAgeSeconds;
    return (s.paymentAnomaliesOpen ?? 0) > 0 && age != null
      ? `Verified events not applied. Oldest has waited ${formatDuration(age)}`
      : 'Verified events that were not applied';
  }

  protected exceptionHint(s: WorkflowSummary): string {
    const age = s.oldestOpenPaymentExceptionAgeSeconds;
    return (s.paymentExceptionsOpen ?? 0) > 0 && age != null
      ? `Success after a recorded failure. Oldest has waited ${formatDuration(age)}`
      : 'Provider success after a recorded failure';
  }

  protected readonly refundStates = REFUND_STATES;
  protected readonly state = signal<State>({ kind: 'loading' });
  protected readonly run = signal<RunState>({ kind: 'idle' });

  protected readonly summary = computed(() => {
    const s = this.state();
    return s.kind === 'ready' ? s.summary : null;
  });
  protected readonly error = computed(() => {
    const s = this.state();
    return s.kind === 'error' ? s.error : null;
  });
  protected readonly result = computed(() => {
    const r = this.run();
    return r.kind === 'done' ? r.result : null;
  });
  protected readonly runMessage = computed(() => {
    const r = this.run();
    return r.kind === 'error' ? r.message : '';
  });
  protected readonly processed = computed(() => {
    const r = this.result();
    return r ? r.tasksProcessed + r.refundsProcessed + r.outboxProcessed : 0;
  });
  protected readonly failures = computed(() => {
    const r = this.result();
    return r ? r.tasksFailed + r.refundsFailed + r.outboxFailed : 0;
  });

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => {
      this.sub?.unsubscribe();
      this.runSub?.unsubscribe();
    });
    this.load();
  }

  /** `quiet` keeps the tiles on screen while the new counts are read, and keeps them if the read fails (live refreshes). */
  protected load(quiet = false): void {
    this.sub?.unsubscribe();
    if (!quiet) {
      this.state.set({ kind: 'loading' });
    }
    this.liveRefresh.markRequested();
    this.sub = this.api.overview().subscribe({
      next: (summary) => this.state.set({ kind: 'ready', summary }),
      error: (error: unknown) => {
        if (!(quiet && this.state().kind === 'ready')) {
          this.state.set({ kind: 'error', error: toApiError(error) });
        }
      },
    });
  }

  protected processNow(): void {
    if (this.run().kind === 'running') {
      return;
    }
    this.run.set({ kind: 'running' });
    this.runSub = this.api.processNow().subscribe({
      next: (result) => {
        this.run.set({ kind: 'done', result });
        const failed = result.tasksFailed + result.refundsFailed + result.outboxFailed;
        if (failed > 0) {
          this.toast.show(`Pass finished; ${failed} item(s) failed.`, 'warning');
        } else {
          this.toast.success('Pass finished.');
        }
        this.load();
        restoreFocus(this.injector, 'process-now-button');
      },
      error: (e: unknown) => {
        const err = toApiError(e);
        const message =
          err.status === 403
            ? 'The server refused this action: administrator access is required.'
            : err.status === 401
              ? 'Your session has ended. Sign in again to continue.'
              : err.message;
        this.run.set({ kind: 'error', message });
        this.toast.error(message);
        restoreFocus(this.injector, 'process-now-button');
      },
    });
  }
}

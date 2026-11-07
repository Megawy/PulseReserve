import { Component, Injector, OnInit, inject } from '@angular/core';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { ReconciliationTask, WORK_STATES } from '../../../core/api/admin-payments.models';
import { formatDateTime, shortId } from '../../../shared/format';
import { statusView, taskKindLabel } from '../../../shared/presentation';
import { Alert } from '../../../shared/ui/alert/alert';
import { ConfirmDialog } from '../../../shared/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '../../../shared/ui/empty-state/empty-state';
import { ErrorState } from '../../../shared/ui/error-state/error-state';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { createActionFlow, createOpsList, createProviderDirectory, restoreFocus } from './ops-flow';
import {
  canRetryWork,
  paymentLookupUnsupported,
  shortError,
  unqueryableAttemptGuidance,
  workGuidance,
} from './ops-rules';
import { FilterOption, OpsToolbar } from './ops-toolbar';

/**
 * Reconciliation tasks: work that settles payments with the provider (finalize a success, resolve a stale attempt,
 * expire an order). Queued and processing tasks are automatic; only "Needs review" waits for a person, and re-queueing
 * it is not resolving it: the row stays unresolved until the worker marks it Completed.
 */
@Component({
  selector: 'app-admin-tasks',
  imports: [Alert, ConfirmDialog, EmptyState, ErrorState, OpsToolbar, StatusBadge],
  template: `
    <section class="stack" aria-labelledby="tasks-heading">
      <div>
        <h2 id="tasks-heading" tabindex="-1">Reconciliation tasks</h2>
        <p class="muted">
          <strong>Queued</strong> and <strong>Processing</strong> tasks are retried automatically.
          <strong>Needs review</strong> means automatic retries are exhausted: check the cause, then
          re-queue it. A re-queued task is not resolved until it shows <strong>Completed</strong>.
        </p>
      </div>

      <app-ops-toolbar
        id="tasks"
        filterLabel="State"
        noun="tasks"
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
          <p class="visually-hidden" role="status">Loading reconciliation tasks…</p>
          <div class="panel stack" aria-hidden="true">
            @for (n of [1, 2, 3, 4]; track n) {
              <span class="skeleton skeleton--line"></span>
            }
          </div>
        }
        @case ('error') {
          @if (errorOf(); as err) {
            <app-error-state
              heading="We could not load reconciliation tasks"
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
                list.loadedCount() === 0 ? 'No reconciliation tasks' : 'No tasks match your search'
              "
              [message]="
                list.loadedCount() === 0
                  ? 'Nothing matches this state filter.'
                  : 'Clear the search box to see all loaded tasks.'
              "
            />
          } @else {
            <div class="table-wrap">
              <table class="table table--stack">
                <caption class="visually-hidden">
                  Reconciliation tasks, newest first
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Task</th>
                    <th scope="col">State</th>
                    <th scope="col" class="num">Attempts</th>
                    <th scope="col">Last result</th>
                    <th scope="col">Created</th>
                    <th scope="col">Next</th>
                    <th scope="col"><span class="visually-hidden">Action</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (task of list.visible(); track task.id) {
                    <tr>
                      <td data-label="Task">
                        <span class="cell">
                          <strong>{{ kind(task) }}</strong>
                          <span class="subtle"
                            >Task <code [title]="task.id">{{ short(task.id) }}</code> · subject
                            <code [title]="task.subjectId">{{ short(task.subjectId) }}</code></span
                          >
                          @if (task.provider) {
                            <span class="badge">{{ task.provider }}</span>
                          }
                        </span>
                      </td>
                      <td data-label="State">
                        <app-status-badge kind="work" [value]="task.state" />
                      </td>
                      <td data-label="Attempts" class="num">{{ task.attempts }}</td>
                      <td data-label="Last result">
                        <span class="cell">
                          @if (task.lastOutcome) {
                            <span>{{ task.lastOutcome }}</span>
                          }
                          @if (error(task.lastError); as message) {
                            <span class="subtle" [title]="task.lastError">{{ message }}</span>
                          }
                          @if (!task.lastOutcome && !task.lastError) {
                            <span class="subtle">—</span>
                          }
                          @if (unqueryable(task); as note) {
                            <span class="subtle">{{ note }}</span>
                          }
                        </span>
                      </td>
                      <td data-label="Created">{{ when(task.createdAtUtc) }}</td>
                      <td data-label="Next">
                        {{ task.state === 'Pending' ? when(task.nextAttemptAtUtc) : '—' }}
                      </td>
                      <td class="actions-cell">
                        @if (canRetry(task.state)) {
                          <button
                            type="button"
                            class="btn btn--secondary btn--sm"
                            (click)="flow.ask(task)"
                          >
                            Re-queue<span class="visually-hidden"> task {{ short(task.id) }}</span>
                          </button>
                        } @else {
                          <span class="subtle">{{ guidance(task.state) }}</span>
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
      [open]="flow.pending() !== null"
      heading="Re-queue this reconciliation task?"
      message="The task goes back to Queued with its attempt count reset, and the worker will try it again on its next pass."
      confirmLabel="Re-queue task"
      busyLabel="Re-queuing…"
      tone="primary"
      [busy]="flow.busy()"
      (confirmed)="flow.confirm()"
      (cancelled)="flow.cancel()"
    >
      @if (flow.pending(); as task) {
        <dl class="facts dialog-facts">
          <dt>Task</dt>
          <dd>{{ kind(task) }}</dd>
          <dt>Task id</dt>
          <dd>
            <code>{{ task.id }}</code>
          </dd>
          <dt>Current state</dt>
          <dd>{{ stateLabel(task.state) }} after {{ task.attempts }} attempt(s)</dd>
        </dl>
        <p class="subtle">
          This does not resolve the task. It is resolved only when the worker completes it.
        </p>
      }
      @if (flow.error(); as message) {
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
  `,
})
export class Tasks implements OnInit {
  private readonly api = inject(AdminPaymentsApiClient);
  private readonly injector = inject(Injector);

  ngOnInit(): void {
    this.directory.load();
  }

  protected readonly options: readonly FilterOption[] = WORK_STATES.map((value) => ({
    value,
    label: statusView('work', value).label,
  }));

  protected readonly list = createOpsList<ReconciliationTask>({
    allowed: WORK_STATES,
    fetch: (filter, limit) => this.api.tasks(filter, limit),
    liveAreas: ['tasks'],
    searchText: (t) =>
      [t.id, t.subjectId, t.kind, taskKindLabel(t.kind), t.state, t.lastOutcome, t.lastError].join(
        ' ',
      ),
  });

  protected readonly flow = createActionFlow<ReconciliationTask, void>({
    run: (task) => this.api.retryTask(task.id),
    success: () =>
      'Task re-queued. It is not resolved yet: the worker will retry it on its next pass.',
    done: () => this.list.reload(() => restoreFocus(this.injector, 'tasks-heading')),
  });

  private readonly directory = createProviderDirectory(() => this.api.providers());
  /** For an unresolved task about a provider that cannot be queried: what the operator should do instead. */
  protected readonly unqueryable = (task: ReconciliationTask): string | null =>
    task.provider &&
    task.state !== 'Done' &&
    task.kind === 'ResolveStaleAttempt' &&
    paymentLookupUnsupported(task.provider, this.directory.providers())
      ? unqueryableAttemptGuidance(task.provider)
      : null;
  protected readonly canRetry = canRetryWork;
  protected readonly guidance = workGuidance;
  protected readonly short = shortId;
  protected readonly when = formatDateTime;
  protected readonly error = shortError;
  protected readonly kind = (t: ReconciliationTask) => taskKindLabel(t.kind);
  protected readonly stateLabel = (state: string) => statusView('work', state).label;

  protected errorOf() {
    const s = this.list.state();
    return s.kind === 'error' ? s.error : null;
  }
}

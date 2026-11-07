import { Component, Injector, inject } from '@angular/core';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { OutboxEvent, WORK_STATES } from '../../../core/api/admin-payments.models';
import { formatDateTime, shortId } from '../../../shared/format';
import { outboxTypeLabel, statusView } from '../../../shared/presentation';
import { Alert } from '../../../shared/ui/alert/alert';
import { ConfirmDialog } from '../../../shared/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '../../../shared/ui/empty-state/empty-state';
import { ErrorState } from '../../../shared/ui/error-state/error-state';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { createActionFlow, createOpsList, restoreFocus } from './ops-flow';
import { canRetryWork, shortError, workGuidance } from './ops-rules';
import { FilterOption, OpsToolbar } from './ops-toolbar';

/**
 * Outbox events: follow-up work recorded in the same transaction as the payment (issue tickets, send the order
 * confirmation). Delivery is at least once: a re-queued event runs its handler again, which can repeat its effect.
 */
@Component({
  selector: 'app-admin-outbox',
  imports: [Alert, ConfirmDialog, EmptyState, ErrorState, OpsToolbar, StatusBadge],
  template: `
    <section class="stack" aria-labelledby="outbox-heading">
      <div class="stack stack--tight">
        <h2 id="outbox-heading" tabindex="-1">Outbox events</h2>
        <p class="muted">
          <strong>Queued</strong> and <strong>Processing</strong> events are retried automatically.
          <strong>Needs review</strong> means automatic retries are exhausted: check the cause, then
          re-queue it. A re-queued event is not resolved until it shows <strong>Completed</strong>.
        </p>
        <p class="muted">
          Events are delivered <strong>at least once</strong>, never exactly once: re-queueing an
          event runs its handler again, so its effect (for example a confirmation message) can be
          repeated.
        </p>
      </div>

      <app-ops-toolbar
        id="outbox"
        filterLabel="State"
        noun="events"
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
          <p class="visually-hidden" role="status">Loading outbox events…</p>
          <div class="panel stack" aria-hidden="true">
            @for (n of [1, 2, 3, 4]; track n) {
              <span class="skeleton skeleton--line"></span>
            }
          </div>
        }
        @case ('error') {
          @if (errorOf(); as err) {
            <app-error-state
              heading="We could not load outbox events"
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
                list.loadedCount() === 0 ? 'No outbox events' : 'No events match your search'
              "
              [message]="
                list.loadedCount() === 0
                  ? 'Nothing matches this state filter.'
                  : 'Clear the search box to see all loaded events.'
              "
            />
          } @else {
            <div class="table-wrap">
              <table class="table table--stack">
                <caption class="visually-hidden">
                  Outbox events, newest first
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Event</th>
                    <th scope="col">State</th>
                    <th scope="col" class="num">Attempts</th>
                    <th scope="col">Last error</th>
                    <th scope="col">Created</th>
                    <th scope="col">Processed / next</th>
                    <th scope="col"><span class="visually-hidden">Action</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (event of list.visible(); track event.id) {
                    <tr>
                      <td data-label="Event">
                        <span class="cell">
                          <strong>{{ kind(event) }}</strong>
                          <span class="subtle"
                            >Event <code [title]="event.id">{{ short(event.id) }}</code> · order
                            <code [title]="event.aggregateId">{{
                              short(event.aggregateId)
                            }}</code></span
                          >
                        </span>
                      </td>
                      <td data-label="State">
                        <app-status-badge kind="work" [value]="event.state" />
                      </td>
                      <td data-label="Attempts" class="num">{{ event.attempts }}</td>
                      <td data-label="Last error">
                        @if (error(event.lastError); as message) {
                          <span [title]="event.lastError">{{ message }}</span>
                        } @else {
                          <span class="subtle">—</span>
                        }
                      </td>
                      <td data-label="Created">{{ when(event.createdAtUtc) }}</td>
                      <td data-label="Processed / next">
                        @if (event.processedAtUtc) {
                          {{ when(event.processedAtUtc) }}
                        } @else if (event.state === 'Pending') {
                          {{ when(event.nextAttemptAtUtc) }}
                        } @else {
                          —
                        }
                      </td>
                      <td class="actions-cell">
                        @if (canRetry(event.state)) {
                          <button
                            type="button"
                            class="btn btn--secondary btn--sm"
                            (click)="flow.ask(event)"
                          >
                            Re-queue<span class="visually-hidden">
                              event {{ short(event.id) }}</span
                            >
                          </button>
                        } @else {
                          <span class="subtle">{{ guidance(event.state) }}</span>
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
      heading="Re-queue this outbox event?"
      message="The event goes back to Queued with its attempt count reset, and the worker will deliver it again on its next pass."
      confirmLabel="Re-queue event"
      busyLabel="Re-queuing…"
      tone="primary"
      [busy]="flow.busy()"
      (confirmed)="flow.confirm()"
      (cancelled)="flow.cancel()"
    >
      @if (flow.pending(); as event) {
        <dl class="facts dialog-facts">
          <dt>Event</dt>
          <dd>{{ kind(event) }}</dd>
          <dt>Event id</dt>
          <dd>
            <code>{{ event.id }}</code>
          </dd>
          <dt>Order</dt>
          <dd>
            <code>{{ event.aggregateId }}</code>
          </dd>
          <dt>Current state</dt>
          <dd>{{ stateLabel(event.state) }} after {{ event.attempts }} attempt(s)</dd>
        </dl>
        <p class="subtle">
          Delivery is at least once: running the handler again can repeat its effect, such as a
          second confirmation message. The event is resolved only when the worker completes it.
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
export class Outbox {
  private readonly api = inject(AdminPaymentsApiClient);
  private readonly injector = inject(Injector);

  protected readonly options: readonly FilterOption[] = WORK_STATES.map((value) => ({
    value,
    label: statusView('work', value).label,
  }));

  protected readonly list = createOpsList<OutboxEvent>({
    allowed: WORK_STATES,
    fetch: (filter, limit) => this.api.outbox(filter, limit),
    liveAreas: ['outbox'],
    searchText: (e) =>
      [e.id, e.aggregateId, e.type, outboxTypeLabel(e.type), e.state, e.lastError].join(' '),
  });

  protected readonly flow = createActionFlow<OutboxEvent, void>({
    run: (event) => this.api.retryOutbox(event.id),
    success: () =>
      'Event re-queued. It is not resolved yet: the worker will deliver it again on its next pass.',
    done: () => this.list.reload(() => restoreFocus(this.injector, 'outbox-heading')),
  });

  protected readonly canRetry = canRetryWork;
  protected readonly guidance = workGuidance;
  protected readonly short = shortId;
  protected readonly when = formatDateTime;
  protected readonly error = shortError;
  protected readonly kind = (e: OutboxEvent) => outboxTypeLabel(e.type);
  protected readonly stateLabel = (state: string) => statusView('work', state).label;

  protected errorOf() {
    const s = this.list.state();
    return s.kind === 'error' ? s.error : null;
  }
}

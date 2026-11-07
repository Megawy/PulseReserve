import { Component, DestroyRef, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { FinancialTransition } from '../../../core/api/admin-payments.models';
import { ApiError, toApiError } from '../../../core/api/api-error';
import { formatDateTime, shortId } from '../../../shared/format';
import {
  entityTypeLabel,
  factBasisLabel,
  transitionSourceLabel,
} from '../../../shared/presentation';
import { Alert } from '../../../shared/ui/alert/alert';
import { EmptyState } from '../../../shared/ui/empty-state/empty-state';
import { ErrorState } from '../../../shared/ui/error-state/error-state';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

type State =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'ready'; items: readonly FinancialTransition[] }
  | { kind: 'error'; error: ApiError };

/**
 * The append-only financial transition history of one order: every state change of its attempts, refunds, exceptions and
 * anomalies, and every refused conflicting event, with who or what caused it and on what basis. A provider's verified fact, an
 * application decision, an administrator's command and an administrator's statement are labelled apart. It is read-only: nothing here
 * can change a record.
 */
@Component({
  selector: 'app-admin-history',
  imports: [Alert, EmptyState, ErrorState],
  template: `
    <section class="stack" aria-labelledby="history-heading">
      <div class="stack stack--tight">
        <h2 id="history-heading" tabindex="-1">Financial history</h2>
        <p class="muted">
          Every change of an order, its payment attempts, refunds, exceptions and anomalies is
          recorded when it happens, with its cause. The history is append-only and read-only here.
          <strong>Administrator statement</strong> rows record what someone said was done outside
          this system; only <strong>Provider-verified fact</strong> rows rest on a provider's
          verified answer.
        </p>
      </div>

      <form class="toolbar" (submit)="$event.preventDefault(); search()">
        <div class="field field--grow">
          <label class="field__label" for="history-order">Order id</label>
          <input
            id="history-order"
            class="input"
            type="text"
            autocomplete="off"
            spellcheck="false"
            placeholder="00000000-0000-0000-0000-000000000000"
            [value]="orderId()"
            [attr.aria-invalid]="invalid() ? 'true' : null"
            [attr.aria-describedby]="invalid() ? 'history-order-error' : null"
            (input)="orderId.set($any($event.target).value)"
          />
          @if (invalid()) {
            <span id="history-order-error" class="field__error"
              >Enter a full order id (a UUID).</span
            >
          }
        </div>
        <button type="submit" class="btn btn--primary" [disabled]="state().kind === 'loading'">
          Show history
        </button>
      </form>

      @switch (state().kind) {
        @case ('idle') {
          <p class="muted">Enter an order id to see its history.</p>
        }
        @case ('loading') {
          <p class="visually-hidden" role="status">Loading the financial history…</p>
          <div class="panel stack" aria-hidden="true">
            @for (n of [1, 2, 3]; track n) {
              <span class="skeleton skeleton--line"></span>
            }
          </div>
        }
        @case ('error') {
          @if (errorOf(); as err) {
            <app-error-state
              heading="We could not load the history"
              [message]="err.message"
              [retryable]="err.retryable"
              (retry)="search()"
            />
          }
        }
        @default {
          @if (items().length === 0) {
            <app-empty-state
              icon="✓"
              heading="No history for this order"
              message="Either the order id is wrong, or the order changed before the history was introduced: history is not reconstructed for earlier changes."
            />
          } @else {
            <p class="subtle" role="note">
              Every row below was written by the database at the moment the change happened. Nothing
              here is reconstructed afterwards. A row from <strong>Not specified</strong> means the
              change was made outside the application's labelled paths.
            </p>
            @if (partial().length > 0) {
              <app-alert tone="info" heading="Some history starts mid-life">
                The earliest recorded change of {{ partialNames() }} is already a move from an
                existing state, so its earlier changes were never recorded. They are not
                reconstructed here; check the provider's dashboard and the administrator audit log
                for what happened before {{ since() }}.
              </app-alert>
            }
            @if (truncated()) {
              <app-alert tone="warning" heading="Only the newest rows are shown">
                This order has at least {{ limit }} recorded changes, so older ones are not shown
                and the start of each history cannot be judged here.
              </app-alert>
            }
            <div class="table-wrap">
              <table class="table table--stack">
                <caption class="visually-hidden">
                  Financial history of the order, newest first
                </caption>
                <thead>
                  <tr>
                    <th scope="col">When</th>
                    <th scope="col">What</th>
                    <th scope="col">Change</th>
                    <th scope="col">Caused by</th>
                    <th scope="col">Basis</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of items(); track row.id) {
                    <tr>
                      <td data-label="When">{{ when(row.occurredAtUtc) }}</td>
                      <td data-label="What">
                        <span class="cell">
                          <strong>{{ entity(row.entityType) }}</strong>
                          <code [title]="row.entityId">{{ short(row.entityId) }}</code>
                        </span>
                      </td>
                      <td data-label="Change">
                        <span class="cell">
                          @if (row.kind === 'Rejected') {
                            <span class="badge badge--warning">Refused, nothing changed</span>
                            <span
                              >stayed {{ row.fromState }}; event asked for {{ row.toState }}</span
                            >
                          } @else {
                            <span
                              >{{ row.fromState ?? 'created' }} →
                              <strong>{{ row.toState }}</strong></span
                            >
                          }
                          <span class="subtle">{{ row.eventCode }}</span>
                        </span>
                      </td>
                      <td data-label="Caused by">
                        <span class="cell">
                          <span>{{ source(row.source) }}</span>
                          @if (row.provider) {
                            <span class="subtle">{{ row.provider }}</span>
                          }
                          @if (row.providerEventId) {
                            <span class="subtle"
                              >event <code>{{ row.providerEventId }}</code></span
                            >
                          }
                          @if (row.actorUserId) {
                            <span class="subtle"
                              >by
                              <code [title]="row.actorUserId">{{
                                short(row.actorUserId)
                              }}</code></span
                            >
                          }
                        </span>
                      </td>
                      <td data-label="Basis">
                        <span class="cell">
                          <span>{{ basis(row.factBasis) }}</span>
                          @if (row.reason) {
                            <span class="subtle">{{ row.reason }}</span>
                          }
                        </span>
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
  `,
  styles: `
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-end;
      gap: var(--space-3);
    }
    .field {
      min-width: 12rem;
    }
    .field--grow {
      flex: 1 1 22rem;
    }
    .cell {
      display: grid;
      gap: var(--space-1);
    }
  `,
})
export class History {
  private readonly api = inject(AdminPaymentsApiClient);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private sub: Subscription | null = null;

  protected readonly orderId = signal('');
  protected readonly invalid = signal(false);
  protected readonly state = signal<State>({ kind: 'idle' });

  protected readonly entity = entityTypeLabel;
  protected readonly source = transitionSourceLabel;
  protected readonly basis = factBasisLabel;
  protected readonly short = shortId;
  protected readonly when = formatDateTime;

  constructor() {
    const params = this.route.queryParamMap.subscribe((p) => {
      const order = p.get('order');
      if (order && GUID.test(order)) {
        this.orderId.set(order);
        this.load(order);
      }
    });
    inject(DestroyRef).onDestroy(() => {
      params.unsubscribe();
      this.sub?.unsubscribe();
    });
  }

  protected readonly limit = 200;

  /** Entities whose EARLIEST recorded change is already a move from an existing state: their history began mid-life. */
  protected partial(): readonly FinancialTransition[] {
    const rows = this.items();
    if (rows.length >= this.limit) {
      return [];
    }
    const earliest = new Map<string, FinancialTransition>();
    for (const row of rows) {
      if (row.kind !== 'Transition') {
        continue;
      }
      const seen = earliest.get(row.entityId);
      if (!seen || Date.parse(row.occurredAtUtc) < Date.parse(seen.occurredAtUtc)) {
        earliest.set(row.entityId, row);
      }
    }
    return [...earliest.values()].filter((row) => row.fromState !== null);
  }

  protected partialNames(): string {
    return this.partial()
      .map((row) => `${entityTypeLabel(row.entityType).toLowerCase()} ${shortId(row.entityId)}`)
      .join(', ');
  }

  protected since(): string {
    const times = this.partial().map((row) => Date.parse(row.occurredAtUtc));
    return formatDateTime(new Date(Math.min(...times)).toISOString());
  }

  protected truncated(): boolean {
    return this.items().length >= this.limit;
  }

  protected items(): readonly FinancialTransition[] {
    const s = this.state();
    return s.kind === 'ready' ? s.items : [];
  }

  protected errorOf(): ApiError | null {
    const s = this.state();
    return s.kind === 'error' ? s.error : null;
  }

  protected search(): void {
    const order = this.orderId().trim();
    if (!GUID.test(order)) {
      this.invalid.set(true);
      return;
    }
    this.invalid.set(false);
    // Keep the address bookmarkable: the order id lives in the URL.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { order },
      queryParamsHandling: 'merge',
    });
    this.load(order);
  }

  private load(order: string): void {
    this.sub?.unsubscribe();
    this.state.set({ kind: 'loading' });
    this.sub = this.api.history(order, null, this.limit).subscribe({
      next: (items) => this.state.set({ kind: 'ready', items }),
      error: (e: unknown) => this.state.set({ kind: 'error', error: toApiError(e) }),
    });
  }
}

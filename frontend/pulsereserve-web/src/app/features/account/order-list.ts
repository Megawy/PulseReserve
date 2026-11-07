import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription, catchError, forkJoin, map, of, switchMap } from 'rxjs';
import { ApiError, toApiError } from '../../core/api/api-error';
import { CatalogApiClient } from '../../core/api/catalog-api.client';
import { Order } from '../../core/api/order-api.models';
import { OrderApiClient } from '../../core/api/order-api.client';
import { createLiveRefresh } from '../../core/live/live-refresh';
import { formatDateTime, formatMoney } from '../../shared/format';
import { EmptyState } from '../../shared/ui/empty-state/empty-state';
import { ErrorState } from '../../shared/ui/error-state/error-state';
import { LiveIndicator } from '../../shared/ui/live-indicator/live-indicator';
import { StatusBadge } from '../../shared/ui/status-badge/status-badge';

type State =
  | { kind: 'loading' }
  | { kind: 'ready'; orders: readonly Order[]; eventNames: ReadonlyMap<string, string> }
  | { kind: 'error'; error: ApiError };

/** The signed-in customer's order history. Everything shown is what the server returned for their own orders. */
@Component({
  selector: 'app-order-list',
  imports: [RouterLink, EmptyState, ErrorState, LiveIndicator, StatusBadge],
  template: `
    <div class="container page">
      <header class="page-header">
        <div>
          <p class="page-header__eyebrow">Account</p>
          <h1>My orders</h1>
          <p class="page-header__lead">Your bookings, payments and tickets in one place.</p>
          <app-live-indicator />
        </div>
      </header>

      @switch (state().kind) {
        @case ('loading') {
          <p class="visually-hidden" role="status">Loading your orders…</p>
          <ul class="orders" aria-hidden="true">
            @for (n of [1, 2, 3]; track n) {
              <li class="card">
                <div class="card__body stack">
                  <span class="skeleton skeleton--title"></span>
                  <span class="skeleton skeleton--line skeleton--medium"></span>
                </div>
              </li>
            }
          </ul>
        }
        @case ('error') {
          @if (error(); as err) {
            <app-error-state
              heading="We could not load your orders"
              [message]="err.message"
              [retryable]="err.retryable"
              (retry)="load()"
            />
          }
        }
        @default {
          @if (rows().length === 0) {
            <app-empty-state
              icon="🎟"
              heading="No orders yet"
              message="When you check out, your orders and tickets will appear here."
            >
              <a class="btn btn--primary" routerLink="/">Browse events</a>
            </app-empty-state>
          } @else {
            <ul class="orders">
              @for (row of rows(); track row.order.orderId) {
                <li class="card card--interactive order-row">
                  <div class="card__body order-row__body">
                    <div class="stack stack--tight">
                      <h2 class="order-row__title">
                        <a [routerLink]="['/account/orders', row.order.orderId]">{{
                          row.eventName
                        }}</a>
                      </h2>
                      <p class="subtle">
                        {{ row.seats }} {{ row.seats === 1 ? 'seat' : 'seats' }} · Ordered
                        {{ row.placed }}
                      </p>
                    </div>
                    <div class="order-row__meta">
                      <app-status-badge
                        kind="order"
                        [value]="row.order.status"
                        [simulated]="row.order.payment?.isSimulated ?? true"
                      />
                      <span class="price">{{ row.total }}</span>
                    </div>
                  </div>
                </li>
              }
            </ul>
          }
        }
      }
    </div>
  `,
  styles: `
    .orders {
      display: grid;
      gap: var(--space-3);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .order-row__body {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3) var(--space-5);
    }
    .order-row__title {
      font-size: var(--font-size-lg);
    }
    .order-row__meta {
      display: flex;
      align-items: center;
      gap: var(--space-4);
    }
  `,
})
export class OrderList implements OnInit {
  private readonly orders = inject(OrderApiClient);
  private readonly catalog = inject(CatalogApiClient);
  private readonly destroyRef = inject(DestroyRef);
  private sub: Subscription | null = null;
  /** Any change to one of my orders (or an announced gap) re-reads the list quietly; the list on screen is replaced only by the server's answer. */
  private readonly liveRefresh = createLiveRefresh({
    matches: (n) => n.kind === 'order',
    refresh: () => this.load(true),
  });

  protected readonly state = signal<State>({ kind: 'loading' });
  protected readonly error = computed(() => {
    const s = this.state();
    return s.kind === 'error' ? s.error : null;
  });

  protected readonly rows = computed(() => {
    const s = this.state();
    if (s.kind !== 'ready') {
      return [];
    }
    return s.orders.map((order) => ({
      order,
      eventName: s.eventNames.get(order.eventId) ?? 'Event',
      seats: order.lines.reduce((n, l) => n + l.quantity, 0),
      total: formatMoney(order.totalAmount, order.currency),
      placed: formatDateTime(order.createdAtUtc),
    }));
  });

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => this.sub?.unsubscribe());
    this.load();
  }

  /** `quiet` keeps the current list on screen while the new one is read (live refreshes). */
  protected load(quiet = false): void {
    this.sub?.unsubscribe();
    if (!quiet) {
      this.state.set({ kind: 'loading' });
    }
    this.liveRefresh.markRequested();
    this.sub = this.orders
      .listOrders()
      .pipe(
        switchMap((orders) => {
          const ids = [...new Set(orders.map((o) => o.eventId))];
          if (ids.length === 0) {
            return of({ orders, eventNames: new Map<string, string>() });
          }
          // Event names are decoration: a failed lookup falls back to "Event" instead of failing the page.
          return forkJoin(
            ids.map((id) =>
              this.catalog.getEvent(id).pipe(
                map((e) => [id, e.name] as const),
                catchError(() => of([id, 'Event'] as const)),
              ),
            ),
          ).pipe(
            map((pairs) => ({ orders, eventNames: new Map(pairs) as ReadonlyMap<string, string> })),
          );
        }),
      )
      .subscribe({
        next: ({ orders, eventNames }) => this.state.set({ kind: 'ready', orders, eventNames }),
        error: (error: unknown) => {
          if (!(quiet && this.state().kind === 'ready')) {
            this.state.set({ kind: 'error', error: toApiError(error) });
          }
        },
      });
  }
}

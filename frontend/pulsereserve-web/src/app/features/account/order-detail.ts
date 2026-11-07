import {
  Component,
  DestroyRef,
  InjectionToken,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription, catchError, forkJoin, map, of, switchMap, timer } from 'rxjs';
import { ApiError, toApiError } from '../../core/api/api-error';
import { CatalogApiClient } from '../../core/api/catalog-api.client';
import { CatalogEvent } from '../../core/api/catalog-api.models';
import { OrderApiClient } from '../../core/api/order-api.client';
import { Order, Ticket } from '../../core/api/order-api.models';
import { createLiveRefresh } from '../../core/live/live-refresh';
import { formatDateRange } from '../../shared/format';
import { Alert, AlertTone } from '../../shared/ui/alert/alert';
import { ErrorState } from '../../shared/ui/error-state/error-state';
import { LiveIndicator } from '../../shared/ui/live-indicator/live-indicator';
import { OrderSummary } from '../../shared/ui/order-summary/order-summary';
import { TicketList } from '../../shared/ui/ticket-list/ticket-list';
import { seatNameMap } from '../events/seat-names';

type State =
  | { kind: 'loading' }
  | { kind: 'error'; error: ApiError }
  | {
      kind: 'ready';
      order: Order;
      event: CatalogEvent | null;
      seatNames: ReadonlyMap<string, string>;
      tickets: readonly Ticket[];
    };

interface Explanation {
  readonly tone: AlertTone;
  readonly heading: string;
  readonly text: string;
}

/** How often (and how many times) the page re-reads an order whose payment is being confirmed after a provider redirect. */
export const PAYMENT_CONFIRMATION_POLL = new InjectionToken<{
  intervalMs: number;
  maxPolls: number;
}>('PAYMENT_CONFIRMATION_POLL', {
  providedIn: 'root',
  factory: () => ({ intervalMs: 4000, maxPolls: 8 }),
});

/**
 * What the order's CURRENT server status means. `simulated` is false for a payment through a real provider, so real
 * payments are never described as simulated.
 */
function explain(order: Order, simulated: boolean): Explanation | null {
  switch (order.status) {
    case 'PendingPayment':
      return {
        tone: 'warning',
        heading: 'Payment pending',
        text: 'Your seats are held while the payment window is open. Complete checkout on the event page before it closes.',
      };
    case 'Cancelled':
      return {
        tone: 'info',
        heading: 'Order cancelled',
        text: 'This order was cancelled and its seats were released.',
      };
    case 'Expired':
      return {
        tone: 'info',
        heading: 'Order expired',
        text: 'The payment window ended and the seats were released.',
      };
    case 'RefundRequired':
      return {
        tone: 'warning',
        heading: 'Refund pending',
        text: simulated
          ? 'Your payment arrived after the hold expired, so the seats could not be sold. A refund is being processed (simulated: no real money moved).'
          : 'Your payment arrived after the hold expired, so the seats could not be sold. A refund is required and is complete only once the payment provider confirms it.',
      };
    case 'Refunded':
      return {
        tone: 'info',
        heading: 'Payment refunded',
        text: simulated
          ? 'Your payment was refunded (simulated: no real money moved).'
          : 'Your payment was refunded.',
      };
    default:
      return null;
  }
}

/** What the browser coming back from a provider's page may and may not claim: only the server's status decides. */
function returnNotice(order: Order, returned: 'return' | 'cancelled' | null): Explanation | null {
  if (!returned) {
    return null;
  }
  const real = order.payment && !order.payment.isSimulated;
  if (!real) {
    return null;
  }
  if (order.status === 'Paid') {
    return {
      tone: 'success',
      heading: 'Payment confirmed',
      text: 'The payment provider confirmed your payment. Your seats are yours.',
    };
  }
  if (order.status !== 'PendingPayment') {
    return null;
  }
  return returned === 'cancelled'
    ? {
        tone: 'info',
        heading: 'You left the payment page',
        text: 'Nothing is confirmed until the payment provider says so. If you did pay, this page updates once it does.',
      }
    : {
        tone: 'info',
        heading: 'Confirming your payment',
        text: 'You are back from the payment page. We are waiting for the provider to confirm the payment, and this page updates by itself. Returning here does not confirm it.',
      };
}

/** One order: its server-calculated lines, payment state and (once issued) tickets. Owner only; a foreign id is a 404. */
@Component({
  selector: 'app-order-detail',
  imports: [RouterLink, Alert, ErrorState, LiveIndicator, OrderSummary, TicketList],
  template: `
    <div class="container page">
      <a class="breadcrumb" routerLink="/account/orders">← My orders</a>

      @if (error(); as err) {
        <app-error-state
          [heading]="err.isNotFound ? 'Order not found' : 'We could not load this order'"
          [message]="err.message"
          [retryable]="err.retryable"
          (retry)="load()"
        />
      } @else if (ready(); as page) {
        <header class="page-header">
          <div>
            <p class="page-header__eyebrow">Order</p>
            <h1>{{ page.event?.name ?? 'Your order' }}</h1>
            @if (when(); as w) {
              <p class="page-header__lead">{{ w }}</p>
            }
            <app-live-indicator />
          </div>
          @if (page.event) {
            <a class="btn btn--secondary" [routerLink]="['/events', page.event.id]">View event</a>
          }
        </header>

        @if (returned(); as r) {
          <app-alert [tone]="r.tone" [heading]="r.heading">
            {{ r.text }}
            @if (polling()) {
              <button type="button" class="link-button" (click)="load()">Check now</button>
            }
          </app-alert>
        }
        @if (explanation(); as e) {
          <app-alert [tone]="e.tone" [heading]="e.heading">{{ e.text }}</app-alert>
        }

        <div class="detail-grid">
          <section class="card" aria-labelledby="summary-h">
            <div class="card__body stack">
              <h2 id="summary-h">Summary</h2>
              <app-order-summary [order]="page.order" [seatNames]="page.seatNames" />
            </div>
          </section>

          @if (page.order.status === 'Paid') {
            <section class="card" aria-labelledby="tickets-h">
              <div class="card__body stack">
                <h2 id="tickets-h">Tickets</h2>
                @if (page.tickets.length > 0) {
                  <app-ticket-list
                    [tickets]="page.tickets"
                    [seatNames]="page.seatNames"
                    [full]="true"
                  />
                } @else {
                  <p class="muted" role="status">Your tickets are being prepared.</p>
                  <button type="button" class="btn btn--secondary btn--sm" (click)="load()">
                    Check again
                  </button>
                }
              </div>
            </section>
          }
        </div>
      } @else {
        <p class="visually-hidden" role="status">Loading your order…</p>
        <div class="stack" aria-hidden="true">
          <span class="skeleton skeleton--title"></span>
          <span class="skeleton skeleton--block"></span>
        </div>
      }
    </div>
  `,
  styles: `
    .detail-grid {
      display: grid;
      gap: var(--space-5);
      margin-top: var(--space-5);
      align-items: start;
    }
    @media (min-width: 64rem) {
      .detail-grid {
        grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
      }
    }
  `,
})
export class OrderDetail implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly orders = inject(OrderApiClient);
  private readonly catalog = inject(CatalogApiClient);
  private readonly poll = inject(PAYMENT_CONFIRMATION_POLL);
  private readonly destroyRef = inject(DestroyRef);
  private sub: Subscription | null = null;
  private pollSub: Subscription | null = null;
  private orderId = '';
  private pollCount = 0;
  private readonly returnedFromProvider = signal<'return' | 'cancelled' | null>(null);
  /**
   * A live hint that this order changed, or that a gap was announced, re-reads it (and its tickets) from the API, quietly: the page
   * keeps showing the last server state until the new one arrives. The bounded polling after a provider redirect stays as the fallback.
   */
  private readonly liveRefresh = createLiveRefresh({
    matches: (n) => n.kind === 'order' && n.orderId === this.orderId,
    refresh: () => {
      if (this.orderId) {
        this.load(true);
      }
    },
  });

  protected readonly state = signal<State>({ kind: 'loading' });
  protected readonly error = computed(() => {
    const s = this.state();
    return s.kind === 'error' ? s.error : null;
  });
  protected readonly ready = computed(() => {
    const s = this.state();
    return s.kind === 'ready' ? s : null;
  });
  protected readonly explanation = computed(() => {
    const order = this.ready()?.order;
    if (!order) {
      return null;
    }
    // Coming back from a provider has its own, more specific message for a pending order: do not stack a second one.
    if (this.returned() && order.status === 'PendingPayment') {
      return null;
    }
    return explain(order, order.payment?.isSimulated ?? true);
  });
  protected readonly returned = computed(() => {
    const order = this.ready()?.order;
    return order ? returnNotice(order, this.returnedFromProvider()) : null;
  });
  /** True while the page is re-reading the order to learn the provider's verdict. */
  protected readonly polling = signal(false);
  protected readonly when = computed(() => {
    const event = this.ready()?.event;
    return event ? formatDateRange(event.scheduledStartUtc, event.scheduledEndUtc) : '';
  });

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => {
      this.sub?.unsubscribe();
      this.pollSub?.unsubscribe();
    });
    this.route.queryParamMap.subscribe((p) => {
      const value = p.get('payment');
      this.returnedFromProvider.set(value === 'return' || value === 'cancelled' ? value : null);
    });
    this.route.paramMap.subscribe((p) => {
      this.orderId = p.get('id') ?? '';
      this.pollCount = 0;
      this.load();
    });
  }

  /** `quiet` keeps the current page on screen while the new state is read (live refreshes); the default shows the loading skeleton. */
  protected load(quiet = false): void {
    this.sub?.unsubscribe();
    if (!quiet) {
      this.state.set({ kind: 'loading' });
    }
    this.liveRefresh.markRequested();
    this.sub = this.orders
      .getOrder(this.orderId)
      .pipe(
        switchMap((order) =>
          forkJoin({
            event: this.catalog.getEvent(order.eventId).pipe(catchError(() => of(null))),
            tickets:
              order.status === 'Paid'
                ? this.orders.getTickets(order.orderId).pipe(catchError(() => of([] as Ticket[])))
                : of([] as Ticket[]),
          }).pipe(map((extra) => ({ order, ...extra }))),
        ),
        switchMap((data) =>
          (data.event?.seatingLayoutId
            ? this.catalog
                .getSeatingLayout(data.event.seatingLayoutId)
                .pipe(catchError(() => of(null)))
            : of(null)
          ).pipe(map((layout) => ({ ...data, seatNames: seatNameMap(layout) }))),
        ),
      )
      .subscribe({
        next: (data) => {
          this.state.set({ kind: 'ready', ...data });
          this.scheduleConfirmationPoll(data.order);
        },
        error: (error: unknown) => {
          // A failed quiet refresh must not blank a page that is already showing the server's last answer.
          if (!(quiet && this.ready())) {
            this.state.set({ kind: 'error', error: toApiError(error) });
          }
        },
      });
  }

  /**
   * After a redirect back from a real provider the webhook may still be on its way. Re-read the order a few times
   * (the server's answer is the only thing that changes what this page says), then stop and leave "Check now".
   */
  private scheduleConfirmationPoll(order: Order): void {
    this.pollSub?.unsubscribe();
    const waiting =
      this.returnedFromProvider() === 'return' &&
      order.status === 'PendingPayment' &&
      order.payment?.isSimulated === false &&
      order.payment.status === 'Initiated';
    this.polling.set(waiting);
    if (!waiting || this.pollCount >= this.poll.maxPolls) {
      return;
    }
    this.pollSub = timer(this.poll.intervalMs).subscribe(() => {
      this.pollCount++;
      this.refreshQuietly();
    });
  }

  private refreshQuietly(): void {
    this.sub?.unsubscribe();
    this.sub = this.orders.getOrder(this.orderId).subscribe({
      next: (order) => {
        const current = this.ready();
        if (
          current &&
          order.status === current.order.status &&
          order.payment?.status === current.order.payment?.status
        ) {
          this.scheduleConfirmationPoll(order);
        } else {
          this.load(true); // something changed: re-read everything (including tickets), keeping the page on screen
        }
      },
      error: () => this.scheduleConfirmationPoll(this.ready()!.order),
    });
  }
}

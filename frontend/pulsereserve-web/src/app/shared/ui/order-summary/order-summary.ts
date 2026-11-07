import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Order, OrderLine } from '../../../core/api/order-api.models';
import { formatDateTime, formatMoney, isValidDate } from '../../format';
import { statusView } from '../../presentation';
import { StatusBadge } from '../status-badge/status-badge';

/**
 * Order facts and lines exactly as the server returned them: unit prices, line totals, currency and total are never
 * computed here. Used by the checkout panel and the order pages so an order always reads the same.
 */
@Component({
  selector: 'app-order-summary',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusBadge],
  template: `
    <dl class="facts">
      <dt>Order</dt>
      <dd class="mono">{{ shortId() }}</dd>
      <dt>Status</dt>
      <dd data-testid="order-status">
        <app-status-badge kind="order" [value]="order().status" [simulated]="simulated()" />
      </dd>
      @if (order().status === 'PendingPayment') {
        <dt>Pay within</dt>
        <dd class="countdown-inline" role="timer" aria-live="off">{{ remaining() ?? '…' }}</dd>
      }
      @if (attemptLabel(); as label) {
        <dt>Payment</dt>
        <dd data-testid="attempt-status">{{ label }}</dd>
      }
      @if (placed(); as when) {
        <dt>Placed</dt>
        <dd>{{ when }}</dd>
      }
    </dl>

    <ul class="lines" aria-label="Order lines">
      @for (l of order().lines; track l.seatId) {
        @let row = line(l);
        <li class="line">
          <span class="line__name">{{ row.name }}</span>
          <span class="price line__total">{{ row.total }}</span>
          <span class="subtle line__qty">{{ l.quantity }} × {{ row.unit }}</span>
        </li>
      }
      <li class="lines__total">
        <span>Total ({{ order().currency }})</span>
        <span class="price price--total" data-testid="order-total">{{ totalText() }}</span>
      </li>
    </ul>
  `,
  styles: `
    .countdown-inline {
      color: var(--primary);
      font-variant-numeric: tabular-nums;
      font-weight: var(--weight-bold);
    }
    .lines {
      display: grid;
      gap: var(--space-3);
      margin: var(--space-3) 0 0;
      padding: var(--space-3) var(--space-4);
      border: 1px solid var(--border);
      border-radius: var(--radius-md);
      background: var(--surface-muted);
      list-style: none;
    }
    .line {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: 0 var(--space-3);
      font-size: var(--font-size-sm);
    }
    .line__name {
      font-weight: var(--weight-medium);
      overflow-wrap: anywhere;
    }
    .line__total {
      text-align: right;
    }
    .lines__total {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: var(--space-3);
      padding-top: var(--space-3);
      border-top: 1px solid var(--border);
      font-weight: var(--weight-bold);
    }
  `,
})
export class OrderSummary {
  readonly order = input.required<Order>();
  readonly seatNames = input<ReadonlyMap<string, string>>(new Map());
  readonly remaining = input<string | null>(null);

  protected readonly shortId = computed(() => this.order().orderId.slice(0, 8).toUpperCase());
  protected readonly totalText = computed(() =>
    formatMoney(this.order().totalAmount, this.order().currency),
  );
  protected readonly placed = computed(() =>
    isValidDate(this.order().createdAtUtc) ? formatDateTime(this.order().createdAtUtc) : '',
  );
  /** True unless the order's payment went through a real provider. */
  protected readonly simulated = computed(() => this.order().payment?.isSimulated ?? true);
  protected readonly attemptLabel = computed(() => {
    const a = this.order().payment;
    if (!a) {
      return null;
    }
    const label = `#${a.attemptNumber} ${statusView('payment', a.status, a.isSimulated).label}`;
    // A real provider is named; the simulator's label already says what it is.
    return a.isSimulated ? label : `${label} · ${a.provider}`;
  });

  protected line(l: OrderLine): { name: string; unit: string; total: string } {
    const currency = this.order().currency;
    return {
      name: this.seatNames().get(l.seatId) ?? `Seat ${l.seatId.slice(0, 8)}`,
      unit: formatMoney(l.unitPrice, currency),
      total: formatMoney(l.lineTotal, currency),
    };
  }
}

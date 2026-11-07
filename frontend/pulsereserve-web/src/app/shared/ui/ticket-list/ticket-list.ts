import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Ticket } from '../../../core/api/order-api.models';
import { StatusBadge } from '../status-badge/status-badge';

/** The tickets of a paid order. The public id is shown shortened by default, in full on the order page. */
@Component({
  selector: 'app-ticket-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusBadge],
  template: `
    <ul class="stack ticket-list">
      @for (t of tickets(); track t.ticketId) {
        <li class="ticket">
          <span class="ticket__seat">{{
            seatNames().get(t.seatId) ?? 'Seat ' + t.seatId.slice(0, 8)
          }}</span>
          <app-status-badge kind="ticket" [value]="t.status" />
          <code class="ticket__code" data-testid="ticket-id">{{
            full() ? t.ticketId : t.ticketId.slice(0, 12) + '…'
          }}</code>
        </li>
      }
    </ul>
  `,
  styles: `
    .ticket-list {
      margin: 0;
      padding: 0;
      list-style: none;
    }
  `,
})
export class TicketList {
  readonly tickets = input.required<readonly Ticket[]>();
  readonly seatNames = input<ReadonlyMap<string, string>>(new Map());
  readonly full = input(false);
}

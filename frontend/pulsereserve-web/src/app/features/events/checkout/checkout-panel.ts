import { Component, computed, effect, inject, input, isDevMode, untracked } from '@angular/core';
import { Alert } from '../../../shared/ui/alert/alert';
import { OrderSummary } from '../../../shared/ui/order-summary/order-summary';
import { TicketList } from '../../../shared/ui/ticket-list/ticket-list';
import { BookingFlow } from '../booking/booking-flow';
import { CheckoutFlow } from '../booking/checkout-flow';

/**
 * Order summary and payment controls for a reservation in checkout. All amounts are the server's; this component
 * never computes a total. Payment methods are only those the server lists for this order (an empty list is shown as
 * "payments unavailable", never as a fake button). A real provider's hosted page is reached only through the URL the
 * server returned, and coming back from it changes nothing here: the order's status comes from the server.
 * The simulator controls and its banner appear only for the simulator.
 */
@Component({
  selector: 'app-checkout-panel',
  imports: [Alert, OrderSummary, TicketList],
  templateUrl: './checkout-panel.html',
  styleUrl: './checkout-panel.scss',
})
export class CheckoutPanel {
  readonly reservationId = input.required<string>();
  readonly seatNames = input.required<ReadonlyMap<string, string>>();
  readonly remaining = input<string | null>(null);

  protected readonly flow = inject(CheckoutFlow);
  private readonly booking = inject(BookingFlow);

  protected readonly devBuild = isDevMode();
  protected readonly order = this.flow.order;
  /** The banner belongs to the simulator only: a real provider is never described as a simulation. */
  protected readonly showSimulatorBanner = computed(() => {
    const attempt = this.flow.attempt();
    return attempt && attempt.status === 'Initiated'
      ? attempt.isSimulated
      : this.flow.selectedOption()?.isSimulated === true || attempt?.isSimulated === true;
  });
  protected readonly payLabel = computed(() =>
    this.flow.selectedOption()?.isSimulated ? 'Pay now (simulator)' : 'Continue to payment',
  );

  constructor() {
    // Landing on a reservation that is already in Checkout (refresh, interrupted checkout): fetch its order once.
    effect(() => {
      const id = this.reservationId();
      untracked(() => {
        if (!this.flow.order() && !this.flow.loaded() && !this.flow.isBusy()) {
          this.flow.load(id);
        }
      });
    });

    // The hold's countdown ran out: ask the server what became of the order.
    effect(() => {
      if (this.booking.phase() === 'expired') {
        untracked(() => this.flow.refresh());
      }
    });
  }
}

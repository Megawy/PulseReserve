import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { Observable, Subscription } from 'rxjs';
import { ApiError, toApiError } from '../../../core/api/api-error';
import { OrderApiClient } from '../../../core/api/order-api.client';
import { Redirector } from '../../../core/browser/redirector';
import { createLiveRefresh } from '../../../core/live/live-refresh';
import {
  Order,
  OrderResult,
  PaymentOption,
  PaymentOptions,
  SimulatedOutcome,
  Ticket,
} from '../../../core/api/order-api.models';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { BookingFlow, Notice } from './booking-flow';

export type CheckoutBusy = 'idle' | 'loading' | 'creating' | 'paying' | 'completing' | 'cancelling';

/** Where the list of payment methods stands. */
export type OptionsStatus = 'idle' | 'loading' | 'ready' | 'error';

/**
 * Checkout state for one event page (provided next to BookingFlow). The server is the only authority: every amount
 * shown comes from the order it returned, and an order or payment exists in this state only after the API said so.
 *
 * Idempotency: creating an order and starting a payment each use a generated `Idempotency-Key`. After an ambiguous
 * outcome (network error, timeout, 5xx) the SAME key is reused for the retry, so the server replays the original
 * result instead of creating a second order or attempt; a definite outcome (success or a 4xx answer) discards the
 * key. The in-flight guard below only avoids needless requests: the server's uniqueness rules are what make a
 * duplicate harmless.
 */
@Injectable()
export class CheckoutFlow {
  private readonly api = inject(OrderApiClient);
  private readonly booking = inject(BookingFlow);
  private readonly destroyRef = inject(DestroyRef);
  private readonly toasts = inject(ToastService);
  private readonly redirector = inject(Redirector);

  private sub: Subscription | null = null;
  private ticketsSub: Subscription | null = null;
  private optionsSub: Subscription | null = null;
  private liveSub: Subscription | null = null;
  private optionsFor: string | null = null;
  private orderKey: string | null = null;
  private payKey: string | null = null;
  /** A live hint arrived while a request of ours was in flight; read again once it has finished. */
  private refreshQueued = false;

  /**
   * A hint that THIS order changed (status, payment attempt, tickets) re-reads it from the API; the hint is never shown as the new state.
   * This is how a verdict that arrives by webhook, a cancellation from another tab, or the tickets being issued reach the page.
   */
  private readonly liveRefresh = createLiveRefresh({
    matches: (n) => n.kind === 'order' && n.orderId === this.order()?.orderId,
    refresh: () => this.refreshFromLive(),
  });

  readonly order = signal<Order | null>(null);
  readonly busy = signal<CheckoutBusy>('idle');
  readonly notice = signal<Notice | null>(null);
  /** True once the lookup for an existing order finished (so "no order yet" can be told from "not asked yet"). */
  readonly loaded = signal(false);
  /** Tickets of a paid order. They are issued asynchronously, so an empty list right after payment is normal. */
  readonly tickets = signal<readonly Ticket[]>([]);
  readonly ticketsLoading = signal(false);

  /** The payment methods the server offers for this order (never configured or guessed client-side). */
  readonly options = signal<PaymentOptions | null>(null);
  readonly optionsStatus = signal<OptionsStatus>('idle');
  readonly selectedProvider = signal<string | null>(null);
  readonly providers = computed<readonly PaymentOption[]>(() => this.options()?.providers ?? []);
  /** More than one method: the customer must choose; a single one is preselected but still shown. */
  readonly mustChoose = computed(() => this.providers().length > 1);
  readonly selectedOption = computed(
    () => this.providers().find((p) => p.provider === this.selectedProvider()) ?? null,
  );
  /**
   * Why the server says this ORDER cannot be paid (null while it can, or while the answer is not known). `NoPaymentMethod` is
   * not about the order: the order is payable but no enabled provider can charge its currency, so it is reported by `noProvider`
   * (with its own wording) and never as "no longer awaiting payment".
   */
  readonly ineligibleReason = computed(() => {
    const options = this.options();
    if (this.optionsStatus() !== 'ready' || options?.eligible !== false) {
      return null;
    }
    return options.reason === 'NoPaymentMethod' ? null : (options.reason ?? 'OrderNotPayable');
  });
  /** A payment of this order needs the team's attention: it can never be paid again, whatever the provider. */
  readonly heldForReview = computed(() => this.ineligibleReason() === 'OrderHeldForReview');
  /** Ready and empty although the order is payable: no provider can take this order's payment, so no payment button is offered. */
  readonly noProvider = computed(
    () =>
      this.optionsStatus() === 'ready' &&
      this.ineligibleReason() === null &&
      this.providers().length === 0,
  );
  /** A real provider's attempt is in flight and carries its hosted page. */
  readonly awaitingProvider = computed(() => {
    const a = this.attempt();
    return a?.status === 'Initiated' && !a.isSimulated && !!a.checkoutUrl;
  });
  readonly isBusy = computed(() => this.busy() !== 'idle');
  readonly attempt = computed(() => this.order()?.payment ?? null);
  readonly paymentInFlight = computed(() => this.attempt()?.status === 'Initiated');
  readonly canPay = computed(
    () =>
      this.order()?.status === 'PendingPayment' &&
      !this.paymentInFlight() &&
      !this.isBusy() &&
      this.optionsStatus() === 'ready' &&
      this.ineligibleReason() === null &&
      this.selectedOption() !== null,
  );
  readonly canCancel = computed(() => this.order()?.status === 'PendingPayment' && !this.isBusy());
  /** The simulator controls are only offered for a simulated, in-flight attempt that carries its token. */
  readonly simulatorReady = computed(() => {
    const a = this.attempt();
    return (
      this.order()?.status === 'PendingPayment' &&
      a?.status === 'Initiated' &&
      a.isSimulated &&
      !!a.simulatorToken
    );
  });

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.sub?.unsubscribe();
      this.ticketsSub?.unsubscribe();
      this.optionsSub?.unsubscribe();
      this.liveSub?.unsubscribe();
    });
  }

  /** Looks for the order of a reservation already in checkout (page refresh, interrupted checkout). */
  load(reservationId: string): void {
    this.run('loading', this.api.getOrderByReservation(reservationId), {
      next: (order) => this.setOrder(order),
      error: (error) => {
        this.loaded.set(true);
        if (error.status !== 404) {
          this.fail(error);
        }
      },
    });
  }

  /** Starts (or resumes) checkout for the held reservation. */
  createOrder(reservationId: string): void {
    if (this.isBusy()) {
      return;
    }
    this.orderKey ??= crypto.randomUUID();
    const key = this.orderKey;
    this.notice.set(null);
    this.run('creating', this.api.createOrder(reservationId, key), {
      next: (result: OrderResult) => {
        this.orderKey = null;
        this.setOrder(result.order);
        this.booking.recheckReservation(); // the reservation is now in Checkout
      },
      error: (error) => this.onMutationError(error, () => (this.orderKey = null)),
    });
  }

  startPayment(): void {
    const order = this.order();
    if (!order || !this.canPay()) {
      return;
    }
    this.payKey ??= crypto.randomUUID();
    const key = this.payKey;
    this.notice.set(null);
    const provider = this.selectedOption()?.provider;
    this.run('paying', this.api.startPayment(order.orderId, key, provider), {
      next: (result) => {
        this.payKey = null;
        this.setOrder(result.order);
        // A real provider answered with its hosted page: that click was the intent to pay, so go there now. If the
        // browser blocks the navigation, the "Continue to payment" button remains.
        if (this.awaitingProvider()) {
          this.continueToPayment();
        }
      },
      error: (error) => this.onMutationError(error, () => (this.payKey = null)),
    });
  }

  /** Chooses one of the offered methods. Anything the server did not list is ignored. */
  selectProvider(provider: string): void {
    if (this.providers().some((p) => p.provider === provider)) {
      this.selectedProvider.set(provider);
    }
  }

  /** Sends the browser to the provider's hosted page for the attempt in flight (the URL comes only from the server). */
  continueToPayment(): void {
    const attempt = this.attempt();
    if (!attempt || attempt.status !== 'Initiated' || attempt.isSimulated) {
      return;
    }
    if (this.redirector.toProvider(attempt.checkoutUrl)) {
      this.notice.set({
        tone: 'info',
        text: `Taking you to ${attempt.provider} to complete the payment. Your order is confirmed here only once the provider confirms it.`,
      });
    } else {
      this.notice.set({
        tone: 'error',
        text: 'The payment page could not be opened. Try again, or check the payment status.',
      });
    }
  }

  /** Reads the available payment methods for the current order (retryable after an error). */
  loadOptions(): void {
    const order = this.order();
    if (!order) {
      return;
    }
    this.optionsSub?.unsubscribe();
    this.optionsFor = order.orderId;
    this.optionsStatus.set('loading');
    this.optionsSub = this.api.paymentOptions(order.orderId).subscribe({
      next: (options) => {
        this.options.set(options);
        this.optionsStatus.set('ready');
        const current = this.selectedProvider();
        const stillOffered = options.providers.some((p) => p.provider === current);
        this.selectedProvider.set(
          stillOffered
            ? current
            : options.providers.length === 1
              ? options.providers[0].provider
              : null,
        );
      },
      error: () => {
        this.options.set(null);
        this.optionsStatus.set('error');
      },
    });
  }

  /** DEVELOPMENT/TEST ONLY: tells the simulator to succeed or fail the in-flight attempt. */
  simulate(outcome: SimulatedOutcome): void {
    const order = this.order();
    const attempt = this.attempt();
    if (!order || !attempt?.simulatorToken || this.isBusy()) {
      return;
    }
    this.notice.set(null);
    this.run(
      'completing',
      this.api.completeSimulatedPayment(
        order.orderId,
        attempt.attemptId,
        attempt.simulatorToken,
        outcome,
      ),
      {
        next: (updated) => {
          this.setOrder(updated);
          this.afterOrderSettled(updated);
        },
        error: (error) => this.onMutationError(error),
      },
    );
  }

  cancel(): void {
    const order = this.order();
    if (!order || !this.canCancel()) {
      return;
    }
    this.notice.set(null);
    this.run('cancelling', this.api.cancelOrder(order.orderId), {
      next: (updated) => {
        this.setOrder(updated);
        this.afterOrderSettled(updated);
        this.toasts.show('Order cancelled. Your seats were released.');
        this.notice.set({
          tone: 'info',
          text: 'Your order was cancelled and the seats were released.',
        });
      },
      error: (error) => this.onMutationError(error),
    });
  }

  /** Re-reads the order (e.g. when the hold's countdown ran out, or after an unclear outcome). */
  refresh(): void {
    const order = this.order();
    if (!order || this.isBusy()) {
      return;
    }
    this.run('loading', this.api.getOrder(order.orderId), {
      next: (updated) => {
        this.setOrder(updated);
        if (updated.status !== 'PendingPayment') {
          this.afterOrderSettled(updated);
        }
      },
      error: (error) => this.fail(error),
    });
  }

  /** Reads the order after a live hint, without any busy state (the buttons do not flicker). Skipped while our own request runs. */
  private refreshFromLive(): void {
    const order = this.order();
    if (!order) {
      return;
    }
    if (this.isBusy()) {
      this.refreshQueued = true;
      return;
    }
    this.liveRefresh.markRequested();
    this.liveSub?.unsubscribe();
    this.liveSub = this.api.getOrder(order.orderId).subscribe({
      // A failed read changes nothing on screen: the next hint, the next action or "Refresh" reads again.
      next: (updated) => this.applyFresh(updated),
      error: () => undefined,
    });
  }

  private applyFresh(updated: Order): void {
    const current = this.order();
    const changed =
      !current ||
      current.status !== updated.status ||
      current.payment?.status !== updated.payment?.status ||
      current.payment?.attemptId !== updated.payment?.attemptId;
    if (!changed) {
      this.order.set(updated);
      if (updated.status === 'Paid' && this.tickets().length === 0) {
        this.loadTickets(); // the tickets are issued after the payment; a hint about them lands here
      }
      return;
    }
    this.setOrder(updated);
    if (updated.status !== 'PendingPayment') {
      this.afterOrderSettled(updated);
    }
  }

  private flushQueuedRefresh(): void {
    if (this.refreshQueued) {
      this.refreshQueued = false;
      this.refreshFromLive();
    }
  }

  /** Reads the ticket list; the outbox issues tickets a moment after payment, so this can be repeated. */
  loadTickets(): void {
    const order = this.order();
    if (!order || order.status !== 'Paid') {
      return;
    }
    this.ticketsSub?.unsubscribe();
    this.ticketsLoading.set(true);
    this.ticketsSub = this.api.getTickets(order.orderId).subscribe({
      next: (tickets) => {
        this.ticketsLoading.set(false);
        this.tickets.set(tickets);
      },
      error: () => this.ticketsLoading.set(false), // best effort: the purchase itself is already confirmed
    });
  }

  reset(): void {
    this.options.set(null);
    this.optionsStatus.set('idle');
    this.selectedProvider.set(null);
    this.optionsFor = null;
    this.tickets.set([]);
    this.order.set(null);
    this.loaded.set(false);
    this.notice.set(null);
    this.orderKey = null;
    this.payKey = null;
  }

  private setOrder(order: Order): void {
    this.order.set(order);
    this.loaded.set(true);
    // The payment methods are needed whenever a new attempt may be started (first time, or after a failed attempt).
    const attempt = order.payment;
    if (
      order.status === 'PendingPayment' &&
      attempt?.status !== 'Initiated' &&
      this.optionsFor !== order.orderId
    ) {
      this.loadOptions();
    }
    // Real money is never described as simulated, and the simulator is never described as real.
    const simulated = attempt?.isSimulated ?? true;
    if (order.status === 'Paid') {
      this.notice.set({
        tone: 'success',
        text: simulated
          ? 'Payment confirmed (simulated). Your seats are yours.'
          : 'Payment confirmed. Your seats are yours.',
      });
      this.toasts.success(simulated ? 'Payment confirmed (simulated).' : 'Payment confirmed.');
      this.loadTickets();
    } else if (order.status === 'RefundRequired') {
      this.notice.set({
        tone: 'warning',
        text: simulated
          ? 'The payment arrived after the hold expired, so the seats could not be sold. A refund is required (simulated: no money moved).'
          : 'The payment arrived after the hold expired, so the seats could not be sold. A refund is required and will be processed once the payment provider confirms it.',
      });
    } else if (order.status === 'Refunded') {
      this.notice.set({
        tone: 'info',
        text: simulated
          ? 'Your payment was refunded (simulated: no money moved).'
          : 'Your payment was refunded.',
      });
    } else if (order.status === 'Expired') {
      this.notice.set({
        tone: 'warning',
        text: 'The payment window ended and the seats were released.',
      });
    }
  }

  private afterOrderSettled(order: Order): void {
    if (order.status !== 'PendingPayment') {
      this.booking.recheckReservation();
      this.booking.refreshAvailability();
    }
  }

  private onMutationError(error: ApiError, discardKey?: () => void): void {
    // The provider did not answer: the attempt exists, so the SAME key must be reused (a new key would be refused as
    // "an attempt is in progress"), and nothing may be assumed about what happened on the provider's side.
    const providerSilent = error.code === 'Payment.ProviderTemporarilyUnavailable';
    const ambiguous =
      providerSilent || error.kind === 'network' || error.kind === 'timeout' || error.status >= 500;
    if (ambiguous && (error.status !== 503 || providerSilent)) {
      this.notice.set({
        tone: 'warning',
        text: `${error.message} We could not confirm whether the request went through. Trying again repeats the same request safely.`,
      });
      return;
    }
    discardKey?.();
    this.fail(error);
    // A conflict usually means the world moved on (expired, cancelled, already paid): show the truth.
    if (error.status === 409) {
      this.refresh();
    }
  }

  private fail(error: ApiError): void {
    this.notice.set({
      tone: error.status === 409 ? 'warning' : 'error',
      text: describeError(error),
    });
  }

  private run<T>(
    busy: Exclude<CheckoutBusy, 'idle'>,
    request: Observable<T>,
    handlers: { next: (value: T) => void; error: (error: ApiError) => void },
  ): void {
    this.sub?.unsubscribe();
    this.busy.set(busy);
    this.liveRefresh.markRequested();
    this.sub = request.subscribe({
      next: (value) => {
        this.busy.set('idle');
        handlers.next(value);
        this.flushQueuedRefresh();
      },
      error: (error: unknown) => {
        this.busy.set('idle');
        handlers.error(toApiError(error));
        this.flushQueuedRefresh();
      },
    });
  }
}

export function describeError(error: ApiError): string {
  switch (error.code) {
    case 'Payment.ProviderRequired':
      return 'Choose a payment method to continue.';
    case 'Payment.ProviderNotEnabled':
    case 'Payment.CurrencyNotSupported':
      return error.message;
    case 'Payment.ProviderMismatch':
      return 'A payment with another method is already in progress for this order. Finish or cancel it first.';
    case 'Payment.ProviderRejected':
      return 'The payment provider could not start this payment. Nothing was charged. Try another method or try again later.';
    case 'Payment.AttemptInProgress':
      return 'A payment for this order is already in progress. Use "Continue to payment" or check its status.';
    case 'Payment.OrderHeldForReview':
      return 'This order can no longer be paid: an earlier payment for it needs attention from our team. Cancel this order and start a new one.';
    case 'Payment.InitiationUncertain':
      return 'We could not confirm that the payment page was created, so this attempt was closed. Nothing has been charged. Start the payment again.';
  }
  switch (error.status) {
    case 401:
      return 'Your session has expired. Sign in again to continue.';
    case 403:
      return 'The request could not be verified. Reload the page and try again.';
    case 503:
      return 'Payments are not available in this environment.';
    default:
      return error.message;
  }
}

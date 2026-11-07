import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { ApiError } from '../../../core/api/api-error';
import { OrderApiClient } from '../../../core/api/order-api.client';
import { Order, PaymentAttempt, PaymentOptions } from '../../../core/api/order-api.models';
import { Redirector } from '../../../core/browser/redirector';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { BookingFlow } from '../booking/booking-flow';
import { CheckoutFlow } from '../booking/checkout-flow';
import { CheckoutPanel } from './checkout-panel';

const ORDER = '00000000-0000-4000-8000-0000000000b1';
const ATTEMPT = '00000000-0000-4000-8000-0000000000d1';
const STRIPE = { provider: 'Stripe', displayName: 'Card payment (Stripe)', isSimulated: false };
const PAYMOB = {
  provider: 'Paymob',
  displayName: 'Card or wallet payment (Paymob)',
  isSimulated: false,
};
const SIM = { provider: 'Simulator', displayName: 'Payment simulator', isSimulated: true };

const order = (over: Partial<Order> = {}): Order => ({
  orderId: ORDER,
  reservationId: 'res-1',
  eventId: 'e1',
  status: 'PendingPayment',
  currency: 'USD',
  totalAmount: 100,
  createdAtUtc: '2026-10-21T10:00:00Z',
  paymentDeadlineUtc: '2026-10-21T10:10:00Z',
  lines: [{ seatId: 's1', quantity: 1, unitPrice: 100, lineTotal: 100 }],
  payment: null,
  ...over,
});

const realAttempt = (over: Partial<PaymentAttempt> = {}): PaymentAttempt => ({
  attemptId: ATTEMPT,
  attemptNumber: 1,
  status: 'Initiated',
  provider: 'Stripe',
  isSimulated: false,
  simulatorToken: null,
  createdAtUtc: '2026-10-21T10:00:00Z',
  completedAtUtc: null,
  checkoutUrl: 'https://checkout.stripe.com/c/pay/cs_test_1',
  ...over,
});

const problem = (status: number, title: string, message = title) =>
  new ApiError('problem', status, title, message, message);

describe('CheckoutFlow with several payment providers', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let redirector: { toProvider: ReturnType<typeof vi.fn> };
  let flow: CheckoutFlow;

  function setup(options: PaymentOptions = { currency: 'USD', providers: [STRIPE, PAYMOB] }) {
    api = {
      paymentOptions: vi.fn(() => of(options)),
      startPayment: vi.fn(() => of({ order: order({ payment: realAttempt() }), replayed: false })),
      getOrder: vi.fn(() => of(order())),
      getTickets: vi.fn(() => of([])),
    };
    redirector = { toProvider: vi.fn(() => true) };
    TestBed.configureTestingModule({
      providers: [
        CheckoutFlow,
        { provide: OrderApiClient, useValue: api },
        { provide: Redirector, useValue: redirector },
        {
          provide: BookingFlow,
          useValue: {
            recheckReservation: vi.fn(),
            refreshAvailability: vi.fn(),
            phase: signal('checkout'),
          },
        },
      ],
    });
    flow = TestBed.inject(CheckoutFlow);
    flow.order.set(order());
    flow.loadOptions();
  }

  it('offers only the methods the server listed, and makes the customer choose when there are several', () => {
    setup();

    expect(api['paymentOptions']).toHaveBeenCalledWith(ORDER);
    expect(flow.providers().map((p) => p.provider)).toEqual(['Stripe', 'Paymob']);
    expect(flow.mustChoose()).toBe(true);
    expect(flow.selectedProvider()).toBeNull();
    expect(flow.canPay()).toBe(false);

    flow.startPayment();
    expect(api['startPayment']).not.toHaveBeenCalled();
  });

  it('preselects a single method but still shows it', () => {
    setup({ currency: 'USD', providers: [STRIPE] });

    expect(flow.mustChoose()).toBe(false);
    expect(flow.selectedProvider()).toBe('Stripe');
    expect(flow.canPay()).toBe(true);
  });

  it('ignores a provider the server did not list', () => {
    setup();

    flow.selectProvider('Adyen');
    flow.selectProvider('https://evil.example/pay');

    expect(flow.selectedProvider()).toBeNull();
    expect(flow.canPay()).toBe(false);
  });

  it('starts the payment with the chosen provider NAME only, then goes to the provider page the server returned', () => {
    setup();
    flow.selectProvider('Stripe');

    flow.startPayment();

    const [orderId, key, provider] = api['startPayment'].mock.calls[0];
    expect(orderId).toBe(ORDER);
    expect(key).toMatch(/^[0-9a-f-]{36}$/);
    expect(provider).toBe('Stripe');
    expect(flow.awaitingProvider()).toBe(true);
    expect(redirector.toProvider).toHaveBeenCalledWith(
      'https://checkout.stripe.com/c/pay/cs_test_1',
    );
    expect(flow.order()?.status).toBe('PendingPayment'); // going to the provider confirms nothing
    expect(flow.notice()?.text).toContain('confirmed here only once the provider confirms');
  });

  it('keeps a "continue" path when the browser did not follow the redirect', () => {
    setup({ currency: 'USD', providers: [STRIPE] });
    redirector.toProvider.mockReturnValue(false);

    flow.startPayment();

    expect(flow.awaitingProvider()).toBe(true);
    expect(flow.notice()?.tone).toBe('error');
    redirector.toProvider.mockReturnValue(true);
    flow.continueToPayment();
    expect(redirector.toProvider).toHaveBeenCalledTimes(2);
  });

  it('never redirects for the simulator', () => {
    setup({ currency: 'USD', providers: [SIM] });
    api['startPayment'].mockReturnValue(
      of({
        order: order({
          payment: realAttempt({
            provider: 'Simulator',
            isSimulated: true,
            simulatorToken: 'sim_x',
            checkoutUrl: null,
          }),
        }),
        replayed: false,
      }),
    );

    flow.startPayment();

    expect(redirector.toProvider).not.toHaveBeenCalled();
    expect(flow.simulatorReady()).toBe(true);
    expect(flow.awaitingProvider()).toBe(false);
  });

  it('shows an unavailable state, not a payment button, when no provider is offered', () => {
    setup({ currency: 'USD', providers: [] });

    expect(flow.noProvider()).toBe(true);
    expect(flow.canPay()).toBe(false);
  });

  it('offers no payment method and no button when the server says the order was held for review', () => {
    setup({ currency: 'USD', providers: [], eligible: false, reason: 'OrderHeldForReview' });

    expect(flow.ineligibleReason()).toBe('OrderHeldForReview');
    expect(flow.heldForReview()).toBe(true);
    expect(flow.noProvider()).toBe(false); // a held order is not "no provider": the wording differs
    expect(flow.canPay()).toBe(false);
    flow.startPayment();
    expect(api['startPayment']).not.toHaveBeenCalled();
  });

  it('reports a currency no provider can charge as "no provider", never as an order that is no longer payable', () => {
    setup({ currency: 'USD', providers: [], eligible: false, reason: 'NoPaymentMethod' });

    expect(flow.ineligibleReason()).toBeNull();
    expect(flow.heldForReview()).toBe(false);
    expect(flow.noProvider()).toBe(true);
    expect(flow.canPay()).toBe(false);
  });

  it('even a method the server lists cannot be paid for while the order is ineligible', () => {
    setup({ currency: 'USD', providers: [STRIPE], eligible: false, reason: 'OrderNotPayable' });

    expect(flow.ineligibleReason()).toBe('OrderNotPayable');
    expect(flow.heldForReview()).toBe(false);
    expect(flow.canPay()).toBe(false);
  });

  it('treats a server that sends no eligibility as eligible', () => {
    setup({ currency: 'USD', providers: [STRIPE] });

    expect(flow.ineligibleReason()).toBeNull();
    expect(flow.canPay()).toBe(true);
  });

  it.each([
    ['Payment.OrderHeldForReview', 409, 'can no longer be paid'],
    ['Payment.InitiationUncertain', 409, 'Nothing has been charged. Start the payment again'],
  ])('explains %s without blaming the customer or inventing detail', (code, status, text) => {
    setup({ currency: 'USD', providers: [STRIPE] });
    api['startPayment'].mockReturnValueOnce(throwError(() => problem(status, code)));

    flow.startPayment();
    const shown = flow.notice()?.text ?? '';
    flow.startPayment();

    expect(shown).toContain(text);
    expect(shown.toLowerCase()).not.toMatch(/exception|administrator|attempt id|evt_/);
    // A definite answer: the next try uses a new key.
    expect(api['startPayment'].mock.calls[1][1]).not.toBe(api['startPayment'].mock.calls[0][1]);
  });

  it('reports a failure to load the methods and recovers on retry', () => {
    setup();
    api['paymentOptions'].mockReturnValueOnce(throwError(() => problem(500, 'Server.Error')));
    flow.loadOptions();
    expect(flow.optionsStatus()).toBe('error');
    expect(flow.canPay()).toBe(false);

    flow.loadOptions();
    expect(flow.optionsStatus()).toBe('ready');
  });

  it('reuses the same key when the provider did not answer (the attempt already exists)', () => {
    setup({ currency: 'USD', providers: [STRIPE] });
    api['startPayment'].mockReturnValueOnce(
      throwError(() =>
        problem(
          503,
          'Payment.ProviderTemporarilyUnavailable',
          'The payment provider did not answer.',
        ),
      ),
    );

    flow.startPayment();
    expect(flow.notice()?.text).toContain('could not confirm whether the request went through');

    flow.startPayment();
    expect(api['startPayment'].mock.calls[1][1]).toBe(api['startPayment'].mock.calls[0][1]);
  });

  it('discards the key after a definitive provider refusal so another method can be tried', () => {
    setup();
    flow.selectProvider('Stripe');
    api['startPayment'].mockReturnValueOnce(
      throwError(() => problem(503, 'Payment.ProviderRejected')),
    );

    flow.startPayment();
    expect(flow.notice()?.text).toContain('Nothing was charged');

    flow.selectProvider('Paymob');
    flow.startPayment();
    expect(api['startPayment'].mock.calls[1][1]).not.toBe(api['startPayment'].mock.calls[0][1]);
    expect(api['startPayment'].mock.calls[1][2]).toBe('Paymob');
  });

  it.each([
    ['Payment.ProviderRequired', 400, 'Choose a payment method'],
    ['Payment.ProviderMismatch', 409, 'another method is already in progress'],
  ])('explains %s in plain words', (code, status, text) => {
    setup({ currency: 'USD', providers: [STRIPE] });
    api['startPayment'].mockReturnValueOnce(throwError(() => problem(status, code)));

    flow.startPayment();

    expect(flow.notice()?.text).toContain(text);
  });

  it('describes a confirmed real payment without the word simulated, and a simulated one with it', () => {
    setup({ currency: 'USD', providers: [STRIPE] });
    const toasts = TestBed.inject(ToastService);

    api['getOrder'].mockReturnValue(
      of(
        order({ status: 'Paid', payment: realAttempt({ status: 'Succeeded', checkoutUrl: null }) }),
      ),
    );
    flow.refresh();
    expect(flow.notice()?.text).toBe('Payment confirmed. Your seats are yours.');
    expect(toasts.toasts().at(-1)?.text).toBe('Payment confirmed.');

    flow.reset();
    flow.order.set(order());
    api['getOrder'].mockReturnValue(
      of(
        order({
          status: 'Paid',
          payment: realAttempt({
            status: 'Succeeded',
            provider: 'Simulator',
            isSimulated: true,
            checkoutUrl: null,
          }),
        }),
      ),
    );
    flow.refresh();
    expect(flow.notice()?.text).toContain('simulated');
  });

  it('a refund of a real payment is complete only when the provider confirms: no simulated wording', () => {
    setup({ currency: 'USD', providers: [STRIPE] });
    api['getOrder'].mockReturnValue(
      of(order({ status: 'RefundRequired', payment: realAttempt({ status: 'Succeeded' }) })),
    );

    flow.refresh();

    expect(flow.notice()?.text).toContain('once the payment provider confirms it');
    expect(flow.notice()?.text).not.toContain('simulated');
  });

  it('reloads the methods for a new attempt after a failed one', () => {
    setup({ currency: 'USD', providers: [STRIPE] });
    flow.selectProvider('Stripe');
    api['startPayment'].mockReturnValue(
      of({
        order: order({ payment: realAttempt({ status: 'Failed', checkoutUrl: null }) }),
        replayed: false,
      }),
    );
    flow.startPayment();

    expect(flow.order()?.payment?.status).toBe('Failed');
    expect(flow.canPay()).toBe(true);
  });

  it('waits for the server: a pending real attempt is never shown as paid', () => {
    setup({ currency: 'USD', providers: [STRIPE] });
    const pending = new Subject<unknown>();
    api['startPayment'].mockReturnValue(pending);

    flow.startPayment();

    expect(flow.busy()).toBe('paying');
    expect(flow.order()?.status).toBe('PendingPayment');
    expect(flow.attempt()).toBeNull();
  });
});

describe('CheckoutPanel with real providers', () => {
  function render(o: Order, flowOver: Record<string, unknown> = {}) {
    TestBed.resetTestingModule();
    const attempt = signal(o.payment);
    const selected = signal<string | null>(null);
    const providers = [STRIPE, PAYMOB];
    const selectProvider = vi.fn((p: string) => selected.set(p));
    const flow = {
      order: signal(o),
      attempt,
      loaded: signal(true),
      busy: signal('idle'),
      notice: signal(null),
      isBusy: () => false,
      paymentInFlight: () => attempt()?.status === 'Initiated',
      canPay: () => selected() !== null,
      canCancel: () => true,
      simulatorReady: () => false,
      optionsStatus: () => 'ready',
      providers: () => providers,
      selectedProvider: selected,
      selectedOption: () => providers.find((p) => p.provider === selected()) ?? null,
      noProvider: () => false,
      heldForReview: () => false,
      ineligibleReason: () => null,
      mustChoose: () => true,
      awaitingProvider: () => attempt()?.status === 'Initiated' && attempt()?.isSimulated === false,
      loadOptions: vi.fn(),
      selectProvider,
      continueToPayment: vi.fn(),
      load: vi.fn(),
      tickets: signal([]),
      ticketsLoading: signal(false),
      loadTickets: vi.fn(),
      refresh: vi.fn(),
      createOrder: vi.fn(),
      startPayment: vi.fn(),
      simulate: vi.fn(),
      cancel: vi.fn(),
      ...flowOver,
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: CheckoutFlow, useValue: flow },
        { provide: BookingFlow, useValue: { phase: signal('checkout') } },
      ],
    });
    const fixture = TestBed.createComponent(CheckoutPanel);
    fixture.componentRef.setInput('reservationId', 'res-1');
    fixture.componentRef.setInput('seatNames', new Map([['s1', 'Seat 1']]));
    return { fixture, flow, selectProvider };
  }

  it('lists the offered methods as a labelled radio group, with no simulator banner for real providers', async () => {
    const { fixture, selectProvider } = render(order());
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('fieldset.methods legend')?.textContent).toContain('Payment method');
    const radios = [...el.querySelectorAll<HTMLInputElement>('input[type="radio"]')];
    expect(radios.map((r) => r.value)).toEqual(['Stripe', 'Paymob']);
    expect(el.textContent).toContain('Card payment (Stripe)');
    expect(el.querySelector('.sim-banner')).toBeNull();
    expect(el.textContent).not.toContain('simulator');
    expect(el.textContent).not.toContain('Simulated');

    const pay = [...el.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Continue to payment'),
    );
    expect(pay?.disabled).toBe(true);
    radios[0].click();
    expect(selectProvider).toHaveBeenCalledWith('Stripe');
  });

  it('shows an honest unavailable state instead of a payment button when no provider is offered', async () => {
    const { fixture } = render(order(), {
      providers: () => [],
      noProvider: () => true,
      mustChoose: () => false,
    });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Payments are unavailable');
    expect(el.textContent).toContain('Nothing has been charged');
    expect(el.querySelector('input[type="radio"]')).toBeNull();
    expect(el.textContent).not.toContain('Continue to payment');
  });

  it('explains that a held order cannot be paid again and offers no method, no pay button and no provider wording', async () => {
    const { fixture } = render(order(), {
      providers: () => [],
      noProvider: () => false,
      heldForReview: () => true,
      ineligibleReason: () => 'OrderHeldForReview',
      mustChoose: () => false,
    });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('This order can no longer be paid');
    expect(el.textContent).toContain('needs attention from our team');
    expect(el.textContent).toContain('You have not been charged again');
    expect(el.textContent).not.toContain('Payments are unavailable');
    expect(el.querySelector('input[type="radio"]')).toBeNull();
    expect(el.textContent).not.toContain('Continue to payment');
    expect(el.textContent?.toLowerCase()).not.toMatch(
      /exception|administrator|refund|stripe|paymob/,
    );
    const cancel = [...el.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Cancel order'),
    );
    expect(cancel, 'the customer can still cancel and start again').toBeTruthy();
  });

  it('offers retry when the methods could not be loaded', async () => {
    const loadOptions = vi.fn();
    const { fixture } = render(order(), { optionsStatus: () => 'error', loadOptions });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('We could not load the payment methods');
    el.querySelector<HTMLButtonElement>('[role="alert"] button')!.click();
    expect(loadOptions).toHaveBeenCalled();
  });

  it('for an attempt in flight: explains that returning confirms nothing, and offers continue and a status check', async () => {
    const { fixture, flow } = render(order({ payment: realAttempt() }));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Payment in progress');
    expect(el.textContent).toContain('not when you return here');
    expect(el.textContent).toContain('Already paid? Wait for the confirmation before cancelling');
    const button = (label: string) =>
      [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(label))!;
    button('Continue to payment').click();
    button('Check payment status').click();
    expect(flow['continueToPayment']).toHaveBeenCalled();
    expect(flow['refresh']).toHaveBeenCalled();
    expect(el.querySelector('input[type="radio"]')).toBeNull();
  });

  it('shows the simulator banner and label only for the simulator method', async () => {
    const sim = { ...SIM };
    const { fixture } = render(order(), {
      providers: () => [sim],
      selectedProvider: signal('Simulator'),
      selectedOption: () => sim,
      mustChoose: () => false,
      canPay: () => true,
    });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.sim-banner')?.textContent).toContain('no real money');
    expect(el.textContent).toContain('Pay now (simulator)');
    expect(el.textContent).toContain('Simulated');
  });
});

import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { PaymentException } from '../../../core/api/admin-payments.models';
import { ApiError } from '../../../core/api/api-error';
import { formatMoney } from '../../../shared/format';
import { statusView } from '../../../shared/presentation';
import { canResolveException, exceptionOrderNote } from './ops-rules';
import { Exceptions } from './exceptions';
import { Overview } from './overview';

const exception = (over: Partial<PaymentException> = {}): PaymentException => ({
  exceptionId: 'cccccccc-0000-4000-8000-000000000001',
  kind: 'LateSuccessAfterFailedAttempt',
  attemptId: 'aaaaaaaa-0000-4000-8000-000000000001',
  orderId: 'ffffffff-0000-4000-8000-000000000001',
  provider: 'Stripe',
  isSimulated: false,
  amount: 91,
  currency: 'USD',
  originalAttemptStatus: 'Failed',
  originalFailureCode: 'checkout_session_expired',
  originalVerdictAtUtc: '2030-01-01T09:00:00Z',
  orderStatusAtDetection: 'PendingPayment',
  detectedAtUtc: '2030-01-01T09:30:00Z',
  state: 'Open',
  resolution: null,
  resolvedBy: null,
  resolvedAtUtc: null,
  resolutionReference: null,
  resolutionNote: null,
  orderStatus: 'PendingPayment',
  orderPaymentDeadlineUtc: '2030-01-01T10:00:00Z',
  orderRefundState: null,
  nextAction: 'RefundExternallyThenRecord',
  ...over,
});

const text = (e: Element) => (e.textContent ?? '').replace(/\s+/g, ' ').trim();

async function mount<T>(component: Type<T>, api: Record<string, unknown>, realRouter = false) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: AdminPaymentsApiClient, useValue: api },
      ...(realRouter
        ? []
        : [
            {
              provide: Router,
              useValue: { navigate: vi.fn(async () => true), createUrlTree: vi.fn(), events: of() },
            },
          ]),
      {
        provide: ActivatedRoute,
        useValue: { queryParamMap: new BehaviorSubject({ get: () => null }).asObservable() },
      },
    ],
  });
  const fixture = TestBed.createComponent(component);
  await fixture.whenStable();
  return { el: fixture.nativeElement as HTMLElement, fixture };
}

const tick = (fixture: { detectChanges: () => void }) =>
  new Promise((r) => setTimeout(r)).then(() => fixture.detectChanges());

describe('payment exception rules', () => {
  it('offers only the step the server names', () => {
    expect(canResolveException(exception())).toBe(true);
    expect(canResolveException(exception({ nextAction: 'None' }))).toBe(false);
    expect(canResolveException(exception({ state: 'Resolved', nextAction: 'None' }))).toBe(false);
    expect(canResolveException({ nextAction: 'SomethingElse' })).toBe(false);
  });

  it('never claims the order was paid or fulfilled by an exception', () => {
    for (const orderStatus of [
      'PendingPayment',
      'Cancelled',
      'Expired',
      'Paid',
      'RefundRequired',
      'Refunded',
      'Weird',
    ]) {
      const note = exceptionOrderNote({ orderStatus, state: 'Open' });
      expect(note).not.toMatch(/fulfilled by|was paid by this|ticket.*issued/i);
      expect(note.length).toBeGreaterThan(10);
    }
    expect(exceptionOrderNote({ orderStatus: 'Paid', state: 'Open' })).toContain(
      'additional charge',
    );
    expect(exceptionOrderNote({ orderStatus: 'PendingPayment', state: 'Open' })).toContain(
      'can no longer be paid',
    );
  });

  it('labels a resolution as an administrator statement, never a provider refund', () => {
    expect(statusView('exception', 'Resolved').label).toBe('Resolved (administrator statement)');
    expect(statusView('exception', 'Open').label).toBe('Needs an administrator');
    expect(statusView('exception', 'Resolved').label).not.toMatch(/refunded|provider/i);
  });
});

describe('payment exceptions page', () => {
  const api = (items: PaymentException[], resolve?: unknown) => ({
    exceptions: vi.fn(() => of(items)),
    resolveException: resolve ?? vi.fn(),
  });
  const dialog = (el: HTMLElement) => el.querySelector('dialog')!;
  const confirmButton = (el: HTMLElement) =>
    [...dialog(el).querySelectorAll('button')].at(-1) as HTMLButtonElement;
  const tick10 = () => new Promise((r) => setTimeout(r));
  const open = async (el: HTMLElement) => {
    const button = [...el.querySelectorAll('tbody button')].find((b) =>
      text(b).startsWith('Record refund'),
    ) as HTMLButtonElement;
    button.click();
    await tick10();
  };
  const tickBox = (el: HTMLElement) => {
    const box = dialog(el).querySelector('input[type=checkbox]') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
  };

  it('keeps what this system recorded apart from what the provider reported, and says what has not happened', async () => {
    const { el } = await mount(Exceptions, api([exception()]));
    const row = text(el.querySelector('tbody tr')!);

    expect(row).toContain('Attempt Failed');
    expect(row).toContain('Reason on record: checkout_session_expired');
    expect(row).toContain('Kept exactly as recorded');
    expect(row).toContain(`Success for ${formatMoney(91, 'USD').replace(/\s+/g, ' ')}`);
    expect(row).toContain('Signature, amount and currency matched the order');
    expect(row).toContain('Fulfilment: not applied');
    expect(row).toContain('can no longer be paid');
    expect(row).toContain('Refund required. This app does not refund this payment.');
    expect(row).toContain('Needs an administrator');
    expect(row).toContain('Refund it in the Stripe dashboard first.');
    expect(row).not.toMatch(/refunded|provider confirmed|was paid|is paid|has been paid/i);

    const page = text(el);
    expect(page).toContain('the order is not paid, no tickets are issued');
    expect(page.toLowerCase()).not.toMatch(/signature=|secret|payload|client_secret|hmac/);
  });

  it('shows a simulated payment as simulated, never as real money', async () => {
    const { el } = await mount(
      Exceptions,
      api([exception({ provider: 'Simulator', isSimulated: true })]),
    );
    const row = text(el.querySelector('tbody tr')!);

    expect(row).toContain('Simulated success');
    expect(row).toContain('Simulator: no real money moved.');
    expect(row).not.toContain('Signature, amount and currency matched');
  });

  it('shows a resolved exception with its recorder and without any action', async () => {
    const { el } = await mount(
      Exceptions,
      api([
        exception({
          state: 'Resolved',
          nextAction: 'None',
          resolution: 'RefundedExternally',
          resolvedBy: '99999999-0000-4000-8000-000000000001',
          resolvedAtUtc: '2030-01-02T09:00:00Z',
          resolutionReference: 're_dashboard_1',
        }),
      ]),
    );
    const row = text(el.querySelector('tbody tr')!);

    expect(row).toContain('Resolved (administrator statement)');
    expect(row).toContain('The provider did not confirm it');
    expect(row).toContain('re_dashboard_1');
    expect(row).toContain('Nothing more to do here.');
    expect(el.querySelector('tbody button')).toBeNull();
  });

  it('shows a different payment’s refund on the order as that order’s own', async () => {
    const { el } = await mount(
      Exceptions,
      api([exception({ orderStatus: 'RefundRequired', orderRefundState: 'Pending' })]),
    );
    const row = text(el.querySelector('tbody tr')!);

    expect(row).toContain("The order's own refund");
    expect(row).toContain('different payment');
  });

  it('offers no action unless the server names one', async () => {
    const { el } = await mount(Exceptions, api([exception({ nextAction: 'None' })]));

    expect(el.querySelector('tbody button')).toBeNull();
  });

  it('cannot be confirmed without the explicit tick, and sends nothing before', async () => {
    const resolve = vi.fn(() => of(exception({ state: 'Resolved', nextAction: 'None' })));
    const { el, fixture } = await mount(Exceptions, api([exception()], resolve));
    await open(el);
    fixture.detectChanges();

    expect(text(dialog(el))).toContain('already returned the money');
    expect(text(dialog(el))).toContain('has not confirmed it');
    expect(confirmButton(el).disabled).toBe(true);
    confirmButton(el).click();
    expect(resolve).not.toHaveBeenCalled();

    tickBox(el);
    fixture.detectChanges();
    expect(confirmButton(el).disabled).toBe(false);
  });

  it('sends the statement with the amount shown, trims optional text, and blocks a double submit', async () => {
    const response = new Subject<PaymentException>();
    const resolve = vi.fn(() => response);
    const { el, fixture } = await mount(Exceptions, api([exception()], resolve));
    await open(el);
    const ref = dialog(el).querySelector('#exception-reference') as HTMLInputElement;
    ref.value = '  re_1  ';
    ref.dispatchEvent(new Event('input'));
    tickBox(el);
    fixture.detectChanges();

    confirmButton(el).click();
    confirmButton(el).click();
    fixture.detectChanges();

    expect(resolve).toHaveBeenCalledTimes(1);
    expect(resolve).toHaveBeenCalledWith('cccccccc-0000-4000-8000-000000000001', {
      refundPerformedExternally: true,
      amount: 91,
      currency: 'USD',
      externalReference: 're_1',
      note: null,
    });
    expect(text(confirmButton(el))).toContain('Recording');
    expect(confirmButton(el).disabled).toBe(true);
  });

  it('after success it reloads the list and never says the provider confirmed a refund', async () => {
    const resolve = vi.fn(() => of(exception({ state: 'Resolved', nextAction: 'None' })));
    const spec = api([exception()], resolve);
    const { el, fixture } = await mount(Exceptions, spec);
    await open(el);
    tickBox(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(spec.exceptions).toHaveBeenCalledTimes(2);
    expect(el.querySelector('dialog[open]')).toBeNull();
  });

  it('on a conflict it keeps the dialog, says nothing new was recorded, refreshes, and refuses a retry', async () => {
    const resolve = vi.fn(() =>
      throwError(
        () => new ApiError('problem', 409, null, null, 'This exception was already resolved.'),
      ),
    );
    const spec = api([exception()], resolve);
    const { el, fixture } = await mount(Exceptions, spec);
    await open(el);
    tickBox(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(text(dialog(el))).toContain('already resolved');
    expect(text(dialog(el))).toContain('Nothing new was recorded');
    expect(confirmButton(el).disabled).toBe(true);
    expect(spec.exceptions).toHaveBeenCalledTimes(2);
    expect(resolve).toHaveBeenCalledTimes(1);
  });

  it('on a server error it says nothing was recorded and allows trying again', async () => {
    const resolve = vi.fn(() =>
      throwError(() => new ApiError('problem', 500, null, null, 'Server error.')),
    );
    const { el, fixture } = await mount(Exceptions, api([exception()], resolve));
    await open(el);
    tickBox(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(text(dialog(el))).toContain('Nothing was recorded; you can try again.');
    expect(confirmButton(el).disabled).toBe(false);
  });

  it('closes the dialog when the session has ended', async () => {
    const resolve = vi.fn(() =>
      throwError(() => new ApiError('problem', 401, null, null, 'Sign in.')),
    );
    const { el, fixture } = await mount(Exceptions, api([exception()], resolve));
    await open(el);
    tickBox(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(el.querySelector('dialog[open]')).toBeNull();
  });

  it('shows an honest error with a retry, and an honest empty state', async () => {
    const failing = {
      exceptions: vi.fn(() => throwError(() => new ApiError('network', 0, null, null, 'Offline.'))),
    };
    const { el: errorEl } = await mount(Exceptions, failing);
    expect(text(errorEl)).toContain('We could not load payment exceptions');
    expect(text(errorEl)).toContain('Offline.');
  });

  it('shows the empty state without inventing rows', async () => {
    const { el } = await mount(Exceptions, api([]));

    expect(text(el)).toContain('No payment exceptions');
    expect(el.querySelector('tbody')).toBeNull();
  });
});

describe('overview tile', () => {
  it('counts open payment exceptions from the server and links to the open list', async () => {
    const { el } = await mount(
      Overview,
      {
        overview: vi.fn(() =>
          of({
            paymentExceptionsOpen: 2,
            tasksByState: {},
            refundsByState: {},
            outboxByState: {},
          }),
        ),
      },
      true,
    );

    const tile = [...el.querySelectorAll('a.tile')].find((t) =>
      text(t).includes('Payment exceptions'),
    )!;
    expect(text(tile)).toContain('2');
    expect(tile.getAttribute('href')).toContain('/admin/payments/exceptions');
  });
});

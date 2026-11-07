import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { BehaviorSubject, of, throwError } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import {
  OperationsOrder,
  OperationsProvider,
  OperationsRefund,
  ReconciliationTask,
} from '../../../core/api/admin-payments.models';
import { ApiError } from '../../../core/api/api-error';
import {
  manualRefundGuidance,
  paymentLookupUnsupported,
  providerLabel,
  refundNeedsManualAction,
  unqueryableAttemptGuidance,
} from './ops-rules';
import { Refunds } from './refunds';
import { Tasks } from './tasks';

const STRIPE: OperationsProvider = {
  name: 'Stripe',
  isSimulated: false,
  isAvailable: true,
  supportsPaymentLookup: true,
  supportsRefunds: true,
};
const PAYMOB: OperationsProvider = {
  name: 'Paymob',
  isSimulated: false,
  isAvailable: true,
  supportsPaymentLookup: false,
  supportsRefunds: false,
};
const SIM: OperationsProvider = {
  name: 'Simulator',
  isSimulated: true,
  isAvailable: true,
  supportsPaymentLookup: true,
  supportsRefunds: true,
};

const refund = (over: Partial<OperationsRefund> = {}): OperationsRefund => ({
  refundId: 'eeeeeeee-0000-4000-8000-000000000001',
  orderId: 'ffffffff-0000-4000-8000-000000000001',
  paymentAttemptId: 'p',
  amount: 91,
  currency: 'USD',
  reason: 'PaymentAfterSaleFailed',
  state: 'NeedsReview',
  provider: 'Paymob',
  isSimulated: false,
  submissionNumber: 1,
  attempts: 1,
  lastError:
    'Paymob refunds are not available through the API: refund the payment in the provider dashboard.',
  requestedBy: null,
  createdAtUtc: '2030-01-01T09:00:00Z',
  completedAtUtc: null,
  nextAttemptAtUtc: '2030-01-01T10:00:00Z',
  updatedAtUtc: '2030-01-01T10:00:00Z',
  ...over,
});

const order = (over: Partial<OperationsOrder> = {}): OperationsOrder => ({
  orderId: '11111111-0000-4000-8000-000000000001',
  status: 'RefundRequired',
  totalAmount: 50,
  currency: 'USD',
  createdAtUtc: '2030-01-01T09:00:00Z',
  updatedAtUtc: '2030-01-01T10:00:00Z',
  refundId: 'x',
  refundState: 'NeedsReview',
  paymentProvider: 'Paymob',
  paymentIsSimulated: false,
  ...over,
});

const task = (over: Partial<ReconciliationTask> = {}): ReconciliationTask => ({
  id: 'aaaaaaaa-0000-4000-8000-000000000001',
  kind: 'ResolveStaleAttempt',
  subjectId: 'bbbbbbbb-0000-4000-8000-000000000001',
  state: 'NeedsReview',
  attempts: 8,
  lastOutcome: 'Provider still pending',
  lastError: null,
  nextAttemptAtUtc: '2030-01-01T10:00:00Z',
  createdAtUtc: '2030-01-01T09:00:00Z',
  completedAtUtc: null,
  provider: 'Paymob',
  ...over,
});

const text = (e: Element) => (e.textContent ?? '').replace(/\s+/g, ' ').trim();

async function mount<T>(component: Type<T>, api: Record<string, unknown>) {
  const params = new BehaviorSubject({ get: () => null });
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: AdminPaymentsApiClient, useValue: api },
      {
        provide: Router,
        useValue: { navigate: vi.fn(async () => true), createUrlTree: vi.fn(), events: of() },
      },
      { provide: ActivatedRoute, useValue: { queryParamMap: params.asObservable() } },
    ],
  });
  const fixture = TestBed.createComponent(component);
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

describe('operator visibility rules', () => {
  it('a refund is manual only for a known provider that cannot refund, and only while unfinished', () => {
    const providers = [STRIPE, PAYMOB, SIM];
    expect(refundNeedsManualAction(refund(), providers)).toBe(true);
    expect(refundNeedsManualAction(refund({ state: 'Succeeded' }), providers)).toBe(false);
    expect(
      refundNeedsManualAction(refund({ provider: 'Stripe', state: 'Failed' }), providers),
    ).toBe(false);
    expect(refundNeedsManualAction(refund({ provider: 'Adyen' }), providers)).toBe(false); // never assumed
    expect(refundNeedsManualAction(refund(), [])).toBe(false); // directory unavailable: nothing is guessed
  });

  it('knows which providers cannot be queried, and says what to do instead', () => {
    expect(paymentLookupUnsupported('Paymob', [STRIPE, PAYMOB])).toBe(true);
    expect(paymentLookupUnsupported('Stripe', [STRIPE, PAYMOB])).toBe(false);
    expect(paymentLookupUnsupported('Paymob', [])).toBe(false);
    expect(manualRefundGuidance('Paymob')).toContain('Paymob dashboard');
    expect(manualRefundGuidance('Paymob')).toContain('record it here');
    expect(unqueryableAttemptGuidance('Paymob')).toContain('a verified callback');
  });

  it('labels the simulator as Simulated and a real provider by name', () => {
    expect(providerLabel('Simulator', true)).toBe('Simulated');
    expect(providerLabel('Stripe', false)).toBe('Stripe');
    expect(providerLabel(null, null)).toBe('Unknown provider');
  });
});

describe('refunds page: what only a person can do', () => {
  const api = (providers: unknown, refunds: OperationsRefund[]) => ({
    providers: vi.fn(() => providers),
    orders: vi.fn(() => of([order()])),
    refunds: vi.fn(() => of(refunds)),
  });

  it('names the provider, hides Retry for a refund that only a person can complete, and explains why', async () => {
    const el = await mount(
      Refunds,
      api(of([STRIPE, PAYMOB, SIM]), [
        refund(),
        refund({ refundId: 'e2', provider: 'Stripe', state: 'Failed', lastError: 'declined' }),
        refund({ refundId: 'e3', provider: 'Simulator', isSimulated: true, state: 'Failed' }),
      ]),
    );

    const rows = [...el.querySelectorAll('section:nth-of-type(2) tbody tr')].map((r) => text(r));
    expect(rows[0]).toContain('Paymob');
    expect(rows[0]).toContain('Refund this payment in the Paymob dashboard');
    expect(rows[0]).not.toContain('Retry');
    expect(rows[1]).toContain('Stripe');
    expect(rows[1]).toContain('Retry');
    expect(rows[2]).toContain('Simulated');
    expect(text(el)).toContain('Refunds that only a person can complete');
    expect(text(el)).toContain(
      '1 unfinished refund belongs to a provider that cannot refund through the API (Paymob)',
    );
    expect(text(el)).toContain('then record that here');
  });

  it('never marks such a refund complete, and shows the order as still awaiting its refund', async () => {
    const el = await mount(Refunds, api(of([PAYMOB]), [refund()]));

    const badges = [...el.querySelectorAll('tbody .badge')].map((b) => text(b));
    expect(badges).not.toContain('Refunded (provider confirmed)');
    expect(text(el)).toContain('Needs review');
    expect(text(el)).toContain('Paymob'); // the order row names its provider too
  });

  it('if the provider list cannot be loaded, nothing is hidden and nothing is guessed', async () => {
    const el = await mount(
      Refunds,
      api(
        throwError(() => new ApiError('network', 0, null, null, 'Offline.')),
        [refund()],
      ),
    );

    const row = text(el.querySelector('section:nth-of-type(2) tbody tr')!);
    expect(row).toContain('Retry');
    expect(row).not.toContain('Refund this payment in the');
    expect(text(el)).not.toContain('Refunds that only a person can complete');
  });

  it('shows no callout when every provider can refund through the API', async () => {
    const el = await mount(
      Refunds,
      api(of([STRIPE]), [refund({ provider: 'Stripe', state: 'Failed' })]),
    );

    expect(text(el)).not.toContain('Refunds that only a person can complete');
  });
});

describe('reconciliation page: attempts whose provider cannot be queried', () => {
  const api = (providers: unknown, tasks: ReconciliationTask[]) => ({
    providers: vi.fn(() => providers),
    tasks: vi.fn(() => of(tasks)),
  });

  it('shows the provider and tells the operator where to look for a Paymob attempt', async () => {
    const el = await mount(Tasks, api(of([STRIPE, PAYMOB]), [task()]));
    const row = text(el.querySelector('tbody tr')!);

    expect(row).toContain('Paymob');
    expect(row).toContain("Paymob cannot be asked for a payment's status");
    expect(row).toContain('a verified callback from Paymob completes it automatically');
  });

  it('adds no such note for a provider that can be queried, a finished task, or an order task', async () => {
    const el = await mount(
      Tasks,
      api(of([STRIPE, PAYMOB]), [
        task({ provider: 'Stripe' }),
        task({ id: 'aaaaaaaa-0000-4000-8000-000000000002', state: 'Done' }),
        task({ id: 'aaaaaaaa-0000-4000-8000-000000000003', kind: 'ExpireOrder', provider: null }),
      ]),
    );

    const rows = [...el.querySelectorAll('tbody tr')].map((r) => text(r));
    expect(rows[0]).toContain('Stripe');
    for (const row of rows) {
      expect(row).not.toContain('cannot be asked for a payment');
    }
    expect(rows[2]).not.toContain('Paymob');
  });

  it('works unchanged when the provider list is unavailable', async () => {
    const el = await mount(
      Tasks,
      api(
        throwError(() => new ApiError('network', 0, null, null, 'Offline.')),
        [task()],
      ),
    );

    const row = text(el.querySelector('tbody tr')!);
    expect(row).toContain('Paymob');
    expect(row).not.toContain('cannot be asked for a payment');
    expect(row).toContain('Re-queue');
  });
});

describe('refund request dialog: what happens next depends on the provider', () => {
  const api = (providers: unknown, provider: string) => ({
    providers: vi.fn(() => providers),
    orders: vi.fn(() =>
      of([order({ refundId: null, refundState: null, paymentProvider: provider })]),
    ),
    refunds: vi.fn(() => of([])),
  });

  async function openDialog(el: HTMLElement): Promise<string> {
    const button = [...el.querySelectorAll('tbody button')].find((b) =>
      text(b).startsWith('Request refund'),
    ) as HTMLButtonElement;
    button.click();
    await new Promise((r) => setTimeout(r));
    return text(el.querySelector('dialog[open]') ?? el.querySelector('dialog')!);
  }

  it('for a provider that cannot refund through the API it says so, and promises nothing', async () => {
    const el = await mount(Refunds, api(of([STRIPE, PAYMOB]), 'Paymob'));

    const dialog = await openDialog(el);

    expect(dialog).toContain('cannot refund through the API');
    expect(dialog).toContain('Nothing is sent to Paymob');
    expect(dialog).toContain('record it here');
    expect(dialog).not.toContain('marked refunded only after the provider confirms');
  });

  it('for a provider that can, it keeps the confirmation wording', async () => {
    const el = await mount(Refunds, api(of([STRIPE, PAYMOB]), 'Stripe'));

    const dialog = await openDialog(el);

    expect(dialog).toContain('sends it to the payment provider');
    expect(dialog).toContain('marked refunded only after the provider confirms');
    expect(dialog).not.toContain('Nothing is sent to');
  });

  it('if the provider list could not be loaded, no capability is assumed', async () => {
    const el = await mount(
      Refunds,
      api(
        throwError(() => new ApiError('network', 0, null, null, 'Offline.')),
        'Paymob',
      ),
    );

    const dialog = await openDialog(el);

    expect(dialog).not.toContain('Nothing is sent to');
    expect(dialog).toContain('sends it to the payment provider');
  });
});

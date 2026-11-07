import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { BehaviorSubject, of, throwError } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { FinancialTransition } from '../../../core/api/admin-payments.models';
import { ApiError } from '../../../core/api/api-error';
import { History } from './history';

const ORDER = 'ffffffff-0000-4000-8000-000000000001';

const row = (over: Partial<FinancialTransition> = {}): FinancialTransition => ({
  id: 'eeeeeeee-0000-4000-8000-000000000001',
  occurredAtUtc: '2030-01-01T09:30:00Z',
  entityType: 'PaymentAttempt',
  entityId: 'aaaaaaaa-0000-4000-8000-000000000001',
  orderId: ORDER,
  fromState: 'Initiated',
  toState: 'Succeeded',
  eventCode: 'provider_verdict',
  kind: 'Transition',
  source: 'ProviderWebhook',
  factBasis: 'ProviderVerified',
  provider: 'Stripe',
  providerEventId: 'evt_test_1',
  actorUserId: null,
  correlationId: 'corr-1',
  reason: 'provider_verdict',
  ...over,
});

const text = (e: Element) => (e.textContent ?? '').replace(/\s+/g, ' ').trim();

async function mount(api: Record<string, unknown>, order: string | null = null) {
  const navigate = vi.fn(async () => true);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: AdminPaymentsApiClient, useValue: api },
      { provide: Router, useValue: { navigate, createUrlTree: vi.fn(), events: of() } },
      {
        provide: ActivatedRoute,
        useValue: {
          queryParamMap: new BehaviorSubject({
            get: (k: string) => (k === 'order' ? order : null),
          }).asObservable(),
        },
      },
    ],
  });
  const fixture = TestBed.createComponent(History);
  await fixture.whenStable();
  return { el: fixture.nativeElement as HTMLElement, fixture, navigate };
}

const type = (el: HTMLElement, value: string) => {
  const input = el.querySelector('#history-order') as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
};
const submit = (el: HTMLElement) =>
  (el.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));

describe('financial history page', () => {
  it('asks for an order id first and calls nothing', async () => {
    const history = vi.fn(() => of([]));
    const { el } = await mount({ history });

    expect(text(el)).toContain('Enter an order id');
    expect(history).not.toHaveBeenCalled();
  });

  it('rejects an id that is not a full UUID without calling the server', async () => {
    const history = vi.fn(() => of([]));
    const { el, fixture } = await mount({ history });
    type(el, 'not-an-id');
    submit(el);
    fixture.detectChanges();

    expect(history).not.toHaveBeenCalled();
    expect(text(el)).toContain('Enter a full order id');
    expect(el.querySelector('#history-order')!.getAttribute('aria-invalid')).toBe('true');
    expect(el.querySelector('#history-order')!.getAttribute('aria-describedby')).toBe(
      'history-order-error',
    );
  });

  it('loads and keeps the order id in the address', async () => {
    const history = vi.fn(() => of([row()]));
    const { el, fixture, navigate } = await mount({ history });
    type(el, `  ${ORDER}  `);
    submit(el);
    fixture.detectChanges();

    expect(history).toHaveBeenCalledWith(ORDER, null, 200);
    expect(navigate).toHaveBeenCalled();
  });

  it('loads straight from the address', async () => {
    const history = vi.fn(() => of([row()]));
    const { el, fixture } = await mount({ history }, ORDER);
    fixture.detectChanges();

    expect(history).toHaveBeenCalledWith(ORDER, null, 200);
    expect(el.querySelector('tbody tr')).not.toBeNull();
  });

  it('labels a provider-verified fact apart from an administrator statement', async () => {
    const { el, fixture } = await mount(
      {
        history: vi.fn(() =>
          of([
            row(),
            row({
              id: 'eeeeeeee-0000-4000-8000-000000000002',
              entityType: 'PaymentAnomaly',
              fromState: 'Open',
              toState: 'Resolved',
              source: 'Administrator',
              factBasis: 'AdministratorStatement',
              provider: null,
              providerEventId: null,
              actorUserId: '99999999-0000-4000-8000-000000000001',
              reason: 'no_action_needed',
            }),
          ]),
        ),
      },
      ORDER,
    );
    fixture.detectChanges();
    const rows = [...el.querySelectorAll('tbody tr')].map(text);

    expect(rows[0]).toContain('Payment attempt');
    expect(rows[0]).toContain('Initiated → Succeeded');
    expect(rows[0]).toContain('Provider-verified fact');
    expect(rows[0]).toContain('Provider webhook (signature verified)');
    expect(rows[0]).toContain('evt_test_1');
    expect(rows[1]).toContain('Payment anomaly');
    expect(rows[1]).toContain('Administrator statement (not provider-verified)');
    expect(rows[1]).not.toContain('Provider-verified fact');
  });

  it('shows a refused event as refused, with nothing changed', async () => {
    const { el, fixture } = await mount(
      {
        history: vi.fn(() =>
          of([row({ kind: 'Rejected', fromState: 'Succeeded', toState: 'Failed' })]),
        ),
      },
      ORDER,
    );
    fixture.detectChanges();
    const r = text(el.querySelector('tbody tr')!);

    expect(r).toContain('Refused, nothing changed');
    expect(r).toContain('stayed Succeeded');
    expect(r).not.toContain('Succeeded → Failed');
  });

  it('shows a creation as created, not as a change from nothing', async () => {
    const { el, fixture } = await mount(
      { history: vi.fn(() => of([row({ fromState: null, toState: 'Open' })])) },
      ORDER,
    );
    fixture.detectChanges();

    expect(text(el.querySelector('tbody tr')!)).toContain('created → Open');
  });

  it('says every row was written at the time, and that nothing is reconstructed', async () => {
    const { el, fixture } = await mount(
      { history: vi.fn(() => of([row({ fromState: null })])) },
      ORDER,
    );
    fixture.detectChanges();

    expect(text(el)).toContain('written by the database at the moment the change happened');
    expect(text(el)).toContain('Nothing here is reconstructed afterwards');
    expect(text(el)).not.toContain('Some history starts mid-life');
  });

  it('warns when an entity’s earliest recorded change is already a move from an existing state', async () => {
    const { el, fixture } = await mount(
      {
        history: vi.fn(() =>
          of([
            row({
              id: 'eeeeeeee-0000-4000-8000-0000000000a2',
              occurredAtUtc: '2030-01-02T09:30:00Z',
              entityId: 'aaaaaaaa-0000-4000-8000-0000000000c1',
              fromState: 'Initiated',
              toState: 'Succeeded',
            }),
            row({
              id: 'eeeeeeee-0000-4000-8000-0000000000a1',
              occurredAtUtc: '2030-01-01T09:30:00Z',
              entityType: 'Order',
              entityId: 'ffffffff-0000-4000-8000-0000000000b1',
              fromState: 'PendingPayment',
              toState: 'Paid',
            }),
            row({
              id: 'eeeeeeee-0000-4000-8000-0000000000a0',
              occurredAtUtc: '2030-01-01T09:00:00Z',
              entityId: 'aaaaaaaa-0000-4000-8000-0000000000c1',
              fromState: null,
              toState: 'Initiated',
            }),
          ]),
        ),
      },
      ORDER,
    );
    fixture.detectChanges();

    const page = text(el);
    expect(page).toContain('Some history starts mid-life');
    expect(page).toContain('order ffffffff');
    expect(page).toContain('never recorded');
    expect(page).toContain('not reconstructed');
    // the payment attempt that was CREATED in the history is not flagged
    expect(page).not.toContain('payment attempt aaaaaaaa');
  });

  it('does not judge where a history starts when the page may hide older rows', async () => {
    const many = Array.from({ length: 200 }, (_, n) =>
      row({
        id: `eeeeeeee-0000-4000-8000-${String(n + 1000).padStart(12, '0')}`,
        fromState: 'Initiated',
      }),
    );
    const { el, fixture } = await mount({ history: vi.fn(() => of(many)) }, ORDER);
    fixture.detectChanges();

    expect(text(el)).toContain('Only the newest rows are shown');
    expect(text(el)).not.toContain('Some history starts mid-life');
  });

  it('is honest about an empty history and does not claim a reconstruction', async () => {
    const { el, fixture } = await mount({ history: vi.fn(() => of([])) }, ORDER);
    fixture.detectChanges();

    expect(text(el)).toContain('No history for this order');
    expect(text(el)).toContain('not reconstructed for earlier changes');
    expect(el.querySelector('tbody')).toBeNull();
  });

  it('shows an honest error with a retry, and 403 is not presented as an empty history', async () => {
    const history = vi.fn(() =>
      throwError(() => new ApiError('problem', 403, null, null, 'Administrator access required.')),
    );
    const { el, fixture } = await mount({ history }, ORDER);
    fixture.detectChanges();

    expect(text(el)).toContain('We could not load the history');
    expect(text(el)).toContain('Administrator access required.');
    expect(text(el)).not.toContain('No history for this order');
  });

  it('never shows a secret-like value', async () => {
    const { el, fixture } = await mount({ history: vi.fn(() => of([row()])) }, ORDER);
    fixture.detectChanges();

    expect(text(el).toLowerCase()).not.toMatch(/signature=|secret|payload|hmac/);
  });
});

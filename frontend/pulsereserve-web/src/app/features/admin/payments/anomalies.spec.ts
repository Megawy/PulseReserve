import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { PaymentAnomaly } from '../../../core/api/admin-payments.models';
import { ApiError } from '../../../core/api/api-error';
import { formatDuration, formatMinorUnits, formatMoney } from '../../../shared/format';
import { anomalyKindLabel, anomalyResolutionLabel } from '../../../shared/presentation';
import { Anomalies } from './anomalies';
import { canInvestigateAnomaly } from './ops-rules';
import { Overview } from './overview';

const anomaly = (over: Partial<PaymentAnomaly> = {}): PaymentAnomaly => ({
  anomalyId: 'dddddddd-0000-4000-8000-000000000001',
  kind: 'AmountOrCurrencyMismatch',
  reasonCode: 'amount_mismatch',
  state: 'Open',
  provider: 'Stripe',
  providerEventId: 'evt_test_1',
  attemptId: 'aaaaaaaa-0000-4000-8000-000000000001',
  orderId: 'ffffffff-0000-4000-8000-000000000001',
  claimedAttemptId: 'aaaaaaaa-0000-4000-8000-000000000001',
  claimedOrderId: 'ffffffff-0000-4000-8000-000000000001',
  receivedAmountMinor: 100,
  receivedCurrency: 'USD',
  expectedAmountMinor: 9100,
  expectedCurrency: 'USD',
  orderStatusAtDetection: 'PendingPayment',
  attemptStatusAtDetection: 'Initiated',
  detectedAtUtc: '2030-01-01T09:30:00Z',
  resolution: null,
  resolvedBy: null,
  resolvedAtUtc: null,
  resolutionReference: null,
  resolutionNote: null,
  orderStatus: 'PendingPayment',
  nextAction: 'InvestigateThenRecord',
  ageSeconds: 7500,
  ...over,
});

const text = (e: Element) => (e.textContent ?? '').replace(/\s+/g, ' ').trim();
const money = (minor: number, currency: string) =>
  formatMinorUnits(minor, currency).replace(/\s+/g, ' ');

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

describe('payment anomaly rules and wording', () => {
  it('offers only the step the server names', () => {
    expect(canInvestigateAnomaly(anomaly())).toBe(true);
    expect(canInvestigateAnomaly(anomaly({ nextAction: 'None' }))).toBe(false);
    expect(canInvestigateAnomaly({ nextAction: 'RefundExternallyThenRecord' })).toBe(false);
  });

  it('labels every kind and resolution, and never as a provider confirmation', () => {
    for (const kind of [
      'AmountOrCurrencyMismatch',
      'ReferenceMismatch',
      'UnknownPaymentReference',
      'InconsistentAssociation',
    ]) {
      expect(anomalyKindLabel(kind)).not.toBe(kind);
    }
    for (const resolution of ['NotOurPayment', 'RefundedExternally', 'NoActionNeeded']) {
      expect(anomalyResolutionLabel(resolution)).not.toMatch(/provider (confirmed|verified)/i);
    }
  });

  it('words a waiting time from whole seconds, and calls an unusable value unknown', () => {
    expect(formatDuration(5)).toBe('under a minute');
    expect(formatDuration(60)).toBe('1 min');
    expect(formatDuration(3600)).toBe('1 h');
    expect(formatDuration(7500)).toBe('2 h 5 min');
    expect(formatDuration(86_400)).toBe('1 d');
    expect(formatDuration(90_000)).toBe('1 d 1 h');
    expect(formatDuration(3 * 86_400 + 2 * 3600)).toBe('3 d 2 h');
    for (const bad of [null, undefined, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(formatDuration(bad as number | null | undefined)).toBe('unknown');
    }
  });

  it('formats minor units with the currency’s own number of decimals and never invents a currency', () => {
    expect(formatMinorUnits(9100, 'USD').replace(/\s+/g, ' ')).toBe(
      formatMoney(91, 'USD').replace(/\s+/g, ' '),
    );
    expect(formatMinorUnits(100, 'JPY').replace(/\s+/g, ' ')).toBe(
      formatMoney(100, 'JPY').replace(/\s+/g, ' '),
    );
    expect(formatMinorUnits(null, 'USD')).toBe('not stated');
    expect(formatMinorUnits(100, null)).toContain('currency not stated');
    expect(formatMinorUnits(100, '??')).toContain('minor units');
  });
});

describe('payment anomalies page', () => {
  const api = (items: PaymentAnomaly[], resolve?: unknown) => ({
    anomalies: vi.fn(() => of(items)),
    resolveAnomaly: resolve ?? vi.fn(),
  });
  const dialog = (el: HTMLElement) => el.querySelector('dialog')!;
  const confirmButton = (el: HTMLElement) =>
    [...dialog(el).querySelectorAll('button')].at(-1) as HTMLButtonElement;
  const settle = () => new Promise((r) => setTimeout(r));
  const open = async (el: HTMLElement) => {
    const button = [...el.querySelectorAll('tbody button')].find((b) =>
      text(b).startsWith('Record outcome'),
    ) as HTMLButtonElement;
    button.click();
    await settle();
  };
  const choose = (el: HTMLElement, value: string) => {
    const radio = [...dialog(el).querySelectorAll('input[type=radio]')].find(
      (r) => (r as HTMLInputElement).value === value,
    ) as HTMLInputElement;
    radio.checked = true;
    radio.dispatchEvent(new Event('change'));
  };
  const tickBox = (el: HTMLElement) => {
    const box = dialog(el).querySelector('input[type=checkbox]') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
  };
  const ready = (el: HTMLElement, value = 'NoActionNeeded') => {
    choose(el, value);
    tickBox(el);
  };

  it('keeps what the event claimed (untrusted) apart from what this system records and says nothing was applied', async () => {
    const { el } = await mount(Anomalies, api([anomaly()]));
    const row = text(el.querySelector('tbody tr')!);

    expect(row).toContain('Amount or currency does not match the order');
    expect(row).toContain('evt_test_1');
    expect(row).toContain(`Amount ${money(100, 'USD')}`);
    expect(row).toContain('Taken from the event as received.');
    expect(row).toContain(`expects ${money(9100, 'USD')}`);
    expect(row).toContain('Not applied');
    expect(row).toContain('No order was paid, no ticket issued, no refund made or created.');
    expect(row).toContain('Check it in the Stripe dashboard first.');
    expect(row).not.toMatch(/refunded|provider confirmed|was paid by|has been paid/i);

    const page = text(el);
    expect(page).toContain('nothing was paid, no tickets were issued');
    expect(page.toLowerCase()).not.toMatch(/signature=|secret|payload|client_secret|hmac/);
  });

  it('shows how long an open anomaly has waited, on the server’s clock, and not for a resolved one', async () => {
    const { el } = await mount(
      Anomalies,
      api([
        anomaly(),
        anomaly({
          anomalyId: 'dddddddd-0000-4000-8000-000000000002',
          state: 'Resolved',
          nextAction: 'None',
          resolution: 'NoActionNeeded',
          resolvedBy: '99999999-0000-4000-8000-000000000001',
          resolvedAtUtc: '2030-01-02T09:00:00Z',
        }),
      ]),
    );
    const rows = [...el.querySelectorAll('tbody tr')].map(text);

    expect(rows[0]).toContain('Waiting 2 h 5 min');
    expect(rows[1]).not.toContain('Waiting 2 h');
    expect(rows[1]).not.toMatch(/Waiting \d/);
  });

  it('says that open items come first, oldest first, and warns when the page may hide more', async () => {
    const many = Array.from({ length: 50 }, (_, n) =>
      anomaly({ anomalyId: `dddddddd-0000-4000-8000-${String(n + 10).padStart(12, '0')}` }),
    );
    const { el } = await mount(Anomalies, api(many));

    expect(text(el)).toContain('open ones first, oldest first, then resolved');
    expect(text(el)).not.toContain('newest first');
    expect(text(el)).toContain('There may be more: raise the limit to see more.');
    expect(text(el)).toContain('Show up to');
  });

  it('shows an event that matches no local payment without inventing an order', async () => {
    const { el } = await mount(
      Anomalies,
      api([
        anomaly({
          kind: 'UnknownPaymentReference',
          attemptId: null,
          orderId: null,
          orderStatus: null,
          expectedAmountMinor: null,
          expectedCurrency: null,
          orderStatusAtDetection: null,
          attemptStatusAtDetection: null,
        }),
      ]),
    );
    const row = text(el.querySelector('tbody tr')!);

    expect(row).toContain('Payment for an attempt this system does not know');
    expect(row).toContain('No local payment was matched.');
    expect(row).toContain('not linked to any order or attempt');
    expect(row).not.toContain('expects');
  });

  it('shows a resolved anomaly as an administrator statement without any action', async () => {
    const { el } = await mount(
      Anomalies,
      api([
        anomaly({
          state: 'Resolved',
          nextAction: 'None',
          resolution: 'NotOurPayment',
          resolvedBy: '99999999-0000-4000-8000-000000000001',
          resolvedAtUtc: '2030-01-02T09:00:00Z',
          resolutionReference: 'dash-1',
        }),
      ]),
    );
    const row = text(el.querySelector('tbody tr')!);

    expect(row).toContain('Resolved (administrator statement)');
    expect(row).toContain('The provider did not confirm it');
    expect(row).toContain('dash-1');
    expect(row).toContain('Nothing more to do here.');
    expect(el.querySelector('tbody button')).toBeNull();
  });

  it('offers no action unless the server names one', async () => {
    const { el } = await mount(Anomalies, api([anomaly({ nextAction: 'None' })]));

    expect(el.querySelector('tbody button')).toBeNull();
  });

  it('cannot be confirmed without a choice and the explicit tick, and sends nothing before', async () => {
    const resolve = vi.fn(() => of(anomaly({ state: 'Resolved', nextAction: 'None' })));
    const { el, fixture } = await mount(Anomalies, api([anomaly()], resolve));
    await open(el);
    fixture.detectChanges();

    expect(text(dialog(el))).toContain('has not confirmed your conclusion');
    expect(text(dialog(el))).toContain('does not pay or refund anything');
    expect(confirmButton(el).disabled).toBe(true);
    confirmButton(el).click();
    expect(resolve).not.toHaveBeenCalled();

    tickBox(el);
    fixture.detectChanges();
    expect(confirmButton(el).disabled).toBe(true);

    choose(el, 'NotOurPayment');
    fixture.detectChanges();
    expect(confirmButton(el).disabled).toBe(false);
  });

  it('has unique accessible ids and a described, labelled dialog', async () => {
    const { el, fixture } = await mount(Anomalies, api([anomaly()]));
    await open(el);
    fixture.detectChanges();

    const labelledBy = dialog(el).getAttribute('aria-labelledby')!;
    const describedBy = dialog(el).getAttribute('aria-describedby')!;
    expect(labelledBy).toBeTruthy();
    expect(describedBy).toBeTruthy();
    expect(labelledBy).not.toBe(describedBy);
    expect(el.querySelectorAll(`#${labelledBy}`).length).toBe(1);
    expect(el.querySelectorAll(`#${describedBy}`).length).toBe(1);
    expect(dialog(el).querySelector('label[for=anomaly-reference]')).not.toBeNull();
    expect(dialog(el).querySelector('label[for=anomaly-note]')).not.toBeNull();
  });

  it('sends the chosen statement, trims optional text, and blocks a double submit', async () => {
    const response = new Subject<PaymentAnomaly>();
    const resolve = vi.fn(() => response);
    const { el, fixture } = await mount(Anomalies, api([anomaly()], resolve));
    await open(el);
    const ref = dialog(el).querySelector('#anomaly-reference') as HTMLInputElement;
    ref.value = '  dash-9  ';
    ref.dispatchEvent(new Event('input'));
    ready(el, 'RefundedExternally');
    fixture.detectChanges();

    confirmButton(el).click();
    confirmButton(el).click();
    fixture.detectChanges();

    expect(resolve).toHaveBeenCalledTimes(1);
    expect(resolve).toHaveBeenCalledWith('dddddddd-0000-4000-8000-000000000001', {
      investigationConfirmed: true,
      resolution: 'RefundedExternally',
      externalReference: 'dash-9',
      note: null,
    });
    expect(text(confirmButton(el))).toContain('Recording');
    expect(confirmButton(el).disabled).toBe(true);
  });

  it('after success it reloads the list and closes the dialog', async () => {
    const resolve = vi.fn(() => of(anomaly({ state: 'Resolved', nextAction: 'None' })));
    const spec = api([anomaly()], resolve);
    const { el, fixture } = await mount(Anomalies, spec);
    await open(el);
    ready(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(spec.anomalies).toHaveBeenCalledTimes(2);
    expect(el.querySelector('dialog[open]')).toBeNull();
  });

  it('on a conflict it keeps the dialog, says nothing new was recorded, refreshes, and refuses a retry', async () => {
    const resolve = vi.fn(() =>
      throwError(
        () => new ApiError('problem', 409, null, null, 'This anomaly was already resolved.'),
      ),
    );
    const spec = api([anomaly()], resolve);
    const { el, fixture } = await mount(Anomalies, spec);
    await open(el);
    ready(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(text(dialog(el))).toContain('already resolved');
    expect(text(dialog(el))).toContain('Nothing new was recorded');
    expect(confirmButton(el).disabled).toBe(true);
    expect(spec.anomalies).toHaveBeenCalledTimes(2);
    expect(resolve).toHaveBeenCalledTimes(1);
  });

  it('on a missing anomaly it says nothing was recorded', async () => {
    const resolve = vi.fn(() =>
      throwError(() => new ApiError('problem', 404, null, null, 'Not found.')),
    );
    const { el, fixture } = await mount(Anomalies, api([anomaly()], resolve));
    await open(el);
    ready(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(text(dialog(el))).toContain('no longer exists. Nothing was recorded.');
    expect(confirmButton(el).disabled).toBe(true);
  });

  it('on a server error it says nothing was recorded and allows trying again', async () => {
    const resolve = vi.fn(() =>
      throwError(() => new ApiError('problem', 500, null, null, 'Server error.')),
    );
    const { el, fixture } = await mount(Anomalies, api([anomaly()], resolve));
    await open(el);
    ready(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(text(dialog(el))).toContain('Nothing was recorded; you can try again.');
    expect(confirmButton(el).disabled).toBe(false);
  });

  it('on a refusal it says why, keeps the dialog and does not claim anything was recorded', async () => {
    const resolve = vi.fn(() => throwError(() => new ApiError('problem', 403, null, null, 'No.')));
    const { el, fixture } = await mount(Anomalies, api([anomaly()], resolve));
    await open(el);
    ready(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(text(dialog(el))).toContain('administrator access is required');
    expect(text(dialog(el))).not.toMatch(/was recorded as/i);
  });

  it('on too many requests it shows the server’s own wording and allows trying again', async () => {
    const resolve = vi.fn(() =>
      throwError(
        () => new ApiError('problem', 429, null, null, 'Too many requests. Nothing was changed.'),
      ),
    );
    const { el, fixture } = await mount(Anomalies, api([anomaly()], resolve));
    await open(el);
    ready(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(text(dialog(el))).toContain('Too many requests. Nothing was changed.');
    expect(confirmButton(el).disabled).toBe(false);
  });

  it('closes the dialog when the session has ended', async () => {
    const resolve = vi.fn(() =>
      throwError(() => new ApiError('problem', 401, null, null, 'Sign in.')),
    );
    const { el, fixture } = await mount(Anomalies, api([anomaly()], resolve));
    await open(el);
    ready(el);
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(el.querySelector('dialog[open]')).toBeNull();
  });

  it('shows an honest error with a retry, and an honest empty state', async () => {
    const failing = {
      anomalies: vi.fn(() => throwError(() => new ApiError('network', 0, null, null, 'Offline.'))),
    };
    const { el: errorEl } = await mount(Anomalies, failing);
    expect(text(errorEl)).toContain('We could not load payment anomalies');
    expect(text(errorEl)).toContain('Offline.');

    TestBed.resetTestingModule();
    const { el } = await mount(Anomalies, api([]));
    expect(text(el)).toContain('No payment anomalies');
    expect(el.querySelector('tbody')).toBeNull();
  });
});

describe('overview anomaly tile', () => {
  it('counts open anomalies from the server and links to the open list', async () => {
    const { el } = await mount(
      Overview,
      {
        overview: vi.fn(() =>
          of({
            paymentAnomaliesOpen: 3,
            tasksByState: {},
            refundsByState: {},
            outboxByState: {},
          }),
        ),
      },
      true,
    );

    const tile = [...el.querySelectorAll('a.tile')].find((t) =>
      text(t).includes('Payment anomalies'),
    )!;
    expect(text(tile)).toContain('3');
    expect(tile.getAttribute('href')).toContain('/admin/payments/anomalies');
  });

  it('says how long the longest-waiting anomaly has waited, and stays plain when none is open', async () => {
    const summary = (over: object) => ({
      tasksByState: {},
      refundsByState: {},
      outboxByState: {},
      ...over,
    });
    const waiting = await mount(
      Overview,
      {
        overview: vi.fn(() =>
          of(
            summary({
              paymentAnomaliesOpen: 2,
              oldestOpenPaymentAnomalyAgeSeconds: 3 * 86_400 + 7200,
              paymentExceptionsOpen: 1,
              oldestOpenPaymentExceptionAgeSeconds: 600,
            }),
          ),
        ),
      },
      true,
    );
    const tiles = [...waiting.el.querySelectorAll('a.tile')].map(text);
    expect(tiles.find((t) => t.includes('Payment anomalies'))).toContain(
      'Oldest has waited 3 d 2 h',
    );
    expect(tiles.find((t) => t.includes('Payment exceptions'))).toContain(
      'Oldest has waited 10 min',
    );

    TestBed.resetTestingModule();
    const quiet = await mount(
      Overview,
      { overview: vi.fn(() => of(summary({ paymentAnomaliesOpen: 0 }))) },
      true,
    );
    const quietTile = [...quiet.el.querySelectorAll('.tile')].find((t) =>
      text(t).includes('Payment anomalies'),
    )!;
    expect(text(quietTile)).toContain('Verified events that were not applied');
    expect(text(quietTile)).not.toContain('Oldest has waited');
  });
});

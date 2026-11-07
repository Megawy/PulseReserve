import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { OperationsProvider, OperationsRefund } from '../../../core/api/admin-payments.models';
import { ApiError } from '../../../core/api/api-error';
import { statusView } from '../../../shared/presentation';
import { canRecordManualRefund, refundNeedsManualAction, refundRequestMessage } from './ops-rules';
import { Refunds } from './refunds';

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
  lastError: 'Paymob refunds are not available through the API.',
  requestedBy: null,
  createdAtUtc: '2030-01-01T09:00:00Z',
  completedAtUtc: null,
  nextAttemptAtUtc: '2030-01-01T10:00:00Z',
  updatedAtUtc: '2030-01-01T10:00:00Z',
  ...over,
});

const text = (e: Element) => (e.textContent ?? '').replace(/\s+/g, ' ').trim();

describe('manual refund rules', () => {
  it('is offered only for a refund waiting for review at a provider known not to refund via the API', () => {
    const providers = [STRIPE, PAYMOB];
    expect(canRecordManualRefund(refund(), providers)).toBe(true);
    expect(canRecordManualRefund(refund({ state: 'Pending' }), providers)).toBe(false);
    expect(canRecordManualRefund(refund({ state: 'ManuallyRecorded' }), providers)).toBe(false);
    expect(canRecordManualRefund(refund({ provider: 'Stripe' }), providers)).toBe(false);
    expect(canRecordManualRefund(refund({ provider: 'Adyen' }), providers)).toBe(false);
    expect(canRecordManualRefund(refund(), [])).toBe(false);
    expect(refundNeedsManualAction(refund({ state: 'ManuallyRecorded' }), providers)).toBe(false);
  });

  it('never says a manually recorded refund was confirmed by the provider when a request returns it', () => {
    expect(refundRequestMessage(refund({ state: 'ManuallyRecorded' }))).toContain(
      'The provider has not confirmed it',
    );
    expect(
      refundRequestMessage(refund({ state: 'Succeeded', completedAtUtc: '2030-01-01T00:00:00Z' })),
    ).toBe('The refund was already confirmed by the provider.');
    expect(refundRequestMessage(refund({ state: 'Pending' }))).toContain(
      'not complete until the provider confirms',
    );
  });

  it('labels a manual record differently from a provider-confirmed refund', () => {
    expect(statusView('refund', 'ManuallyRecorded').label).toBe('Refunded (manually recorded)');
    expect(statusView('refund', 'Succeeded').label).toBe('Refunded (provider confirmed)');
  });
});

describe('manual refund dialog', () => {
  async function mount(refunds: OperationsRefund[], record: ReturnType<typeof vi.fn>) {
    const api = {
      providers: vi.fn(() => of([STRIPE, PAYMOB])),
      orders: vi.fn(() => of([])),
      refunds: vi.fn(() => of(refunds)),
      recordManualRefund: record,
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AdminPaymentsApiClient, useValue: api },
        {
          provide: Router,
          useValue: { navigate: vi.fn(async () => true), createUrlTree: vi.fn(), events: of() },
        },
        {
          provide: ActivatedRoute,
          useValue: { queryParamMap: new BehaviorSubject({ get: () => null }).asObservable() },
        },
      ],
    });
    const fixture = TestBed.createComponent(Refunds);
    await fixture.whenStable();
    return { el: fixture.nativeElement as HTMLElement, fixture, api };
  }

  const open = async (el: HTMLElement) => {
    const button = [...el.querySelectorAll('tbody button')].find((b) =>
      text(b).startsWith('Record manual refund'),
    ) as HTMLButtonElement;
    button.click();
    await new Promise((r) => setTimeout(r));
  };
  const dialogOf = (el: HTMLElement) =>
    [...el.querySelectorAll('dialog')].find((d) =>
      d.textContent?.includes('Record a manual refund'),
    )!;
  const confirmButton = (el: HTMLElement) =>
    [...dialogOf(el).querySelectorAll('button')].at(-1) as HTMLButtonElement;
  const tick = (fixture: { detectChanges: () => void }) =>
    new Promise((r) => setTimeout(r)).then(() => fixture.detectChanges());

  it('offers the action only for the eligible row and explains the external step first', async () => {
    const { el } = await mount(
      [refund(), refund({ refundId: 'e2', provider: 'Stripe', state: 'Failed' })],
      vi.fn(),
    );
    const rows = [...el.querySelectorAll('section:nth-of-type(2) tbody tr')].map((r) => text(r));
    expect(rows[0]).toContain('Record manual refund');
    expect(rows[0]).toContain('Refund this payment in the Paymob dashboard first');
    expect(rows[1]).not.toContain('Record manual refund');
    expect(rows[1]).toContain('Retry');

    await open(el);
    const dialog = text(dialogOf(el));
    expect(dialog).toContain('already refunded this payment in the provider');
    expect(dialog).toContain('Nothing is sent to Paymob');
    expect(dialog).toContain('Paymob has not confirmed it');
    expect(dialog).toContain('manually recorded');
  });

  it('cannot be confirmed until the administrator ticks the confirmation, and sends nothing before', async () => {
    const record = vi.fn(() => of(refund({ state: 'ManuallyRecorded' })));
    const { el, fixture } = await mount([refund()], record);
    await open(el);
    fixture.detectChanges();

    expect(confirmButton(el).disabled).toBe(true);
    confirmButton(el).click();
    expect(record).not.toHaveBeenCalled();

    const box = dialogOf(el).querySelector('input[type=checkbox]') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(confirmButton(el).disabled).toBe(false);
  });

  it('sends the explicit confirmation with the amount shown, trims optional text, and blocks a double submit', async () => {
    const response = new Subject<OperationsRefund>();
    const record = vi.fn(() => response);
    const { el, fixture } = await mount([refund()], record);
    await open(el);
    const dialog = dialogOf(el);
    const ref = dialog.querySelector('#manual-reference') as HTMLInputElement;
    ref.value = '  PM-123  ';
    ref.dispatchEvent(new Event('input'));
    const box = dialog.querySelector('input[type=checkbox]') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    confirmButton(el).click();
    confirmButton(el).click();
    fixture.detectChanges();

    expect(record).toHaveBeenCalledTimes(1);
    expect(record).toHaveBeenCalledWith('eeeeeeee-0000-4000-8000-000000000001', {
      confirmedPerformedExternally: true,
      amount: 91,
      currency: 'USD',
      externalReference: 'PM-123',
      note: null,
    });
    expect(text(confirmButton(el))).toContain('Recording');
    expect(confirmButton(el).disabled).toBe(true);
  });

  it('on a conflict it keeps the dialog, says nothing new was recorded, and refuses a retry', async () => {
    const record = vi.fn(() =>
      throwError(
        () =>
          new ApiError(
            'problem',
            409,
            null,
            null,
            'This refund was already recorded as manually completed.',
          ),
      ),
    );
    const { el, fixture, api } = await mount([refund()], record);
    await open(el);
    const box = dialogOf(el).querySelector('input[type=checkbox]') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(text(dialogOf(el))).toContain('already recorded as manually completed');
    expect(text(dialogOf(el))).toContain('Nothing new was recorded');
    expect(confirmButton(el).disabled).toBe(true);
    expect(api.refunds).toHaveBeenCalledTimes(2); // the list was refreshed
    expect(record).toHaveBeenCalledTimes(1);
  });

  it('on a server error it says nothing was recorded and allows trying again', async () => {
    const record = vi.fn(() =>
      throwError(() => new ApiError('problem', 500, null, null, 'Server error.')),
    );
    const { el, fixture } = await mount([refund()], record);
    await open(el);
    const box = dialogOf(el).querySelector('input[type=checkbox]') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    confirmButton(el).click();
    await tick(fixture);

    expect(text(dialogOf(el))).toContain('Nothing was recorded; you can try again.');
    expect(confirmButton(el).disabled).toBe(false);
  });

  it('shows a recorded refund as manually recorded, never as provider confirmed', async () => {
    const { el } = await mount(
      [
        refund({
          state: 'ManuallyRecorded',
          completedAtUtc: '2030-01-02T09:00:00Z',
          manualReference: 'PM-123',
          manuallyRecordedBy: '99999999-0000-4000-8000-000000000001',
          manuallyRecordedAtUtc: '2030-01-02T09:00:00Z',
        }),
      ],
      vi.fn(),
    );
    const row = text(el.querySelector('section:nth-of-type(2) tbody tr')!);
    expect(row).toContain('Refunded (manually recorded)');
    expect(row).toContain('Not confirmed by Paymob');
    expect(row).toContain('PM-123');
    expect(row).not.toContain('provider confirmed');
    expect(row).not.toContain('Confirmed ');
    expect(row).not.toContain('Record manual refund');
    expect(row).not.toContain('Retry');
  });
  it('names every dialog by its own heading (a shared id would name the manual dialog "Refund this order?")', async () => {
    const { el } = await mount([refund()], vi.fn());

    const dialogs = [...el.querySelectorAll('dialog')];
    expect(dialogs.length).toBeGreaterThanOrEqual(3);
    const ids = dialogs.map((d) => d.getAttribute('aria-labelledby'));
    expect(new Set(ids).size).toBe(ids.length);
    for (const d of dialogs) {
      const label = d.querySelector(`[id="${d.getAttribute('aria-labelledby')}"]`);
      expect(label, 'aria-labelledby points inside its own dialog').not.toBeNull();
      expect(el.querySelectorAll(`[id="${d.getAttribute('aria-labelledby')}"]`).length).toBe(1);
    }
    // The dialog's description (what the action is for) is announced too, from its own paragraph.
    for (const d of dialogs) {
      const described = d.querySelector(`[id="${d.getAttribute('aria-describedby')}"]`);
      expect(described, 'aria-describedby points inside its own dialog').not.toBeNull();
    }
    const manual = dialogOf(el);
    expect(text(manual.querySelector(`[id="${manual.getAttribute('aria-labelledby')}"]`)!)).toBe(
      'Record a manual refund?',
    );
  });
});

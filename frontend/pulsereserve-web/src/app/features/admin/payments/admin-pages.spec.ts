import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import {
  AuditEntry,
  OperationsOrder,
  OperationsRefund,
  OutboxEvent,
  ReconciliationTask,
  WorkflowSummary,
} from '../../../core/api/admin-payments.models';
import { ApiError } from '../../../core/api/api-error';
import { formatMoney } from '../../../shared/format';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { Audit } from './audit';
import { Outbox } from './outbox';
import { Overview } from './overview';
import { Refunds } from './refunds';
import { Tasks } from './tasks';

const apiError = (status: number, title = 'X', message = 'Problem') =>
  new ApiError('problem', status, title, message, message);

const task = (over: Partial<ReconciliationTask> = {}): ReconciliationTask => ({
  id: 'aaaaaaaa-0000-4000-8000-000000000001',
  kind: 'FinalizeSuccessfulPayment',
  subjectId: 'bbbbbbbb-0000-4000-8000-000000000001',
  state: 'NeedsReview',
  attempts: 5,
  lastOutcome: 'ProviderError',
  lastError: 'Provider timeout',
  nextAttemptAtUtc: '2030-01-01T10:00:00Z',
  createdAtUtc: '2030-01-01T09:00:00Z',
  completedAtUtc: null,
  ...over,
});

const outbox = (over: Partial<OutboxEvent> = {}): OutboxEvent => ({
  id: 'cccccccc-0000-4000-8000-000000000001',
  type: 'NotifyOrderPaid',
  aggregateId: 'dddddddd-0000-4000-8000-000000000001',
  state: 'NeedsReview',
  attempts: 5,
  lastError: 'smtp down',
  createdAtUtc: '2030-01-01T09:00:00Z',
  processedAtUtc: null,
  nextAttemptAtUtc: '2030-01-01T10:00:00Z',
  updatedAtUtc: '2030-01-01T10:00:00Z',
  ...over,
});

const refund = (over: Partial<OperationsRefund> = {}): OperationsRefund => ({
  refundId: 'eeeeeeee-0000-4000-8000-000000000001',
  orderId: 'ffffffff-0000-4000-8000-000000000001',
  paymentAttemptId: 'p',
  amount: 91,
  currency: 'USD',
  reason: 'PaymentAfterSaleFailed',
  state: 'Failed',
  provider: 'Simulator',
  isSimulated: true,
  submissionNumber: 1,
  attempts: 3,
  lastError: 'declined',
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
  totalAmount: 120.5,
  currency: 'USD',
  createdAtUtc: '2030-01-01T09:00:00Z',
  updatedAtUtc: '2030-01-01T10:00:00Z',
  refundId: null,
  refundState: null,
  paymentProvider: 'Simulator',
  paymentIsSimulated: true,
  ...over,
});

const summary: WorkflowSummary = {
  reconciliationNeedsReview: 2,
  reconciliationPending: 3,
  refundsNeedingAttention: 1,
  refundsOpen: 4,
  outboxNeedsReview: 0,
  outboxPending: 6,
  ordersAwaitingRefund: 5,
  tasksByState: { NeedsReview: 2, Pending: 3, Done: 9 },
  refundsByState: { Failed: 1, Succeeded: 7 },
  outboxByState: { Pending: 6, Done: 11 },
};

interface Harness<T> {
  fixture: ComponentFixture<T>;
  el: HTMLElement;
  api: Record<string, ReturnType<typeof vi.fn>>;
  params: BehaviorSubject<Map<string, string>>;
  navigate: ReturnType<typeof vi.fn>;
  toast: ToastService;
}

async function mount<T>(
  component: Type<T>,
  api: Record<string, ReturnType<typeof vi.fn>>,
  query: Record<string, string> = {},
): Promise<Harness<T>> {
  const params = new BehaviorSubject(new Map(Object.entries(query)));
  const navigate = vi.fn(
    async (_c: unknown, extras: { queryParams: Record<string, string | null> }) => {
      const next = new Map(params.value);
      for (const [k, v] of Object.entries(extras.queryParams)) {
        if (v === null) next.delete(k);
        else next.set(k, v);
      }
      params.next(next);
      return true;
    },
  );
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: AdminPaymentsApiClient, useValue: { providers: () => of([]), ...api } },
      {
        provide: Router,
        useValue: { navigate, createUrlTree: vi.fn(), serializeUrl: vi.fn(), events: of() },
      },
      {
        provide: ActivatedRoute,
        useValue: { queryParamMap: params.asObservable().pipe((s) => mapParams(s)) },
      },
    ],
  });
  const fixture = TestBed.createComponent(component);
  await fixture.whenStable();
  return {
    fixture,
    el: fixture.nativeElement as HTMLElement,
    api,
    params,
    navigate,
    toast: TestBed.inject(ToastService),
  };
}

import { Observable, map } from 'rxjs';
function mapParams(source: Observable<Map<string, string>>) {
  return source.pipe(map((m) => ({ get: (k: string) => m.get(k) ?? null })));
}

const money = (n: number) => formatMoney(n, 'USD').replace(/\s+/g, ' ');
const text = (el: HTMLElement) => (el.textContent ?? '').replace(/\s+/g, ' ').trim();
const button = (el: HTMLElement, label: string) =>
  [...el.querySelectorAll('button')].find((b) => text(b).includes(label)) as HTMLButtonElement;
const dialogIsOpen = (el: HTMLElement) =>
  [...el.querySelectorAll('dialog')].some((d) => d.open || d.hasAttribute('open'));
const openDialog = (el: HTMLElement) =>
  [...el.querySelectorAll('dialog')].find(
    (d) => d.open || d.hasAttribute('open'),
  ) as HTMLDialogElement;

describe('Overview', () => {
  it('shows a loading state, then only the numbers the API returned', async () => {
    const pending = new Subject<WorkflowSummary>();
    const api = { overview: vi.fn(() => pending) };
    const h = await mount(Overview, api);
    expect(text(h.el)).toContain('Loading operations overview');
    pending.next(summary);
    await h.fixture.whenStable();
    const tiles = [...h.el.querySelectorAll('.tile')].map((t) => text(t as HTMLElement));
    expect(tiles.some((t) => t.includes('Reconciliation needs review') && t.includes('2'))).toBe(
      true,
    );
    expect(tiles.some((t) => t.includes('Orders awaiting a refund') && t.includes('5'))).toBe(true);
    expect(tiles.some((t) => t.includes('Outbox queued') && t.includes('6'))).toBe(true);
    expect(text(h.el)).toContain('Refunded (provider confirmed)');
  });

  it('highlights a needs-review tile only when its count is above zero', async () => {
    const h = await mount(Overview, { overview: vi.fn(() => of(summary)) });
    const attention = [...h.el.querySelectorAll('.tile--attention')].map((t) =>
      text(t as HTMLElement),
    );
    expect(attention.some((t) => t.includes('Outbox needs review'))).toBe(false);
    expect(attention.some((t) => t.includes('Reconciliation needs review'))).toBe(true);
  });

  it('shows the failure with a retry that reloads', async () => {
    const overview = vi
      .fn()
      .mockReturnValueOnce(
        throwError(() => new ApiError('network', 0, null, null, 'Could not reach the server.')),
      )
      .mockReturnValue(of(summary));
    const h = await mount(Overview, { overview });
    expect(text(h.el)).toContain('We could not load the overview');
    button(h.el, 'Try again').click();
    await h.fixture.whenStable();
    expect(overview).toHaveBeenCalledTimes(2);
    expect(text(h.el)).toContain('Needs attention');
  });

  it('process now: disables the button while running, then reports the server counts', async () => {
    const run = new Subject<unknown>();
    const api = { overview: vi.fn(() => of(summary)), processNow: vi.fn(() => run) };
    const h = await mount(Overview, api);
    button(h.el, 'Process now').click();
    await h.fixture.whenStable();
    expect(button(h.el, 'Processing…').disabled).toBe(true);
    button(h.el, 'Processing…').click();
    expect(api.processNow).toHaveBeenCalledTimes(1);

    run.next({
      tasksDiscovered: 4,
      tasksProcessed: 3,
      tasksFailed: 1,
      refundsProcessed: 2,
      refundsFailed: 0,
      outboxProcessed: 1,
      outboxFailed: 0,
    });
    run.complete();
    await h.fixture.whenStable();
    expect(text(h.el)).toContain('Pass finished with failures');
    expect(text(h.el)).toContain('6 item(s) processed and 1 failed');
    expect(text(h.el)).toContain('3 / 1');
    expect(api.overview).toHaveBeenCalledTimes(2);
  });

  it('process now: shows the server refusal', async () => {
    const api = {
      overview: vi.fn(() => of(summary)),
      processNow: vi.fn(() => throwError(() => apiError(403, 'Auth.Forbidden'))),
    };
    const h = await mount(Overview, api);
    button(h.el, 'Process now').click();
    await h.fixture.whenStable();
    expect(text(h.el)).toContain('The pass could not run');
    expect(text(h.el)).toContain('administrator access is required');
    expect(button(h.el, 'Process now').disabled).toBe(false);
  });
});

describe('Tasks', () => {
  const rows = [
    task(),
    task({
      id: 'aaaaaaaa-0000-4000-8000-000000000002',
      state: 'Pending',
      lastError: null,
      lastOutcome: null,
    }),
    task({ id: 'aaaaaaaa-0000-4000-8000-000000000003', state: 'Done', kind: 'ExpireOrder' }),
  ];

  it('offers re-queue only for Needs review and explains the rest', async () => {
    const h = await mount(Tasks, { tasks: vi.fn(() => of(rows)) });
    expect(h.el.querySelectorAll('tbody tr').length).toBe(3);
    expect([...h.el.querySelectorAll('tbody button')].map((b) => text(b as HTMLElement))).toEqual([
      'Re-queue task aaaaaaaa',
    ]);
    expect(text(h.el)).toContain('retried automatically');
    expect(text(h.el)).toContain('Finalize successful payment');
    expect(text(h.el)).toContain('Completed');
  });

  it('sends the state filter from the URL to the API and ignores an unknown one', async () => {
    const api = { tasks: vi.fn(() => of([])) };
    await mount(Tasks, api, { state: 'NeedsReview' });
    expect(api.tasks).toHaveBeenCalledWith('NeedsReview', 50);
    TestBed.resetTestingModule();
    const api2 = { tasks: vi.fn(() => of([])) };
    await mount(Tasks, api2, { state: 'Bogus', limit: '100' });
    expect(api2.tasks).toHaveBeenCalledWith(null, 100);
  });

  it('changing the filter updates the URL and reloads with it', async () => {
    const api = { tasks: vi.fn(() => of([])) };
    const h = await mount(Tasks, api);
    const select = h.el.querySelector('#tasks-state') as HTMLSelectElement;
    select.value = 'Pending';
    select.dispatchEvent(new Event('change'));
    await h.fixture.whenStable();
    expect(api.tasks).toHaveBeenLastCalledWith('Pending', 50);
    expect(text(h.el)).toContain('No reconciliation tasks');
  });

  it('search narrows only the loaded rows', async () => {
    const h = await mount(Tasks, { tasks: vi.fn(() => of(rows)) });
    const search = h.el.querySelector('#tasks-search') as HTMLInputElement;
    search.value = 'expire';
    search.dispatchEvent(new Event('input'));
    await h.fixture.whenStable();
    expect(h.el.querySelectorAll('tbody tr').length).toBe(1);
    expect(text(h.el)).toContain('Showing 1 of 3 loaded tasks');
  });

  it('asks before re-queueing, sends nothing until confirmed, and does not claim it is resolved', async () => {
    const api = { tasks: vi.fn(() => of(rows)), retryTask: vi.fn(() => of(undefined)) };
    const h = await mount(Tasks, api);
    button(h.el, 'Re-queue task').click();
    await h.fixture.whenStable();
    expect(dialogIsOpen(h.el)).toBe(true);
    expect(text(openDialog(h.el))).toContain(rows[0].id);
    expect(api.retryTask).not.toHaveBeenCalled();

    button(openDialog(h.el), 'Cancel').click();
    await h.fixture.whenStable();
    expect(dialogIsOpen(h.el)).toBe(false);
    expect(api.retryTask).not.toHaveBeenCalled();

    button(h.el, 'Re-queue task').click();
    await h.fixture.whenStable();
    button(openDialog(h.el), 'Re-queue task').click();
    await h.fixture.whenStable();
    expect(api.retryTask).toHaveBeenCalledWith(rows[0].id);
    expect(api.tasks).toHaveBeenCalledTimes(2);
    const toast = h.toast.toasts()[0];
    expect(toast.tone).toBe('success');
    expect(toast.text).toContain('not resolved yet');
  });

  it('a 409 closes the dialog, reloads and says the item is no longer eligible', async () => {
    const api = {
      tasks: vi.fn(() => of(rows)),
      retryTask: vi.fn(() => throwError(() => apiError(409, 'Operations.InvalidState'))),
    };
    const h = await mount(Tasks, api);
    button(h.el, 'Re-queue task').click();
    await h.fixture.whenStable();
    button(openDialog(h.el), 'Re-queue task').click();
    await h.fixture.whenStable();
    expect(dialogIsOpen(h.el)).toBe(false);
    expect(api.tasks).toHaveBeenCalledTimes(2);
    expect(h.toast.toasts()[0].tone).toBe('error');
    expect(h.toast.toasts()[0].text).toContain('no longer eligible');
  });

  it('a 403 keeps the dialog open with the refusal and does not reload', async () => {
    const api = {
      tasks: vi.fn(() => of(rows)),
      retryTask: vi.fn(() => throwError(() => apiError(403, 'Auth.Forbidden'))),
    };
    const h = await mount(Tasks, api);
    button(h.el, 'Re-queue task').click();
    await h.fixture.whenStable();
    button(openDialog(h.el), 'Re-queue task').click();
    await h.fixture.whenStable();
    expect(dialogIsOpen(h.el)).toBe(true);
    expect(text(openDialog(h.el))).toContain('administrator access is required');
    expect(api.tasks).toHaveBeenCalledTimes(1);
  });

  it('shows the load failure with retry', async () => {
    const tasks = vi
      .fn()
      .mockReturnValueOnce(throwError(() => new ApiError('network', 0, null, null, 'Offline.')))
      .mockReturnValue(of(rows));
    const h = await mount(Tasks, { tasks });
    expect(text(h.el)).toContain('We could not load reconciliation tasks');
    button(h.el, 'Try again').click();
    await h.fixture.whenStable();
    expect(h.el.querySelectorAll('tbody tr').length).toBe(3);
  });
});

describe('Outbox', () => {
  it('states at-least-once delivery and re-queues through the existing contract after confirmation', async () => {
    const api = {
      outbox: vi.fn(() =>
        of([
          outbox(),
          outbox({
            id: 'cccccccc-0000-4000-8000-000000000002',
            state: 'Done',
            processedAtUtc: '2030-01-01T11:00:00Z',
          }),
        ]),
      ),
      retryOutbox: vi.fn(() => of(undefined)),
    };
    const h = await mount(Outbox, api);
    expect(text(h.el)).toContain('at least once');
    expect(h.el.querySelectorAll('tbody button').length).toBe(1);
    button(h.el, 'Re-queue event').click();
    await h.fixture.whenStable();
    expect(text(openDialog(h.el))).toContain('repeat its effect');
    expect(api.retryOutbox).not.toHaveBeenCalled();
    button(openDialog(h.el), 'Re-queue event').click();
    await h.fixture.whenStable();
    expect(api.retryOutbox).toHaveBeenCalledWith('cccccccc-0000-4000-8000-000000000001');
    expect(h.toast.toasts()[0].text).toContain('not resolved yet');
  });

  it('shows an empty state when nothing matches', async () => {
    const h = await mount(Outbox, { outbox: vi.fn(() => of([])) });
    expect(text(h.el)).toContain('No outbox events');
  });
});

describe('Refunds', () => {
  const baseApi = (over: Record<string, ReturnType<typeof vi.fn>> = {}) => ({
    orders: vi.fn(() =>
      of([
        order(),
        order({
          orderId: '22222222-0000-4000-8000-000000000002',
          refundId: 'x',
          refundState: 'Pending',
        }),
      ]),
    ),
    refunds: vi.fn(() =>
      of([
        refund(),
        refund({ refundId: 'e2', state: 'Succeeded', completedAtUtc: '2030-01-02T00:00:00Z' }),
        refund({ refundId: 'e3', state: 'Pending' }),
        refund({ refundId: 'e4', state: 'NeedsReview' }),
      ]),
    ),
    requestRefund: vi.fn(() => of(refund({ state: 'Requested' }))),
    retryRefund: vi.fn(() => of(undefined)),
    ...over,
  });

  it('labels only a provider-confirmed refund as refunded and offers retry only where eligible', async () => {
    const h = await mount(Refunds, baseApi());
    const rowsText = [...h.el.querySelectorAll('section:nth-of-type(2) tbody tr')].map((r) =>
      text(r as HTMLElement),
    );
    expect(rowsText.filter((r) => r.includes('Refunded (provider confirmed)')).length).toBe(1);
    const retryButtons = [...h.el.querySelectorAll('tbody button')].filter((b) =>
      text(b as HTMLElement).startsWith('Retry'),
    );
    expect(retryButtons.length).toBe(2); // Failed and NeedsReview
    expect(text(h.el)).toContain('Simulated');
  });

  it('request refund: shows the exact order and amount, sends nothing until confirmed, never says completed', async () => {
    const api = baseApi();
    const h = await mount(Refunds, api);
    button(h.el, 'Request refund').click();
    await h.fixture.whenStable();
    const dialog = text(openDialog(h.el));
    expect(dialog).toContain('11111111-0000-4000-8000-000000000001');
    expect(dialog).toContain(money(120.5));
    expect(dialog).toContain('provider confirms');
    expect(api.requestRefund).not.toHaveBeenCalled();

    button(openDialog(h.el), 'Request refund').click();
    await h.fixture.whenStable();
    expect(api.requestRefund).toHaveBeenCalledWith('11111111-0000-4000-8000-000000000001');
    const toast = h.toast.toasts()[0];
    expect(toast.tone).toBe('success');
    expect(toast.text).toContain('not complete until the provider confirms');
    expect(api.orders).toHaveBeenCalledTimes(2);
    expect(api.refunds).toHaveBeenCalledTimes(2);
  });

  it('does not offer a second refund for an order that already has one', async () => {
    const h = await mount(Refunds, baseApi());
    expect(text(h.el)).toContain('Refund already created');
    expect(
      [...h.el.querySelectorAll('tbody button')].filter((b) =>
        text(b as HTMLElement).startsWith('Request refund'),
      ).length,
    ).toBe(1);
  });

  it('retry refund: explains the consequence and calls the retry route only after confirmation', async () => {
    const api = baseApi();
    const h = await mount(Refunds, api);
    const retry = [...h.el.querySelectorAll('tbody button')].find((b) =>
      text(b as HTMLElement).startsWith('Retry'),
    ) as HTMLButtonElement;
    retry.click();
    await h.fixture.whenStable();
    expect(text(openDialog(h.el))).toContain('new submission');
    expect(text(openDialog(h.el))).toContain(money(91));
    expect(api.retryRefund).not.toHaveBeenCalled();
    button(openDialog(h.el), 'Retry refund').click();
    await h.fixture.whenStable();
    expect(api.retryRefund).toHaveBeenCalledWith('eeeeeeee-0000-4000-8000-000000000001');
    expect(h.toast.toasts()[0].text).toContain('not complete until the provider confirms');
  });

  it('shows an inline failure when the order list cannot load, without hiding the refund records', async () => {
    const h = await mount(
      Refunds,
      baseApi({
        orders: vi.fn(() => throwError(() => new ApiError('network', 0, null, null, 'Offline.'))),
      }),
    );
    expect(text(h.el)).toContain('We could not load orders awaiting a refund');
    expect(h.el.querySelectorAll('tbody tr').length).toBe(4);
  });
});

describe('Audit', () => {
  const entry = (over: Partial<AuditEntry> = {}): AuditEntry => ({
    id: 'a1',
    actorUserId: '99999999-0000-4000-8000-000000000001',
    action: 'RetryRefund',
    subjectType: 'Refund',
    subjectId: 'eeeeeeee-0000-4000-8000-000000000001',
    detail: 'Failed -> Requested',
    createdAtUtc: '2030-01-01T10:00:00Z',
    ...over,
  });

  it('renders readable, read-only entries with no actions', async () => {
    const h = await mount(Audit, {
      audit: vi.fn(() => of([entry(), entry({ id: 'a2', action: 'Mystery', detail: null })])),
    });
    const t = text(h.el);
    expect(t).toContain('Retried a refund');
    expect(t).toContain('Failed -> Requested');
    expect(t).toContain('99999999');
    expect(t).toContain('Mystery');
    expect(h.el.querySelectorAll('tbody button').length).toBe(0);
    expect(t).toContain('cannot be edited or deleted');
  });

  it('shows empty and error states', async () => {
    const empty = await mount(Audit, { audit: vi.fn(() => of([])) });
    expect(text(empty.el)).toContain('No audit entries yet');
    TestBed.resetTestingModule();
    const failed = await mount(Audit, {
      audit: vi.fn(() => throwError(() => apiError(403, 'Auth.Forbidden', 'Forbidden'))),
    });
    expect(text(failed.el)).toContain('We could not load the audit log');
  });
});

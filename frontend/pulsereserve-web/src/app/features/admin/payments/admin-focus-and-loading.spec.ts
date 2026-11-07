import { Type, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { BehaviorSubject, Observable, Subject, map, of, throwError } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import {
  OperationsOrder,
  OperationsRefund,
  OutboxEvent,
  ReconciliationTask,
} from '../../../core/api/admin-payments.models';
import { ApiError } from '../../../core/api/api-error';
import { AuthService } from '../../../core/auth/auth.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { AdminLayout } from './admin-layout';
import { Audit } from './audit';
import { Outbox } from './outbox';
import { Overview } from './overview';
import { Refunds } from './refunds';
import { Tasks } from './tasks';

const apiError = (status: number, title = 'X') =>
  new ApiError('problem', status, title, null, title);

const task: ReconciliationTask = {
  id: 'aaaaaaaa-0000-4000-8000-000000000001',
  kind: 'FinalizeSuccessfulPayment',
  subjectId: 'bbbbbbbb-0000-4000-8000-000000000001',
  state: 'NeedsReview',
  attempts: 5,
  lastOutcome: null,
  lastError: null,
  nextAttemptAtUtc: '2030-01-01T10:00:00Z',
  createdAtUtc: '2030-01-01T09:00:00Z',
  completedAtUtc: null,
};
const queued: ReconciliationTask = { ...task, state: 'Pending' };
const event: OutboxEvent = {
  id: 'cccccccc-0000-4000-8000-000000000001',
  type: 'NotifyOrderPaid',
  aggregateId: 'dddddddd-0000-4000-8000-000000000001',
  state: 'NeedsReview',
  attempts: 5,
  lastError: null,
  createdAtUtc: '2030-01-01T09:00:00Z',
  processedAtUtc: null,
  nextAttemptAtUtc: '2030-01-01T10:00:00Z',
  updatedAtUtc: '2030-01-01T10:00:00Z',
};
const refund: OperationsRefund = {
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
  lastError: null,
  requestedBy: null,
  createdAtUtc: '2030-01-01T09:00:00Z',
  completedAtUtc: null,
  nextAttemptAtUtc: '2030-01-01T10:00:00Z',
  updatedAtUtc: '2030-01-01T10:00:00Z',
};
const order: OperationsOrder = {
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
};

type Api = Record<string, ReturnType<typeof vi.fn>>;

async function mount<T>(
  component: Type<T>,
  api: Api,
  fakeRouter = true,
): Promise<ComponentFixture<T>> {
  const params = new BehaviorSubject(new Map<string, string>());
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: AdminPaymentsApiClient, useValue: { providers: () => of([]), ...api } },
      ...(fakeRouter
        ? [
            {
              provide: Router,
              useValue: { navigate: vi.fn(async () => true), createUrlTree: vi.fn(), events: of() },
            },
          ]
        : []),
      {
        provide: ActivatedRoute,
        useValue: {
          queryParamMap: params.pipe(map((m) => ({ get: (k: string) => m.get(k) ?? null }))),
        },
      },
    ],
  });
  const fixture = TestBed.createComponent(component);
  // Focus only exists for elements in the document.
  document.body.appendChild(fixture.nativeElement as HTMLElement);
  await fixture.whenStable();
  return fixture;
}

const el = (f: ComponentFixture<unknown>) => f.nativeElement as HTMLElement;
const text = (e: Element) => (e.textContent ?? '').replace(/\s+/g, ' ').trim();
const btn = (root: Element, label: string) =>
  [...root.querySelectorAll('button')].find((b) => text(b).startsWith(label)) as HTMLButtonElement;
const openDialog = (root: Element) =>
  [...root.querySelectorAll('dialog')].find((d) => d.open || d.hasAttribute('open')) as HTMLElement;
const openDialogs = (root: Element) =>
  [...root.querySelectorAll('dialog')].filter((d) => d.open || d.hasAttribute('open')).length;

afterEach(() => {
  document.body.innerHTML = '';
});

describe('focus after a dialog action', () => {
  const rowButton = (root: Element, label: string) =>
    [...root.querySelectorAll('tbody button')].find((b) =>
      text(b).startsWith(label),
    ) as HTMLButtonElement;

  async function openAndConfirm(
    fixture: ComponentFixture<unknown>,
    rowLabel: string,
    confirmLabel: string,
  ) {
    const opener = rowButton(el(fixture), rowLabel);
    opener.focus();
    opener.click();
    await fixture.whenStable();
    btn(openDialog(el(fixture)), confirmLabel).click();
    await fixture.whenStable();
  }

  it('re-queueing a task moves focus to the section heading instead of leaving it on body', async () => {
    const api = {
      tasks: vi
        .fn()
        .mockReturnValueOnce(of([task]))
        .mockReturnValue(of([queued])),
      retryTask: vi.fn(() => of(undefined)),
    };
    const f = await mount(Tasks, api);
    await openAndConfirm(f, 'Re-queue', 'Re-queue task');
    expect(api.retryTask).toHaveBeenCalledTimes(1);
    expect(openDialogs(el(f))).toBe(0);
    expect(document.activeElement?.id).toBe('tasks-heading');
  });

  it('the success toast says the task was only re-queued, never resolved', async () => {
    const f = await mount(Tasks, {
      tasks: vi
        .fn()
        .mockReturnValueOnce(of([task]))
        .mockReturnValue(of([queued])),
      retryTask: vi.fn(() => of(undefined)),
    });
    await openAndConfirm(f, 'Re-queue', 'Re-queue task');
    const toast = TestBed.inject(ToastService).toasts()[0];
    expect(toast.tone).toBe('success');
    expect(toast.text).toContain('not resolved yet');
    expect(toast.text.toLowerCase()).not.toMatch(/\b(resolved|completed|reconciled)\b(?! yet)/);
  });

  it('outbox re-queue restores focus to its heading', async () => {
    const f = await mount(Outbox, {
      outbox: vi
        .fn()
        .mockReturnValueOnce(of([event]))
        .mockReturnValue(of([{ ...event, state: 'Pending' }])),
      retryOutbox: vi.fn(() => of(undefined)),
    });
    await openAndConfirm(f, 'Re-queue', 'Re-queue event');
    expect(document.activeElement?.id).toBe('outbox-heading');
  });

  it('a refund request focuses the "awaiting a refund" heading, a refund retry the records heading', async () => {
    const requested = { ...refund, state: 'Requested' };
    const api = {
      orders: vi
        .fn()
        .mockReturnValueOnce(of([order]))
        .mockReturnValue(of([{ ...order, refundId: 'x', refundState: 'Requested' }])),
      refunds: vi
        .fn()
        .mockReturnValueOnce(of([refund]))
        .mockReturnValueOnce(of([refund]))
        .mockReturnValue(of([requested])),
      requestRefund: vi.fn(() => of(requested)),
      retryRefund: vi.fn(() => of(undefined)),
    };
    const f = await mount(Refunds, api);
    await openAndConfirm(f, 'Request refund', 'Request refund');
    expect(document.activeElement?.id).toBe('awaiting-heading');

    await openAndConfirm(f, 'Retry', 'Retry refund');
    expect(api.retryRefund).toHaveBeenCalledTimes(1);
    expect(document.activeElement?.id).toBe('refunds-heading');
  });

  it('refund toasts never announce completion for a request the backend only accepted', async () => {
    const f = await mount(Refunds, {
      orders: vi.fn(() => of([order])),
      refunds: vi.fn(() => of([])),
      requestRefund: vi.fn(() => of({ ...refund, state: 'Requested' })),
    });
    await openAndConfirm(f, 'Request refund', 'Request refund');
    const text0 = TestBed.inject(ToastService).toasts()[0].text;
    expect(text0).toContain('not complete until the provider confirms');
    expect(text0).not.toContain('Refunded');
  });

  it('a 409 closes the dialog, announces the conflict as an error and still restores focus', async () => {
    const f = await mount(Tasks, {
      tasks: vi
        .fn()
        .mockReturnValueOnce(of([task]))
        .mockReturnValue(of([queued])),
      retryTask: vi.fn(() => throwError(() => apiError(409, 'Operations.InvalidState'))),
    });
    await openAndConfirm(f, 'Re-queue', 'Re-queue task');
    expect(openDialogs(el(f))).toBe(0);
    const toast = TestBed.inject(ToastService).toasts()[0];
    expect(toast.tone).toBe('error'); // errors use role="alert" in the toast host
    expect(document.activeElement?.id).toBe('tasks-heading');
  });

  it('a 403 keeps the dialog open with an alert and does not move focus away from it', async () => {
    const f = await mount(Tasks, {
      tasks: vi.fn(() => of([task])),
      retryTask: vi.fn(() => throwError(() => apiError(403, 'Auth.Forbidden'))),
    });
    await openAndConfirm(f, 'Re-queue', 'Re-queue task');
    const dialog = openDialog(el(f));
    expect(dialog).toBeTruthy();
    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain(
      'administrator access is required',
    );
    expect(document.activeElement?.id).not.toBe('tasks-heading');
  });

  it('Escape cancels without a request and leaves focus on the control that opened the dialog', async () => {
    const api = { tasks: vi.fn(() => of([task])), retryTask: vi.fn(() => of(undefined)) };
    const f = await mount(Tasks, api);
    const opener = rowButton(el(f), 'Re-queue');
    opener.focus();
    opener.click();
    await f.whenStable();
    const dialog = openDialog(el(f));
    const escape = new Event('cancel', { cancelable: true });
    dialog.dispatchEvent(escape);
    await f.whenStable();
    expect(escape.defaultPrevented).toBe(true);
    expect(api.retryTask).not.toHaveBeenCalled();
    expect(openDialogs(el(f))).toBe(0);
    expect(document.activeElement?.id).not.toBe('tasks-heading');
  });

  it('does not steal focus when the person already moved it somewhere else', async () => {
    const pending = new Subject<undefined>();
    const f = await mount(Tasks, {
      tasks: vi.fn(() => of([task])),
      retryTask: vi.fn(() => pending),
    });
    const opener = rowButton(el(f), 'Re-queue');
    opener.click();
    await f.whenStable();
    btn(openDialog(el(f)), 'Re-queue task').click();
    const search = el(f).querySelector('#tasks-search') as HTMLInputElement;
    search.focus();
    pending.next(undefined);
    pending.complete();
    await f.whenStable();
    expect(document.activeElement).toBe(search);
  });

  it('process now returns focus to its button and announces the real outcome', async () => {
    const run = new Subject<unknown>();
    const f = await mount(
      Overview,
      {
        overview: vi.fn(() => of({ tasksByState: {}, refundsByState: {}, outboxByState: {} })),
        processNow: vi.fn(() => run),
      },
      false,
    );
    const button = el(f).querySelector('#process-now-button') as HTMLButtonElement;
    button.focus();
    button.click();
    await f.whenStable();
    button.blur(); // a disabled button cannot keep focus in every browser
    run.next({
      tasksDiscovered: 1,
      tasksProcessed: 1,
      tasksFailed: 0,
      refundsProcessed: 0,
      refundsFailed: 0,
      outboxProcessed: 0,
      outboxFailed: 0,
    });
    run.complete();
    await f.whenStable();
    expect(document.activeElement?.id).toBe('process-now-button');
    expect(el(f).querySelector('[role="status"]')).toBeTruthy();
    expect(text(el(f))).toContain('1 item(s) processed and 0 failed');
  });

  it('process now announces a failure with role="alert"', async () => {
    const f = await mount(
      Overview,
      {
        overview: vi.fn(() => of({ tasksByState: {}, refundsByState: {}, outboxByState: {} })),
        processNow: vi.fn(() => throwError(() => apiError(500, 'Server.Error'))),
      },
      false,
    );
    (el(f).querySelector('#process-now-button') as HTMLButtonElement).click();
    await f.whenStable();
    expect(el(f).querySelector('app-alert[role="alert"]')).toBeTruthy();
    expect(document.activeElement?.id).toBe('process-now-button');
  });
});

describe('session end', () => {
  it('moves focus to the sign-in prompt when the console is replaced by it', async () => {
    const sessionExpired = signal(false);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { sessionExpired, user: signal(null) } },
      ],
    });
    const f = TestBed.createComponent(AdminLayout);
    document.body.appendChild(f.nativeElement as HTMLElement);
    await f.whenStable();
    (f.nativeElement as HTMLElement).querySelector<HTMLElement>('nav a')?.focus();
    sessionExpired.set(true);
    await f.whenStable();
    expect(document.activeElement?.id).toBe('session-ended');
  });
});

describe('loading states', () => {
  const skeletons = (root: Element) => root.querySelectorAll('.skeleton').length;

  async function pendingThenReady<T, R>(
    component: Type<T>,
    apiFactory: (pending: Subject<R>) => Api,
    ready: R,
    afterLoad: (root: HTMLElement) => void,
  ) {
    const pending = new Subject<R>();
    const f = await mount(component, apiFactory(pending));
    const root = el(f);
    return {
      f,
      root,
      finish: async () => {
        pending.next(ready);
        pending.complete();
        await f.whenStable();
        afterLoad(root);
      },
    };
  }

  it('overview: skeleton tiles are hidden from assistive tech while one polite status announces loading', async () => {
    const pending = new Subject<unknown>();
    const f = await mount(
      Overview,
      { overview: vi.fn(() => pending as Observable<unknown>) },
      false,
    );
    const s = {
      root: el(f),
      finish: async () => {
        pending.next({ tasksByState: {}, refundsByState: {}, outboxByState: {} });
        pending.complete();
        await f.whenStable();
      },
    };
    expect(skeletons(s.root)).toBeGreaterThan(0);
    expect(s.root.querySelector('.tiles[aria-hidden="true"]')).toBeTruthy();
    expect(text(s.root.querySelector('[role="status"].visually-hidden')!)).toContain('Loading');
    expect(s.root.querySelector('.tile--attention')).toBeNull();
    await s.finish();
    expect(s.root.querySelector('[role="status"].visually-hidden')).toBeNull();
  });

  it.each([
    ['tasks', Tasks, 'tasks', 'Loading reconciliation tasks'],
    ['outbox', Outbox, 'outbox', 'Loading outbox events'],
    ['audit', Audit, 'audit', 'Loading the audit log'],
  ] as const)(
    '%s: skeleton rows while pending, replaced by the empty state once loaded',
    async (_n, component, method, message) => {
      const s = await pendingThenReady<unknown, unknown[]>(
        component as Type<unknown>,
        (p) => ({ [method]: vi.fn(() => p) }),
        [],
        (root) => expect(skeletons(root)).toBe(0),
      );
      expect(skeletons(s.root)).toBeGreaterThan(0);
      expect(s.root.querySelector('.panel[aria-hidden="true"]')).toBeTruthy();
      expect(text(s.root.querySelector('[role="status"].visually-hidden')!)).toContain(message);
      expect(s.root.querySelector('table')).toBeNull();
      await s.finish();
      expect(s.root.querySelector('app-empty-state')).toBeTruthy();
    },
  );

  it('refunds: both sections show their own skeleton and resolve independently', async () => {
    const orders = new Subject<OperationsOrder[]>();
    const refunds = new Subject<OperationsRefund[]>();
    const f = await mount(Refunds, {
      orders: vi.fn(() => orders),
      refunds: vi.fn(() => refunds),
    });
    const root = el(f);
    expect(root.querySelectorAll('.panel[aria-hidden="true"]').length).toBe(2);
    expect(
      [...root.querySelectorAll('[role="status"].visually-hidden')].map((e) => text(e)),
    ).toEqual(['Loading orders awaiting a refund…', 'Loading refunds…']);

    orders.next([order]);
    orders.complete();
    await f.whenStable();
    expect(root.querySelectorAll('.panel[aria-hidden="true"]').length).toBe(1);
    expect(root.querySelector('tbody button')).toBeTruthy();

    refunds.next([refund]);
    refunds.complete();
    await f.whenStable();
    expect(skeletons(root)).toBe(0);
  });

  it('reloading after an action shows the skeleton again until the new data arrives', async () => {
    const second = new Subject<ReconciliationTask[]>();
    const tasks = vi
      .fn()
      .mockReturnValueOnce(of([task]))
      .mockReturnValue(second);
    const f = await mount(Tasks, { tasks, retryTask: vi.fn(() => of(undefined)) });
    const opener = [...el(f).querySelectorAll('tbody button')].find((b) =>
      text(b).startsWith('Re-queue'),
    ) as HTMLButtonElement;
    opener.click();
    await f.whenStable();
    btn(openDialog(el(f)), 'Re-queue task').click();
    await f.whenStable();
    expect(skeletons(el(f))).toBeGreaterThan(0);
    second.next([]);
    second.complete();
    await f.whenStable();
    expect(skeletons(el(f))).toBe(0);
  });
});

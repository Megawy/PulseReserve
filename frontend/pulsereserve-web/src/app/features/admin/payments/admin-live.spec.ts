import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { Subject, map, of, throwError } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { ReconciliationTask, WorkflowSummary } from '../../../core/api/admin-payments.models';
import { ApiError } from '../../../core/api/api-error';
import { LIVE_REFRESH_DEBOUNCE_MS } from '../../../core/live/live-refresh';
import { LiveArea, LiveNotice } from '../../../core/live/live.models';
import { LIVE_TIMING, LiveService } from '../../../core/live/live.service';
import { Overview } from './overview';
import { Tasks } from './tasks';

const task: ReconciliationTask = {
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
};

const summary = (needsReview: number): WorkflowSummary => ({
  reconciliationNeedsReview: needsReview,
  reconciliationPending: 0,
  refundsNeedingAttention: 0,
  refundsOpen: 0,
  outboxNeedsReview: 0,
  outboxPending: 0,
  ordersAwaitingRefund: 0,
  tasksByState: {},
  refundsByState: {},
  outboxByState: {},
});

const area = (a: LiveArea): LiveNotice => ({ kind: 'admin', area: a });
const text = (el: HTMLElement) => (el.textContent ?? '').replace(/\s+/g, ' ').trim();
const networkError = () => new ApiError('network', 0, null, null, 'Could not reach the server.');
const DEBOUNCE_MS = 5;

describe('administrator pages with live updates', () => {
  let notices: Subject<LiveNotice>;
  let nowMs: number;

  async function mount<T>(
    component: Type<T>,
    api: Record<string, ReturnType<typeof vi.fn>>,
  ): Promise<ComponentFixture<T>> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AdminPaymentsApiClient, useValue: { providers: () => of([]), ...api } },
        {
          provide: Router,
          useValue: {
            navigate: vi.fn(),
            createUrlTree: vi.fn(),
            serializeUrl: vi.fn(),
            events: of(),
          },
        },
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: of(new Map<string, string>()).pipe(
              map((m) => ({ get: (k: string) => m.get(k) ?? null })),
            ),
          },
        },
        { provide: LiveService, useValue: { notices: notices.asObservable() } },
        { provide: LIVE_TIMING, useValue: { now: () => nowMs } },
        { provide: LIVE_REFRESH_DEBOUNCE_MS, useValue: DEBOUNCE_MS },
      ],
    });
    const fixture = TestBed.createComponent(component);
    await fixture.whenStable();
    return fixture;
  }

  /** Waits past the debounce so a read that was going to happen has happened, then lets the page render. */
  async function pastDebounce(fixture: ComponentFixture<unknown>): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, DEBOUNCE_MS * 6));
    await fixture.whenStable();
  }

  beforeEach(() => {
    notices = new Subject<LiveNotice>();
    nowMs = 1000;
  });

  describe('a list', () => {
    it('re-reads when its own area changes and keeps the rows on screen while it does', async () => {
      const reload = new Subject<ReconciliationTask[]>();
      const tasks = vi
        .fn()
        .mockReturnValueOnce(of([task]))
        .mockReturnValueOnce(reload);
      const fixture = await mount(Tasks, { tasks });
      const el = fixture.nativeElement as HTMLElement;
      expect(text(el)).toContain('Provider timeout');

      notices.next(area('tasks'));
      await pastDebounce(fixture);

      expect(tasks).toHaveBeenCalledTimes(2);
      expect(text(el)).toContain('Provider timeout'); // no skeleton in between
      expect(text(el)).not.toContain('Loading reconciliation tasks');

      reload.next([{ ...task, lastError: 'Provider refused' }]);
      await fixture.whenStable();
      expect(text(el)).toContain('Provider refused');
    });

    it('ignores the areas that are not its own', async () => {
      const tasks = vi.fn(() => of([task]));
      const fixture = await mount(Tasks, { tasks });

      for (const a of [
        'orders',
        'refunds',
        'exceptions',
        'anomalies',
        'outbox',
        'overview',
      ] as const) {
        notices.next(area(a));
      }
      notices.next({
        kind: 'order',
        orderId: 'o',
        reservationId: 'r',
        status: 'Paid',
        paymentStatus: null,
        ticketCount: 0,
      });
      await pastDebounce(fixture);

      expect(tasks).toHaveBeenCalledTimes(1);
    });

    it('keeps the rows when a live read fails', async () => {
      const tasks = vi
        .fn()
        .mockReturnValueOnce(of([task]))
        .mockReturnValueOnce(throwError(() => networkError()));
      const fixture = await mount(Tasks, { tasks });
      const el = fixture.nativeElement as HTMLElement;

      notices.next(area('tasks'));
      await pastDebounce(fixture);

      expect(tasks).toHaveBeenCalledTimes(2);
      expect(text(el)).toContain('Provider timeout');
      expect(text(el)).not.toContain('Could not reach the server');
    });

    it('re-reads after an announced gap', async () => {
      const tasks = vi.fn(() => of([task]));
      const fixture = await mount(Tasks, { tasks });

      notices.next({ kind: 'resync', reason: 'connected', at: 5000 });
      await pastDebounce(fixture);

      expect(tasks).toHaveBeenCalledTimes(2);
    });

    it('stops listening when the page is destroyed', async () => {
      const tasks = vi.fn(() => of([task]));
      const fixture = await mount(Tasks, { tasks });
      fixture.destroy();

      notices.next(area('tasks'));
      await new Promise((resolve) => setTimeout(resolve, DEBOUNCE_MS * 6));

      expect(tasks).toHaveBeenCalledTimes(1);
    });
  });

  describe('the overview', () => {
    const tile = (el: HTMLElement) =>
      [...el.querySelectorAll('.tile')]
        .map((t) => text(t as HTMLElement))
        .find((t) => t.includes('Reconciliation needs review')) ?? '';

    it('updates its counts when the overview changes, without showing a loading state', async () => {
      const overview = vi
        .fn()
        .mockReturnValueOnce(of(summary(2)))
        .mockReturnValueOnce(of(summary(7)));
      const fixture = await mount(Overview, { overview });
      const el = fixture.nativeElement as HTMLElement;
      expect(tile(el)).toContain('2');

      notices.next(area('overview'));
      await pastDebounce(fixture);

      expect(overview).toHaveBeenCalledTimes(2);
      expect(tile(el)).toContain('7');
    });

    it('ignores other areas', async () => {
      const overview = vi.fn(() => of(summary(2)));
      const fixture = await mount(Overview, { overview });

      notices.next(area('tasks'));
      await pastDebounce(fixture);

      expect(overview).toHaveBeenCalledTimes(1);
    });

    it('keeps the tiles when a live read fails', async () => {
      const overview = vi
        .fn()
        .mockReturnValueOnce(of(summary(2)))
        .mockReturnValueOnce(throwError(() => networkError()));
      const fixture = await mount(Overview, { overview });
      const el = fixture.nativeElement as HTMLElement;

      notices.next(area('overview'));
      await pastDebounce(fixture);

      expect(overview).toHaveBeenCalledTimes(2);
      expect(el.querySelectorAll('.tile').length).toBeGreaterThan(0);
      expect(text(el)).not.toContain('Could not reach the server');
    });
  });
});

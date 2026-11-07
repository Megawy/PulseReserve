import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { LiveNotice } from './live.models';
import { LIVE_TIMING, LiveService } from './live.service';
import { LiveRefresh, createLiveRefresh } from './live-refresh';

const order = (orderId = 'o1'): LiveNotice => ({
  kind: 'order',
  orderId,
  reservationId: 'r1',
  status: 'Paid',
  paymentStatus: 'Succeeded',
  ticketCount: 0,
});
const resync = (at: number): LiveNotice => ({ kind: 'resync', reason: 'connected', at });

describe('createLiveRefresh', () => {
  let notices: Subject<LiveNotice>;
  let nowMs: number;
  /** One entry per read the page was asked to make: true when a gap was announced, false for a plain change. */
  let reads: boolean[];

  function make(): LiveRefresh {
    return TestBed.runInInjectionContext(() =>
      createLiveRefresh({
        matches: (n) => n.kind === 'order' && n.orderId === 'o1',
        refresh: (full) => reads.push(full),
        debounceMs: 100,
      }),
    );
  }

  beforeEach(() => {
    vi.useFakeTimers();
    notices = new Subject<LiveNotice>();
    nowMs = 1000;
    reads = [];
    TestBed.configureTestingModule({
      providers: [
        { provide: LiveService, useValue: { notices: notices.asObservable() } },
        { provide: LIVE_TIMING, useValue: { now: () => nowMs } },
      ],
    });
  });

  afterEach(() => vi.useRealTimers());

  it('turns a burst of matching notices into one read after the quiet period', () => {
    make();
    for (let i = 0; i < 5; i++) notices.next(order());

    expect(reads).toEqual([]);
    vi.advanceTimersByTime(99);
    expect(reads).toEqual([]);
    vi.advanceTimersByTime(1);

    expect(reads).toEqual([false]);
  });

  it('ignores notices that are not about this page', () => {
    make();
    notices.next(order('someone-elses'));
    notices.next({ kind: 'admin', area: 'orders' });
    notices.next({ kind: 'availability', eventId: 'e1', seats: [] });

    vi.advanceTimersByTime(1000);

    expect(reads).toEqual([]);
  });

  it('reads again after a gap only when the data on screen was requested no later than the gap', () => {
    const live = make();
    live.markRequested(); // requested at 1000
    notices.next(resync(999)); // the gap was announced before the data was requested: it cannot have missed anything
    vi.advanceTimersByTime(500);
    expect(reads).toEqual([]);

    notices.next(resync(1000)); // the same instant: judged stale, because a read cannot be proven to have come after
    vi.advanceTimersByTime(100);
    expect(reads).toEqual([true]);

    nowMs = 2000;
    live.markRequested();
    notices.next(resync(1500)); // older than the latest request again
    vi.advanceTimersByTime(500);
    expect(reads).toEqual([true]);

    notices.next(resync(2500)); // announced after the latest request
    vi.advanceTimersByTime(100);
    expect(reads).toEqual([true, true]);
  });

  it('reads again after a gap when nothing was ever requested', () => {
    make();
    notices.next(resync(5));
    vi.advanceTimersByTime(100);

    expect(reads).toEqual([true]);
  });

  it('reports a gap even when a matching notice started the wait first', () => {
    make();
    notices.next(order());
    notices.next(resync(1000));
    vi.advanceTimersByTime(100);

    expect(reads).toEqual([true]);
  });

  it('forgets a gap once it has been read, so the next plain notice is a plain read', () => {
    make();
    notices.next(resync(1000));
    vi.advanceTimersByTime(100);
    notices.next(order());
    vi.advanceTimersByTime(100);

    expect(reads).toEqual([true, false]);
  });

  it('stops listening and cancels a pending read when its owner is destroyed', () => {
    make();
    notices.next(order());

    TestBed.resetTestingModule();
    vi.advanceTimersByTime(1000);
    notices.next(order());
    vi.advanceTimersByTime(1000);

    expect(reads).toEqual([]);
  });
});

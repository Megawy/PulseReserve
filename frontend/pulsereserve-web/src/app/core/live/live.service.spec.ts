import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService, AuthUser } from '../auth/auth.service';
import { LIVE_CONNECTION_FACTORY, LiveConnectionLike } from './live-connection';
import { LIVE_TIMING, LiveService, LiveTiming } from './live.service';
import { LiveNotice } from './live.models';

const E1 = '00000000-0000-4000-8000-0000000000e1';
const E2 = '00000000-0000-4000-8000-0000000000e2';
const EPOCH_A = '11111111-1111-4111-8111-111111111111';
const EPOCH_B = '22222222-2222-4222-8222-222222222222';
const SEAT = '00000000-0000-4000-8000-000000000001';

/** A scripted hub connection: the test decides what Hello answers, which calls fail, and when it drops. */
class FakeConnection implements LiveConnectionLike {
  readonly handlers = new Map<string, (body: unknown) => void>();
  readonly invocations: { method: string; args: unknown[] }[] = [];
  closeHandler: ((error?: Error) => void) | null = null;
  startError: unknown = null;
  helloEpoch = EPOCH_A;
  admin = false;
  readonly failing = new Map<string, unknown>();
  stopped = false;

  start(): Promise<void> {
    return this.startError ? Promise.reject(this.startError) : Promise.resolve();
  }

  stop(): Promise<void> {
    this.stopped = true;
    return Promise.resolve();
  }

  invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.invocations.push({ method, args });
    const failure = this.failing.get(method);
    if (failure) {
      return Promise.reject(failure);
    }
    if (method === 'Hello') {
      return Promise.resolve({
        epoch: this.helloEpoch,
        serverTimeUtc: '2030-01-01T10:00:00Z',
        authenticated: true,
        admin: this.admin,
        maxConnectionSeconds: 600,
      } as T);
    }
    return Promise.resolve(undefined as T);
  }

  on(method: string, handler: (body: unknown) => void): void {
    this.handlers.set(method, handler);
  }

  onClose(handler: (error?: Error) => void): void {
    this.closeHandler = handler;
  }

  push(method: string, body: unknown): void {
    this.handlers.get(method)?.(body);
  }

  drop(): void {
    this.closeHandler?.();
  }

  calls(method: string): unknown[][] {
    return this.invocations.filter((i) => i.method === method).map((i) => i.args);
  }
}

describe('LiveService', () => {
  let connections: FakeConnection[];
  let created: FakeConnection[];
  let nowMs: number;
  let random: number;
  let user: ReturnType<typeof signal<AuthUser | null>>;
  let live: LiveService;
  let notices: LiveNotice[];

  const alice: AuthUser = { id: 'alice', email: 'a@example.com', displayName: 'Alice' };
  const bob: AuthUser = { id: 'bob', email: 'b@example.com', displayName: 'Bob' };

  const timing = (): LiveTiming => ({
    backoffBaseMs: 1000,
    backoffMaxMs: 30_000,
    jitterRatio: 0.25,
    rotationDelayMs: 300,
    stableAfterMs: 30_000,
    unavailableRetryMs: 300_000,
    degradedAfterMs: 2000,
    now: () => nowMs,
    random: () => random,
  });

  /** Lets the awaited calls inside the service run (without moving timers). */
  const settle = () => vi.advanceTimersByTimeAsync(0);
  const advance = (ms: number) => {
    nowMs += ms;
    return vi.advanceTimersByTimeAsync(ms);
  };

  function setup(initial: AuthUser | null = null) {
    user = signal<AuthUser | null>(initial);
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { user } },
        { provide: LIVE_TIMING, useValue: timing() },
        {
          provide: LIVE_CONNECTION_FACTORY,
          useValue: () => {
            const next = connections.shift() ?? new FakeConnection();
            created.push(next);
            return next;
          },
        },
      ],
    });
    live = TestBed.inject(LiveService);
    live.notices.subscribe((n) => notices.push(n));
  }

  beforeEach(() => {
    vi.useFakeTimers();
    connections = [];
    created = [];
    notices = [];
    nowMs = 1000;
    random = 0;
  });

  afterEach(() => {
    live?.stop();
    vi.useRealTimers();
  });

  const frame = (seq: number, epoch = EPOCH_A) => ({ epoch, seq });
  const availability = (seq: number, status = 'Held', epoch = EPOCH_A) => ({
    eventId: E1,
    seats: [{ seatId: SEAT, status }],
    ...frame(seq, epoch),
  });

  describe('connecting', () => {
    it('is live only after the server has identified the connection, and says so exactly once', async () => {
      setup(alice);
      const connection = new FakeConnection();
      connections.push(connection);

      expect(live.status()).toBe('off');
      live.start();
      expect(live.status()).toBe('connecting');
      await settle();

      expect(live.status()).toBe('live');
      expect(live.isLive()).toBe(true);
      expect(live.degraded()).toBe(false);
      expect(connection.calls('Hello')).toHaveLength(1);
      expect(notices).toEqual([{ kind: 'resync', reason: 'connected', at: 1000 }]);
    });

    it('records what the server says about the connection without treating it as authority', async () => {
      setup(alice);
      const connection = new FakeConnection();
      connection.admin = true;
      connections.push(connection);
      live.start();
      await settle();

      expect(live.serverSaysAdmin()).toBe(true);
    });

    it('starts only once', async () => {
      setup(alice);
      live.start();
      live.start();
      await settle();

      expect(created).toHaveLength(1);
    });
  });

  describe('messages', () => {
    let connection: FakeConnection;

    beforeEach(async () => {
      setup(alice);
      connection = new FakeConnection();
      connections.push(connection);
      live.start();
      await settle();
      notices.length = 0;
    });

    it('turns each message into a notice, in the order accepted', () => {
      connection.push('availability', availability(1));
      connection.push('reservation', {
        reservationId: 'r1',
        eventId: E1,
        status: 'Held',
        expiresAtUtc: '2030-01-01T10:10:00Z',
        ...frame(2),
      });
      connection.push('order', {
        orderId: 'o1',
        reservationId: 'r1',
        status: 'PendingPayment',
        paymentStatus: null,
        ticketCount: 0,
        ...frame(3),
      });
      connection.push('admin', { area: 'orders', ...frame(4) });

      expect(notices.map((n) => n.kind)).toEqual(['availability', 'reservation', 'order', 'admin']);
      expect(notices[0]).toEqual({
        kind: 'availability',
        eventId: E1,
        seats: [{ seatId: SEAT, status: 'Held' }],
      });
      expect(notices[2]).toEqual({
        kind: 'order',
        orderId: 'o1',
        reservationId: 'r1',
        status: 'PendingPayment',
        paymentStatus: null,
        ticketCount: 0,
      });
    });

    it('drops a duplicate or out-of-order message and counts it', () => {
      connection.push('availability', availability(5));
      connection.push('availability', availability(5, 'Available')); // duplicate position
      connection.push('availability', availability(4, 'Available')); // older
      connection.push('availability', availability(6, 'Sold'));

      expect(notices.map((n) => (n.kind === 'availability' ? n.seats[0].status : n.kind))).toEqual([
        'Held',
        'Sold',
      ]);
      expect(live.ignored()).toBe(2);
    });

    it('ignores anything it does not understand, without throwing', () => {
      connection.push('availability', null);
      connection.push('availability', 'text');
      connection.push('availability', { eventId: E1, ...frame(1) }); // no seats
      connection.push('reservation', { reservationId: 'r1', ...frame(2) });
      connection.push('admin', { area: 'not-an-area', ...frame(3) });
      connection.push('order', { orderId: 7, ...frame(4) });
      connection.push('availability', { eventId: E1, seats: [] }); // no position

      expect(notices).toEqual([]);
      expect(live.ignored()).toBe(7);
    });

    it('keeps only well-formed seats of an availability message', () => {
      connection.push('availability', {
        eventId: E1,
        seats: [{ seatId: SEAT, status: 'Sold' }, { seatId: 1 }, null, { status: 'Held' }],
        ...frame(1),
      });

      expect(notices).toEqual([
        { kind: 'availability', eventId: E1, seats: [{ seatId: SEAT, status: 'Sold' }] },
      ]);
    });

    it('passes a server resync on as a resync notice', () => {
      connection.push('resync', { reason: 'queue-overflow', ...frame(1) });

      expect(notices).toEqual([{ kind: 'resync', reason: 'server', at: 1000 }]);
    });

    it('treats a message from another server process as a lost baseline, then applies it', () => {
      connection.push('availability', availability(1));
      connection.push('availability', availability(1, 'Sold', EPOCH_B)); // same seq, new epoch

      expect(notices.map((n) => n.kind)).toEqual(['availability', 'resync', 'availability']);
      expect(notices[1]).toMatchObject({ kind: 'resync', reason: 'epoch-changed' });
      // The new epoch has its own sequence: its next position is judged against it, not against the old process.
      connection.push('availability', availability(2, 'Available', EPOCH_B));
      expect(notices.at(-1)).toMatchObject({ kind: 'availability' });
    });
  });

  describe('event subscriptions', () => {
    it('waits for the connection, then subscribes and tells pages whose data is older to read again', async () => {
      setup(alice);
      const connection = new FakeConnection();
      connections.push(connection);
      live.watchEvent(E1);
      live.start();
      await settle();

      expect(connection.calls('SubscribeEvent')).toEqual([[E1]]);
      expect(live.subscribedEvents().has(E1)).toBe(true);
      expect(
        notices.filter((n) => n.kind === 'resync').map((n) => (n as { reason: string }).reason),
      ).toEqual(['connected', 'subscribed']);
    });

    it('subscribes at once when already live, and shares one subscription between watchers', async () => {
      setup(alice);
      const connection = new FakeConnection();
      connections.push(connection);
      live.start();
      await settle();

      const first = live.watchEvent(E1);
      const second = live.watchEvent(E1);
      await settle();
      expect(connection.calls('SubscribeEvent')).toEqual([[E1]]);

      first();
      first(); // releasing twice does nothing
      expect(connection.calls('UnsubscribeEvent')).toEqual([]);
      second();
      expect(connection.calls('UnsubscribeEvent')).toEqual([[E1]]);
      expect(live.subscribedEvents().has(E1)).toBe(false);
    });

    it('does not claim a subscription the server refused', async () => {
      setup(alice);
      const connection = new FakeConnection();
      connection.failing.set('SubscribeEvent', new Error('Live.TooManySubscriptions'));
      connections.push(connection);
      live.watchEvent(E1);
      live.start();
      await settle();

      expect(live.subscribedEvents().has(E1)).toBe(false);
      expect(notices.filter((n) => n.kind === 'resync' && n.reason === 'subscribed')).toEqual([]);
      expect(live.isLive()).toBe(true); // the connection itself is fine
    });

    it('renews its subscriptions after a reconnect', async () => {
      setup(alice);
      const first = new FakeConnection();
      const second = new FakeConnection();
      connections.push(first, second);
      live.watchEvent(E1);
      live.watchEvent(E2);
      live.start();
      await settle();
      expect(live.subscribedEvents().size).toBe(2);

      await advance(60_000); // a healthy connection
      first.drop();
      expect(live.subscribedEvents().size).toBe(0); // nothing is claimed on a dead connection
      await advance(300);

      expect(second.calls('SubscribeEvent')).toEqual([[E1], [E2]]);
      expect(live.subscribedEvents().size).toBe(2);
    });

    it('ignores an empty event id', () => {
      setup(alice);
      const release = live.watchEvent('');

      expect(() => release()).not.toThrow();
    });
  });

  describe('reconnecting', () => {
    it('reconnects almost at once when a long-lived connection is closed (the server rotates connections on purpose)', async () => {
      setup(alice);
      const first = new FakeConnection();
      const second = new FakeConnection();
      connections.push(first, second);
      live.start();
      await settle();
      notices.length = 0;

      await advance(60_000);
      first.drop();
      expect(live.status()).toBe('reconnecting');
      expect(live.degraded()).toBe(false); // a gap this short is not worth a warning
      await advance(300);

      expect(created).toHaveLength(2);
      expect(live.status()).toBe('live');
      expect(notices).toEqual([{ kind: 'resync', reason: 'connected', at: nowMs }]);
    });

    it('backs off exponentially, with the configured jitter and cap, and resets after a success', async () => {
      setup(alice);
      const failing = (): FakeConnection => {
        const c = new FakeConnection();
        c.startError = new Error('network down');
        return c;
      };
      random = 0.5; // +12.5 % of each delay
      connections.push(
        failing(),
        failing(),
        failing(),
        failing(),
        failing(),
        failing(),
        failing(),
        failing(),
      );
      live.start();
      await settle();
      expect(live.failures()).toBe(1);

      // 1, 2, 4, 8, 16 s, then the 30 s cap (applied before the jitter), each lengthened by 12.5 %.
      const waits = [1125, 2250, 4500, 9000, 18_000, 33_750, 33_750];
      for (const wait of waits) {
        const before = created.length;
        await advance(wait - 1);
        expect(created.length).toBe(before); // not yet
        await advance(1);
        expect(created.length).toBe(before + 1);
      }
      expect(live.failures()).toBe(8);
      expect(live.status()).toBe('reconnecting');

      // A success resets the count.
      connections.length = 0;
      connections.push(new FakeConnection());
      await advance(33_750);
      expect(live.isLive()).toBe(true);
      expect(live.failures()).toBe(0);
    });

    it('says so once the connection has been down long enough to matter, and not before', async () => {
      setup(alice);
      for (let i = 0; i < 2; i++) {
        const bad = new FakeConnection();
        bad.startError = new Error('network down');
        connections.push(bad);
      }
      connections.push(new FakeConnection());
      live.start();
      await settle();

      expect(live.degraded()).toBe(false);
      await advance(1999);
      expect(live.degraded()).toBe(false); // the second attempt (at 1 s) failed too, but the gap is still short
      await advance(1);
      expect(live.degraded()).toBe(true);

      await advance(1000); // the third attempt (at 3 s) succeeds
      expect(live.isLive()).toBe(true);
      expect(live.degraded()).toBe(false);
    });

    it('stops pressing a server that refuses live connections and keeps the pages working', async () => {
      setup(alice);
      const refused = new FakeConnection();
      refused.startError = new Error(
        "Failed to complete negotiation with the server: Error: Not Found: Status code '404'",
      );
      connections.push(refused);
      live.start();
      await settle();

      expect(live.status()).toBe('unavailable');
      expect(live.degraded()).toBe(true);
      await advance(299_999);
      expect(created).toHaveLength(1);
      await advance(1);
      expect(created).toHaveLength(2); // it does try again, rarely
    });

    it('treats an origin refusal the same way', async () => {
      setup(alice);
      const refused = new FakeConnection();
      refused.startError = Object.assign(new Error('Forbidden'), { statusCode: 403 });
      connections.push(refused);
      live.start();
      await settle();

      expect(live.status()).toBe('unavailable');
    });

    it('treats a failed Hello as a failed attempt', async () => {
      setup(alice);
      const broken = new FakeConnection();
      broken.failing.set('Hello', new Error('boom'));
      connections.push(broken);
      live.start();
      await settle();

      expect(live.isLive()).toBe(false);
      expect(broken.stopped).toBe(true);
      expect(live.failures()).toBe(1);
    });

    it('announces a new server process when Hello answers with another epoch', async () => {
      setup(alice);
      const first = new FakeConnection();
      const second = new FakeConnection();
      second.helloEpoch = EPOCH_B;
      connections.push(first, second);
      live.start();
      await settle();
      notices.length = 0;

      await advance(60_000);
      first.drop();
      await advance(300);

      expect(notices).toEqual([{ kind: 'resync', reason: 'epoch-changed', at: nowMs }]);
    });

    it('ignores everything a replaced connection still says', async () => {
      setup(alice);
      const first = new FakeConnection();
      const second = new FakeConnection();
      connections.push(first, second);
      live.start();
      await settle();
      await advance(60_000);
      first.drop();
      await advance(300);
      notices.length = 0;

      first.push('availability', availability(99)); // a late message from the dead connection
      first.drop();

      expect(notices).toEqual([]);
      expect(live.isLive()).toBe(true);
    });

    it('retries at once when the browser comes back online', async () => {
      setup(alice);
      const bad = new FakeConnection();
      bad.startError = new Error('network down');
      connections.push(bad, new FakeConnection());
      live.start();
      await settle();
      expect(live.isLive()).toBe(false);

      globalThis.dispatchEvent(new Event('online'));
      await settle();

      expect(live.isLive()).toBe(true);
    });
  });

  describe('identity', () => {
    it('opens a new connection when the signed-in user changes, because the cookie decides what a connection may hear', async () => {
      setup(alice);
      TestBed.tick();
      const first = new FakeConnection();
      const second = new FakeConnection();
      connections.push(first, second);
      live.start();
      await settle();
      notices.length = 0;

      user.set(bob);
      TestBed.tick();
      await settle();

      expect(first.stopped).toBe(true);
      expect(created).toHaveLength(2);
      expect(live.isLive()).toBe(true);
      expect(notices).toEqual([{ kind: 'resync', reason: 'identity-changed', at: nowMs }]);
    });

    it('opens ONE connection when the session becomes known just before the service starts (a signed-in page load)', async () => {
      setup(null);
      TestBed.tick(); // the effect runs while nobody is signed in yet
      user.set(alice); // the session load finishes ...
      connections.push(new FakeConnection());
      live.start(); // ... and the shell starts the service right after
      TestBed.tick(); // the effect sees the new user: it must not reopen what start() just opened for that user
      await settle();

      expect(created).toHaveLength(1);
      expect(live.isLive()).toBe(true);
    });

    it('does the same on sign-out', async () => {
      setup(alice);
      TestBed.tick();
      connections.push(new FakeConnection(), new FakeConnection());
      live.start();
      await settle();

      user.set(null);
      TestBed.tick();
      await settle();

      expect(created).toHaveLength(2);
    });

    it('does not reconnect for a change that is not a different user', async () => {
      setup(alice);
      TestBed.tick();
      connections.push(new FakeConnection());
      live.start();
      await settle();

      user.set({ ...alice, displayName: 'Alice B.' });
      TestBed.tick();
      await settle();

      expect(created).toHaveLength(1);
    });
  });

  describe('stopping', () => {
    it('closes the connection, cancels retries and goes quiet', async () => {
      setup(alice);
      const connection = new FakeConnection();
      connections.push(connection);
      live.start();
      await settle();

      live.stop();

      expect(connection.stopped).toBe(true);
      expect(live.status()).toBe('off');
      connection.push('availability', availability(1));
      expect(notices.filter((n) => n.kind === 'availability')).toEqual([]);
      connection.drop();
      await advance(120_000);
      expect(created).toHaveLength(1);
    });

    it('can be started again', async () => {
      setup(alice);
      connections.push(new FakeConnection(), new FakeConnection());
      live.start();
      await settle();
      live.stop();

      live.start();
      await settle();

      expect(created).toHaveLength(2);
      expect(live.isLive()).toBe(true);
    });
  });
});

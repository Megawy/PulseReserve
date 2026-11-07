import {
  Injectable,
  InjectionToken,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { LIVE_CONNECTION_FACTORY, LiveConnectionLike } from './live-connection';
import {
  HelloWire,
  LIVE_AREAS,
  LiveArea,
  LiveNotice,
  LiveStatus,
  ResyncReason,
  SeatDeltaWire,
} from './live.models';

/** Timing of the connection lifecycle. Frontend defaults (not backend-specified); tests override them. */
export interface LiveTiming {
  /** First retry delay after a failure; doubled each time up to `backoffMaxMs`. */
  readonly backoffBaseMs: number;
  readonly backoffMaxMs: number;
  /** Random extra delay, as a fraction of the delay, so that many clients do not reconnect in lockstep. */
  readonly jitterRatio: number;
  /** Delay before reconnecting after a connection that lived long enough (the server rotates connections on purpose). */
  readonly rotationDelayMs: number;
  /** A connection that lived this long counts as healthy: the next drop is a rotation, not a failure. */
  readonly stableAfterMs: number;
  /** Retry interval when the server refuses live connections (disabled, or this origin is not allowed). */
  readonly unavailableRetryMs: number;
  /** How long the connection may be down before the UI says so (short gaps are invisible). */
  readonly degradedAfterMs: number;
  /** Monotonic milliseconds. */
  readonly now: () => number;
  /** A number in [0, 1). */
  readonly random: () => number;
}

export const LIVE_TIMING = new InjectionToken<LiveTiming>('LIVE_TIMING', {
  providedIn: 'root',
  factory: () => ({
    backoffBaseMs: 1_000,
    backoffMaxMs: 30_000,
    jitterRatio: 0.25,
    rotationDelayMs: 300,
    stableAfterMs: 30_000,
    unavailableRetryMs: 5 * 60_000,
    degradedAfterMs: 2_000,
    now: () => performance.now(),
    random: () => Math.random(),
  }),
});

type Wire = Record<string, unknown>;

const isRecord = (value: unknown): value is Wire => typeof value === 'object' && value !== null;
const isString = (value: unknown): value is string => typeof value === 'string';

/** The HTTP status behind a failed start, if there was one (the client library reports it inside the message). */
function statusOf(error: unknown): number | null {
  if (isRecord(error) && typeof error['statusCode'] === 'number') {
    return error['statusCode'];
  }
  const message = error instanceof Error ? error.message : String(error);
  const match = /Status code '(\d{3})'/.exec(message) ?? /\b(40[134])\b/.exec(message);
  return match ? Number(match[1]) : null;
}

/**
 * The browser's single real-time connection (`/hubs/live`). What it is, and is not:
 *
 * - **Hints, not facts.** A message says WHAT changed (a seat's availability, my reservation, my order, an admin area). Pages re-read
 *   the authoritative state from the REST API; nothing here is ever shown as "payment succeeded" or "refund done".
 * - **Identity is the cookie.** The connection is authenticated by the session cookie when it is opened, so it is reopened whenever
 *   the signed-in user changes. The server decides which groups it joins; the client cannot ask for anyone else's.
 * - **No gap is silent.** Every (re)connect, every event subscription that becomes active, every server `resync` and every change of
 *   server process is announced as a `resync` notice carrying the monotonic time `at`; a page re-reads when its data is older than
 *   that. Messages are de-duplicated and ordered per process by `(epoch, seq)`; a stale or repeated one is dropped.
 * - **Failure is tolerated.** The client retries with exponential backoff and jitter. While it is not live the pages keep their normal
 *   refresh/polling behaviour and the UI says so (`degraded`); when the server refuses live connections it retries only rarely.
 */
@Injectable({ providedIn: 'root' })
export class LiveService {
  private readonly auth = inject(AuthService);
  private readonly factory = inject(LIVE_CONNECTION_FACTORY);
  private readonly timing = inject(LIVE_TIMING);

  private readonly noticeSubject = new Subject<LiveNotice>();
  private readonly statusState = signal<LiveStatus>('off');
  private readonly degradedState = signal(false);
  private readonly subscribedState = signal<ReadonlySet<string>>(new Set());
  private readonly adminState = signal(false);
  private readonly failureState = signal(0);

  /** Every notice, in the order accepted. Subscribe before reading your data so a gap cannot open between the two. */
  readonly notices: Observable<LiveNotice> = this.noticeSubject.asObservable();
  readonly status = this.statusState.asReadonly();
  readonly isLive = computed(() => this.statusState() === 'live');
  /** The connection has been down long enough that the UI should say it: pages are then relying on their normal refresh. */
  readonly degraded = this.degradedState.asReadonly();
  /** Events whose availability subscription is active on the current connection. */
  readonly subscribedEvents = this.subscribedState.asReadonly();
  /** What the server said about this connection (UI hint only; the server authorises every call itself). */
  readonly serverSaysAdmin = this.adminState.asReadonly();
  /** Consecutive failed attempts (0 while live). */
  readonly failures = this.failureState.asReadonly();
  /** Messages that were not understood, or were duplicates/out of order; for diagnostics and tests. */
  readonly ignored = signal(0);

  private running = false;
  private generation = 0;
  private connection: LiveConnectionLike | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private degradedTimer: ReturnType<typeof setTimeout> | null = null;
  private liveSince = 0;
  private attempts = 0;
  private epoch: string | null = null;
  private lastSeq = 0;
  /** Whose session the current connection was opened for (null: anonymous). Compared, not assumed: the session can become known just before `start()`. */
  private connectedIdentity: string | null = null;
  private nextReason: ResyncReason = 'connected';
  private readonly watched = new Map<string, number>();

  constructor() {
    // The connection is authenticated when it opens, so a different user (or none) needs a new connection.
    effect(() => {
      const id = this.auth.user()?.id ?? null;
      untracked(() => {
        if (this.running && id !== this.connectedIdentity) {
          this.connectedIdentity = id;
          this.nextReason = 'identity-changed';
          this.reopen();
        }
      });
    });
  }

  /** Starts the connection (idempotent). Called once by the application shell. */
  start(): void {
    if (this.running) {
      return;
    }
    this.running = true;
    this.connectedIdentity = this.auth.user()?.id ?? null;
    // The browser regaining its network is a better moment to retry than the end of a backoff.
    globalThis.addEventListener?.('online', this.onOnline);
    void this.open();
  }

  /** Ends the connection for good (until `start()` again). */
  stop(): void {
    this.running = false;
    globalThis.removeEventListener?.('online', this.onOnline);
    this.teardown();
    this.statusState.set('off');
    this.setDegraded(false);
  }

  private readonly onOnline = (): void => this.retryNow();

  /** Retries now instead of waiting for the backoff (e.g. the browser came back online). */
  retryNow(): void {
    if (this.running && this.connection === null && this.statusState() !== 'connecting') {
      this.clearRetry();
      void this.open();
    }
  }

  /**
   * Subscribes to an event's public seat availability for as long as the returned function has not been called. Reference counted:
   * several components may watch the same event. The subscription is renewed after every reconnect, and once it is active a
   * `resync` notice (`subscribed`) tells pages whose data predates it to read again.
   */
  watchEvent(eventId: string): () => void {
    if (!eventId) {
      return () => undefined;
    }
    const count = this.watched.get(eventId) ?? 0;
    this.watched.set(eventId, count + 1);
    if (count === 0 && this.connection !== null && this.isLive()) {
      void this.subscribeEvents([eventId], this.connection, this.generation);
    }
    let released = false;
    return () => {
      if (released) {
        return;
      }
      released = true;
      const remaining = (this.watched.get(eventId) ?? 1) - 1;
      if (remaining > 0) {
        this.watched.set(eventId, remaining);
        return;
      }
      this.watched.delete(eventId);
      const connection = this.connection;
      if (this.subscribedState().has(eventId)) {
        const next = new Set(this.subscribedState());
        next.delete(eventId);
        this.subscribedState.set(next);
        connection?.invoke('UnsubscribeEvent', eventId).catch(() => undefined);
      }
    };
  }

  // ───────────── connection lifecycle ─────────────

  private reopen(): void {
    this.teardown();
    void this.open();
  }

  private teardown(): void {
    this.generation++;
    this.clearRetry();
    const connection = this.connection;
    this.connection = null;
    this.subscribedState.set(new Set());
    this.adminState.set(false);
    connection?.stop().catch(() => undefined);
  }

  private clearRetry(): void {
    if (this.retryTimer !== null) {
      clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
  }

  private async open(): Promise<void> {
    const generation = ++this.generation;
    this.statusState.set(
      this.attempts === 0 && this.epoch === null ? 'connecting' : 'reconnecting',
    );
    if (this.statusState() === 'reconnecting') {
      this.armDegraded();
    }
    const connection = this.factory();
    this.wire(connection, generation);
    try {
      await connection.start();
      if (generation !== this.generation) {
        connection.stop().catch(() => undefined);
        return;
      }
      const hello = await connection.invoke<HelloWire>('Hello');
      if (generation !== this.generation) {
        connection.stop().catch(() => undefined);
        return;
      }
      this.connection = connection;
      this.adopt(hello);
      await this.subscribeEvents([...this.watched.keys()], connection, generation);
    } catch (error) {
      if (generation !== this.generation) {
        return;
      }
      connection.stop().catch(() => undefined);
      this.connection = null;
      this.failed(error);
    }
  }

  private adopt(hello: HelloWire): void {
    const reason: ResyncReason =
      this.epoch !== null && this.epoch !== hello.epoch ? 'epoch-changed' : this.nextReason;
    this.nextReason = 'connected';
    if (this.epoch !== hello.epoch) {
      this.epoch = hello.epoch;
      this.lastSeq = 0;
    }
    this.adminState.set(hello.admin === true);
    this.attempts = 0;
    this.failureState.set(0);
    this.liveSince = this.timing.now();
    this.statusState.set('live');
    this.setDegraded(false);
    // Whatever was committed while there was no connection (or before this one existed) was not announced to it.
    this.noticeSubject.next({ kind: 'resync', reason, at: this.timing.now() });
  }

  private async subscribeEvents(
    eventIds: readonly string[],
    connection: LiveConnectionLike,
    generation: number,
  ): Promise<void> {
    let activated = false;
    for (const eventId of eventIds) {
      try {
        await connection.invoke('SubscribeEvent', eventId);
      } catch {
        // For example the per-connection limit: this event simply stays on the page's normal refresh.
        continue;
      }
      if (generation !== this.generation || !this.watched.has(eventId)) {
        continue;
      }
      this.subscribedState.set(new Set(this.subscribedState()).add(eventId));
      activated = true;
    }
    if (activated && generation === this.generation) {
      this.noticeSubject.next({ kind: 'resync', reason: 'subscribed', at: this.timing.now() });
    }
  }

  private closed(generation: number): void {
    if (generation !== this.generation) {
      return;
    }
    this.connection = null;
    this.subscribedState.set(new Set());
    this.adminState.set(false);
    if (!this.running) {
      return;
    }
    const healthy = this.timing.now() - this.liveSince >= this.timing.stableAfterMs;
    this.statusState.set('reconnecting');
    this.armDegraded();
    if (healthy) {
      // The server closes every connection after a fixed age so that its identity is checked again: reconnect almost at once.
      this.attempts = 0;
      this.scheduleRetry(this.timing.rotationDelayMs);
    } else {
      this.failed(new Error('The live connection was closed.'));
    }
  }

  private failed(error: unknown): void {
    this.attempts++;
    this.failureState.set(this.attempts);
    const status = statusOf(error);
    if (status === 401 || status === 403 || status === 404) {
      // Disabled on the server, or this origin is not allowed: do not hammer it. Pages keep their normal refresh.
      this.statusState.set('unavailable');
      this.setDegraded(true);
      this.scheduleRetry(this.timing.unavailableRetryMs);
      return;
    }
    this.statusState.set('reconnecting');
    this.armDegraded();
    const exponential = Math.min(
      this.timing.backoffMaxMs,
      this.timing.backoffBaseMs * 2 ** (this.attempts - 1),
    );
    this.scheduleRetry(
      Math.round(exponential * (1 + this.timing.jitterRatio * this.timing.random())),
    );
  }

  private scheduleRetry(delayMs: number): void {
    this.clearRetry();
    if (!this.running) {
      return;
    }
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      void this.open();
    }, delayMs);
  }

  private armDegraded(): void {
    if (this.degradedState() || this.degradedTimer !== null) {
      return;
    }
    this.degradedTimer = setTimeout(() => {
      this.degradedTimer = null;
      if (!this.isLive() && this.running) {
        this.degradedState.set(true);
      }
    }, this.timing.degradedAfterMs);
  }

  private setDegraded(value: boolean): void {
    if (!value && this.degradedTimer !== null) {
      clearTimeout(this.degradedTimer);
      this.degradedTimer = null;
    }
    this.degradedState.set(value);
  }

  // ───────────── messages ─────────────

  private wire(connection: LiveConnectionLike, generation: number): void {
    const on = (method: string, parse: (body: Wire) => LiveNotice | null): void =>
      connection.on(method, (body) => {
        if (generation !== this.generation) {
          return; // a connection that has been replaced
        }
        if (!isRecord(body) || !this.accept(body)) {
          this.ignored.update((n) => n + 1);
          return;
        }
        const notice = parse(body);
        if (notice) {
          this.noticeSubject.next(notice);
        } else {
          this.ignored.update((n) => n + 1);
        }
      });

    on('availability', (b) =>
      isString(b['eventId']) && Array.isArray(b['seats'])
        ? { kind: 'availability', eventId: b['eventId'], seats: parseSeats(b['seats']) }
        : null,
    );
    on('reservation', (b) =>
      isString(b['reservationId']) &&
      isString(b['eventId']) &&
      isString(b['status']) &&
      isString(b['expiresAtUtc'])
        ? {
            kind: 'reservation',
            reservationId: b['reservationId'],
            eventId: b['eventId'],
            status: b['status'],
            expiresAtUtc: b['expiresAtUtc'],
          }
        : null,
    );
    on('order', (b) =>
      isString(b['orderId']) && isString(b['reservationId']) && isString(b['status'])
        ? {
            kind: 'order',
            orderId: b['orderId'],
            reservationId: b['reservationId'],
            status: b['status'],
            paymentStatus: isString(b['paymentStatus']) ? b['paymentStatus'] : null,
            ticketCount: typeof b['ticketCount'] === 'number' ? b['ticketCount'] : 0,
          }
        : null,
    );
    on('admin', (b) =>
      isString(b['area']) && (LIVE_AREAS as readonly string[]).includes(b['area'])
        ? { kind: 'admin', area: b['area'] as LiveArea }
        : null,
    );
    on('resync', () => ({ kind: 'resync', reason: 'server', at: this.timing.now() }));
    connection.onClose(() => this.closed(generation));
  }

  /**
   * Applies the `(epoch, seq)` rule. A message of an earlier or the same position is a duplicate or arrived out of order and is dropped;
   * a message from another server process means the baseline is gone, so pages are asked to read again before it is used.
   */
  private accept(body: Wire): boolean {
    const epoch = body['epoch'];
    const seq = body['seq'];
    if (!isString(epoch) || typeof seq !== 'number' || !Number.isFinite(seq)) {
      return false;
    }
    if (epoch !== this.epoch) {
      this.epoch = epoch;
      this.lastSeq = seq;
      this.noticeSubject.next({ kind: 'resync', reason: 'epoch-changed', at: this.timing.now() });
      return true;
    }
    if (seq <= this.lastSeq) {
      return false;
    }
    this.lastSeq = seq;
    return true;
  }
}

function parseSeats(seats: readonly unknown[]): readonly SeatDeltaWire[] {
  const parsed: SeatDeltaWire[] = [];
  for (const seat of seats) {
    if (isRecord(seat) && isString(seat['seatId']) && isString(seat['status'])) {
      parsed.push({ seatId: seat['seatId'], status: seat['status'] });
    }
  }
  return parsed;
}

import { DestroyRef, InjectionToken, inject } from '@angular/core';
import { LIVE_TIMING, LiveService } from './live.service';
import { LiveNotice } from './live.models';

/** How long a burst of notices is allowed to settle before a page reads (a frontend default; tests shorten it). */
export const LIVE_REFRESH_DEBOUNCE_MS = new InjectionToken<number>('LIVE_REFRESH_DEBOUNCE_MS', {
  providedIn: 'root',
  factory: () => 300,
});

export interface LiveRefreshOptions {
  /** Which notices (other than `resync`, which is handled here) mean "what this page shows may have changed". */
  readonly matches: (notice: LiveNotice) => boolean;
  /**
   * Re-reads the authoritative state from the API; the notice itself is never trusted as the new state. `resync` is true when a gap was
   * announced (so everything on screen may be stale), false when only a matching change was.
   */
  readonly refresh: (resync: boolean) => void;
  /** A burst of notices causes one read after this quiet period (default: `LIVE_REFRESH_DEBOUNCE_MS`, 300 ms). */
  readonly debounceMs?: number;
}

export interface LiveRefresh {
  /** Call whenever a read of the data on screen STARTS (not when it ends), so a gap announced later is judged correctly. */
  markRequested(): void;
}

/**
 * Makes a page follow live notices. A matching notice triggers one debounced `refresh`; a `resync` triggers it only when the data
 * on screen was requested no later than the moment the gap was announced (data requested after that moment cannot have missed
 * anything, so a page that loads once the connection is already live does not read twice). Call within an injection context; the
 * subscription ends with the owner.
 */
export function createLiveRefresh(options: LiveRefreshOptions): LiveRefresh {
  const live = inject(LiveService);
  const timing = inject(LIVE_TIMING);
  const destroyRef = inject(DestroyRef);
  const debounceMs = options.debounceMs ?? inject(LIVE_REFRESH_DEBOUNCE_MS);
  let requestedAt = Number.NEGATIVE_INFINITY;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let resyncPending = false;

  const schedule = (resync: boolean): void => {
    resyncPending ||= resync;
    if (timer !== null) {
      return;
    }
    timer = setTimeout(() => {
      timer = null;
      const full = resyncPending;
      resyncPending = false;
      options.refresh(full);
    }, debounceMs);
  };

  const subscription = live.notices.subscribe((notice) => {
    if (notice.kind === 'resync') {
      if (requestedAt <= notice.at) {
        schedule(true);
      }
    } else if (options.matches(notice)) {
      schedule(false);
    }
  });

  destroyRef.onDestroy(() => {
    subscription.unsubscribe();
    if (timer !== null) {
      clearTimeout(timer);
    }
  });

  return { markRequested: () => (requestedAt = timing.now()) };
}

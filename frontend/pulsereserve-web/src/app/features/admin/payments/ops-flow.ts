import {
  DestroyRef,
  Injector,
  Signal,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable, Subscription } from 'rxjs';
import { OperationsProvider } from '../../../core/api/admin-payments.models';
import { ApiError, toApiError } from '../../../core/api/api-error';
import { createLiveRefresh } from '../../../core/live/live-refresh';
import { LiveArea } from '../../../core/live/live.models';
import { ToastService } from '../../../shared/ui/toast/toast.service';

export type ListState<T> =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly items: readonly T[] }
  | { readonly kind: 'error'; readonly error: ApiError };

export const PAGE_SIZES = [50, 100, 200] as const;

/**
 * The configured providers and what the system can do with each, loaded once per page. It only ADDS explanation: if the
 * request fails the lists work exactly as before (no button is hidden and no state is guessed from a missing answer).
 */
export function createProviderDirectory(fetch: () => Observable<readonly OperationsProvider[]>): {
  readonly providers: Signal<readonly OperationsProvider[]>;
  load(): void;
} {
  const destroyRef = inject(DestroyRef);
  const providers = signal<readonly OperationsProvider[]>([]);
  let sub: Subscription | null = null;
  destroyRef.onDestroy(() => sub?.unsubscribe());
  return {
    providers,
    load: () => {
      sub?.unsubscribe();
      sub = fetch().subscribe({
        next: (list) => providers.set(list),
        error: () => providers.set([]),
      });
    },
  };
}

export interface OpsListOptions<T> {
  /** Filter values the API accepts; anything else in the URL is ignored instead of being sent (the API would answer 400). */
  readonly allowed: readonly string[];
  readonly fetch: (filter: string | null, limit: number) => Observable<T[]>;
  /** Text matched by the search box against the rows that were loaded. */
  readonly searchText: (item: T) => string;
  /**
   * The live areas that mean this list may have changed. A matching notice (or an announced gap) re-reads the list quietly: the rows on
   * screen are replaced only by the server's answer, and are kept if that read fails.
   */
  readonly liveAreas?: readonly LiveArea[];
}

export interface OpsList<T> {
  readonly state: Signal<ListState<T>>;
  readonly filter: Signal<string | null>;
  readonly limit: Signal<number>;
  readonly query: Signal<string>;
  /** Loaded rows that match the search box. */
  readonly visible: Signal<readonly T[]>;
  readonly loadedCount: Signal<number>;
  setFilter(value: string | null): void;
  setLimit(value: number): void;
  setQuery(value: string): void;
  /** Reloads the list; `onLoaded` runs once the request has finished, whether it succeeded or failed. */
  reload(onLoaded?: () => void): void;
}

/**
 * State of one operations list. The filter and page size live in the URL (`?state=NeedsReview&limit=100`) so a view can
 * be bookmarked, the overview can link straight to a filtered list, and the back button works. The search box narrows
 * only the rows already loaded: the API has no free-text search, and the UI says so.
 */
export function createOpsList<T>(options: OpsListOptions<T>): OpsList<T> {
  const route = inject(ActivatedRoute);
  const router = inject(Router);
  const destroyRef = inject(DestroyRef);

  const state = signal<ListState<T>>({ kind: 'loading' });
  const filter = signal<string | null>(null);
  const limit = signal<number>(PAGE_SIZES[0]);
  const query = signal('');
  let sub: Subscription | null = null;

  const liveAreas = options.liveAreas ?? [];
  const liveRefresh = createLiveRefresh({
    matches: (n) => n.kind === 'admin' && liveAreas.includes(n.area),
    refresh: () => {
      if (liveAreas.length > 0) {
        load(undefined, true);
      }
    },
  });

  /** `quiet` keeps the rows on screen while the new ones are read, and keeps them if the read fails (live refreshes). */
  function load(onLoaded?: () => void, quiet = false): void {
    sub?.unsubscribe();
    if (!quiet) {
      state.set({ kind: 'loading' });
    }
    liveRefresh.markRequested();
    sub = options.fetch(filter(), limit()).subscribe({
      next: (items) => {
        state.set({ kind: 'ready', items });
        onLoaded?.();
      },
      error: (error: unknown) => {
        if (!(quiet && state().kind === 'ready')) {
          state.set({ kind: 'error', error: toApiError(error) });
        }
        onLoaded?.();
      },
    });
  }

  const params = route.queryParamMap.subscribe((p) => {
    const f = p.get('state');
    filter.set(f !== null && options.allowed.includes(f) ? f : null);
    const l = Number(p.get('limit'));
    limit.set((PAGE_SIZES as readonly number[]).includes(l) ? l : PAGE_SIZES[0]);
    load();
  });
  destroyRef.onDestroy(() => {
    params.unsubscribe();
    sub?.unsubscribe();
  });

  const navigate = (queryParams: Record<string, string | null>): void => {
    void router.navigate([], { relativeTo: route, queryParams, queryParamsHandling: 'merge' });
  };

  const visible = computed(() => {
    const s = state();
    if (s.kind !== 'ready') {
      return [];
    }
    const needle = query().trim().toLowerCase();
    return needle
      ? s.items.filter((i) => options.searchText(i).toLowerCase().includes(needle))
      : s.items;
  });

  return {
    state,
    filter,
    limit,
    query,
    visible,
    loadedCount: computed(() => {
      const s = state();
      return s.kind === 'ready' ? s.items.length : 0;
    }),
    setFilter: (value) => navigate({ state: value || null }),
    setLimit: (value) => navigate({ limit: value === PAGE_SIZES[0] ? null : String(value) }),
    setQuery: (value) => query.set(value),
    reload: (onLoaded) => load(onLoaded),
  };
}

export interface ActionFlowOptions<T, R> {
  readonly run: (target: T) => Observable<R>;
  /** Toast text on success. It must describe what the server reported, never more. */
  readonly success: (target: T, result: R) => string;
  /** Called after the server answered (success, or "no longer eligible") so lists show the current truth. */
  readonly done: () => void;
}

export interface ActionFlow<T> {
  /** The item the open confirmation dialog is about; null when closed. */
  readonly pending: Signal<T | null>;
  readonly busy: Signal<boolean>;
  /** A failure the person can act on, shown inside the dialog. */
  readonly error: Signal<string | null>;
  ask(target: T): void;
  cancel(): void;
  confirm(): void;
}

/**
 * Confirm-then-act. Nothing is sent until `confirm()`; the dialog stays open while the request is in flight so the
 * action cannot be repeated, and the outcome is shown exactly as the server answered it. A 404/409 means the item is
 * no longer eligible (someone else handled it, or the worker moved it on): the dialog closes and the lists reload.
 */
export function createActionFlow<T, R>(options: ActionFlowOptions<T, R>): ActionFlow<T> {
  const toast = inject(ToastService);
  const destroyRef = inject(DestroyRef);
  const pending = signal<T | null>(null);
  const busy = signal(false);
  const error = signal<string | null>(null);
  let sub: Subscription | null = null;
  destroyRef.onDestroy(() => sub?.unsubscribe());

  return {
    pending,
    busy,
    error,
    ask: (target) => {
      error.set(null);
      pending.set(target);
    },
    cancel: () => {
      if (!busy()) {
        pending.set(null);
        error.set(null);
      }
    },
    confirm: () => {
      const target = pending();
      if (target === null || busy()) {
        return;
      }
      busy.set(true);
      error.set(null);
      sub = options.run(target).subscribe({
        next: (result) => {
          busy.set(false);
          pending.set(null);
          toast.success(options.success(target, result));
          options.done();
        },
        error: (e: unknown) => {
          const err = toApiError(e);
          busy.set(false);
          if (err.status === 404 || err.status === 409) {
            pending.set(null);
            toast.error(
              'That item is no longer eligible for this action (it may already have been handled). The list has been refreshed.',
            );
            options.done();
          } else if (err.status === 401) {
            pending.set(null);
            toast.error('Your session has ended. Sign in again to continue.');
          } else if (err.status === 403) {
            error.set(
              err.code === 'Csrf.Invalid'
                ? 'The security token was rejected. Reload the page and try again.'
                : 'The server refused this action: administrator access is required.',
            );
          } else {
            error.set(err.message);
          }
        },
      });
    },
  };
}

/**
 * After an action closes its dialog and the reload removes the row that opened it, the browser drops focus on
 * `<body>` and a keyboard or screen-reader user loses their place. This moves focus to a stable heading (it carries
 * `tabindex="-1"`) but only when focus really was lost, so it never steals focus from something the person chose.
 * It runs after the next render, which is after the dialog has closed and returned focus to its opener.
 */
export function restoreFocus(injector: Injector, headingId: string): void {
  afterNextRender(
    () => {
      const active = document.activeElement;
      // Lost: nothing focused, the row is gone, or focus is still inside the dialog that has just closed.
      const lost =
        !active ||
        active === document.body ||
        !active.isConnected ||
        active.closest('dialog:not([open])') !== null;
      if (!lost) {
        return;
      }
      document.getElementById(headingId)?.focus();
    },
    { injector },
  );
}

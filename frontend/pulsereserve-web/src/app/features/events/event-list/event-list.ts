import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ApiError, toApiError } from '../../../core/api/api-error';
import { CatalogApiClient } from '../../../core/api/catalog-api.client';
import { CatalogEvent } from '../../../core/api/catalog-api.models';
import { dateTile, formatDateTime, formatMoney } from '../../../shared/format';
import { EmptyState } from '../../../shared/ui/empty-state/empty-state';
import { ErrorState } from '../../../shared/ui/error-state/error-state';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';

type ListState =
  | { kind: 'loading' }
  | { kind: 'ready'; events: readonly CatalogEvent[] }
  | { kind: 'error'; error: ApiError };

type Filter = 'all' | 'OnSale' | 'Published';

@Component({
  selector: 'app-event-list',
  imports: [RouterLink, EmptyState, ErrorState, StatusBadge],
  templateUrl: './event-list.html',
  styleUrl: './event-list.scss',
})
export class EventList implements OnInit {
  private readonly catalog = inject(CatalogApiClient);
  private readonly destroyRef = inject(DestroyRef);
  private sub: Subscription | null = null;

  protected readonly state = signal<ListState>({ kind: 'loading' });
  protected readonly filter = signal<Filter>('all');
  protected readonly filters: readonly { value: Filter; label: string }[] = [
    { value: 'all', label: 'All' },
    { value: 'OnSale', label: 'On sale' },
    { value: 'Published', label: 'Coming soon' },
  ];
  protected readonly error = computed(() => {
    const s = this.state();
    return s.kind === 'error' ? s.error : null;
  });
  protected readonly skeletons = [1, 2, 3];

  /** The filter is only worth showing when the loaded events are not all of one kind. */
  protected readonly filterable = computed(() => {
    const s = this.state();
    return s.kind === 'ready' && new Set(s.events.map((e) => e.status)).size > 1;
  });

  protected readonly cards = computed(() => {
    const s = this.state();
    if (s.kind !== 'ready') {
      return [];
    }
    const filter = this.filter();
    return s.events
      .filter((event) => filter === 'all' || event.status === filter)
      .map((event) => ({
        event,
        price: priceFrom(event),
        tile: dateTile(event.scheduledStartUtc),
        when: formatDateTime(event.scheduledStartUtc),
      }));
  });

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => this.sub?.unsubscribe());
    this.load();
  }

  protected load(): void {
    this.sub?.unsubscribe();
    this.state.set({ kind: 'loading' });
    this.sub = this.catalog.listEvents().subscribe({
      next: (events) => this.state.set({ kind: 'ready', events }),
      error: (error: unknown) => this.state.set({ kind: 'error', error: toApiError(error) }),
    });
  }
}

/** "From" price: the lowest of the event's real prices, only when all share one currency. */
function priceFrom(event: CatalogEvent): string | null {
  if (event.prices.length === 0) {
    return null;
  }
  const currencies = new Set(event.prices.map((p) => p.currency));
  if (currencies.size !== 1) {
    return null;
  }
  const min = Math.min(...event.prices.map((p) => p.amount));
  return `From ${formatMoney(min, event.prices[0].currency)}`;
}

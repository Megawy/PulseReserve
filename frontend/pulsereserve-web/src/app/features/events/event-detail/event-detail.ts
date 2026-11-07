import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription, forkJoin, of, catchError, map, switchMap } from 'rxjs';
import { ApiError, toApiError } from '../../../core/api/api-error';
import { CatalogApiClient } from '../../../core/api/catalog-api.client';
import { CatalogEvent, SeatingLayout, Venue } from '../../../core/api/catalog-api.models';
import { formatRemaining } from '../../../core/time/countdown';
import { formatDateRange, formatMoney, formatTime, isValidDate } from '../../../shared/format';
import { Reservation } from '../../../core/api/reservation-api.models';
import { Alert } from '../../../shared/ui/alert/alert';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { ConfirmDialog } from '../../../shared/ui/confirm-dialog/confirm-dialog';
import { ErrorState } from '../../../shared/ui/error-state/error-state';
import { LiveIndicator } from '../../../shared/ui/live-indicator/live-indicator';
import { BookingFlow } from '../booking/booking-flow';
import { CheckoutFlow } from '../booking/checkout-flow';
import { seatNameMap } from '../seat-names';
import { CheckoutPanel } from '../checkout/checkout-panel';
import { SeatMap } from '../seat-map/seat-map';

type DetailState =
  | { kind: 'loading' }
  | { kind: 'error'; error: ApiError }
  | {
      kind: 'ready';
      event: CatalogEvent;
      venue: Venue | null;
      layout: SeatingLayout | null;
      layoutError: ApiError | null;
    };

@Component({
  selector: 'app-event-detail',
  imports: [
    RouterLink,
    Alert,
    StatusBadge,
    ErrorState,
    LiveIndicator,
    SeatMap,
    ConfirmDialog,
    CheckoutPanel,
  ],
  providers: [BookingFlow, CheckoutFlow],
  templateUrl: './event-detail.html',
  styleUrl: './event-detail.scss',
})
export class EventDetail implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly catalog = inject(CatalogApiClient);
  private readonly destroyRef = inject(DestroyRef);
  private loadSub: Subscription | null = null;
  private eventId = '';

  protected readonly flow = inject(BookingFlow);
  protected readonly checkout = inject(CheckoutFlow);
  protected readonly state = signal<DetailState>({ kind: 'loading' });
  protected readonly confirmingRelease = signal(false);
  protected readonly returnUrl = computed(
    () => `/events/${this.ready()?.event.id ?? this.eventId}`,
  );

  protected readonly ready = computed(() => {
    const s = this.state();
    return s.kind === 'ready' ? s : null;
  });
  protected readonly error = computed(() => {
    const s = this.state();
    return s.kind === 'error' ? s.error : null;
  });
  protected readonly availabilityError = computed(() => {
    const a = this.flow.availability();
    return a.kind === 'error' ? a.error : null;
  });
  protected readonly availabilityLoading = computed(
    () => this.flow.availability().kind === 'loading',
  );

  protected readonly salesOpen = computed(() => this.ready()?.event.status === 'OnSale');

  /** Human-readable seat descriptions, keyed by seat ID, from the real layout. */
  protected readonly seatNames = computed(() => seatNameMap(this.ready()?.layout));

  protected readonly selectedSeats = computed(() =>
    [...this.flow.selected()].map((id) => ({
      id,
      name: this.seatNames().get(id) ?? id,
      price: this.priceText(id),
    })),
  );

  protected readonly selectionTotalText = computed(() => {
    const { amount, currency, mixed } = this.flow.selectionTotal();
    return currency && !mixed ? formatMoney(amount, currency) : null;
  });

  protected readonly reservationSeats = computed(() =>
    (this.flow.reservation()?.seats ?? []).map((seat) => ({
      id: seat.seatId,
      name: this.seatNames().get(seat.seatId) ?? seat.seatId,
      price: formatMoney(seat.priceAmount, seat.currency),
    })),
  );

  protected readonly remainingText = computed(() => {
    const c = this.flow.countdown();
    return c?.kind === 'running' ? formatRemaining(c.remainingSeconds) : null;
  });

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => this.loadSub?.unsubscribe());
    this.route.paramMap.subscribe((params) => {
      this.eventId = params.get('id') ?? '';
      this.load();
    });
  }

  protected load(): void {
    this.loadSub?.unsubscribe();
    this.state.set({ kind: 'loading' });
    this.loadSub = this.catalog
      .getEvent(this.eventId)
      .pipe(
        switchMap((event) =>
          forkJoin({
            venue: this.catalog.getVenue(event.venueId).pipe(catchError(() => of(null))),
            layout: event.seatingLayoutId
              ? this.catalog.getSeatingLayout(event.seatingLayoutId).pipe(
                  map((layout) => ({ layout, error: null as ApiError | null })),
                  catchError((e: unknown) => of({ layout: null, error: toApiError(e) })),
                )
              : of({ layout: null, error: null as ApiError | null }),
          }).pipe(map((extra) => ({ event, ...extra }))),
        ),
      )
      .subscribe({
        next: ({ event, venue, layout }) => {
          this.state.set({
            kind: 'ready',
            event,
            venue,
            layout: layout.layout,
            layoutError: layout.error,
          });
          if (event.status === 'OnSale' && layout.layout) {
            this.flow.start(event.id);
          }
        },
        error: (error: unknown) => this.state.set({ kind: 'error', error: toApiError(error) }),
      });
  }

  protected validDate(iso: string): boolean {
    return isValidDate(iso);
  }

  protected readonly dateRange = computed(() => {
    const event = this.ready()?.event;
    return event && isValidDate(event.scheduledStartUtc)
      ? formatDateRange(event.scheduledStartUtc, event.scheduledEndUtc)
      : '';
  });

  /** Real price tiers: the event's prices labelled with the layout's tier names. */
  protected readonly tiers = computed(() => {
    const page = this.ready();
    if (!page) {
      return [];
    }
    const names = new Map((page.layout?.priceTiers ?? []).map((t) => [t.id, t.name]));
    return page.event.prices
      .map((p) => ({
        id: p.priceTierId,
        name: names.get(p.priceTierId) ?? 'Standard',
        amount: p.amount,
        price: formatMoney(p.amount, p.currency),
      }))
      .sort((a, b) => a.amount - b.amount);
  });

  /** Where the customer is in the journey, derived only from real reservation/order state. */
  protected readonly steps = computed(() => {
    const phase = this.flow.phase();
    const index = {
      none: 0,
      expired: 0,
      released: 0,
      held: 1,
      unverifiable: 1,
      checkout: 2,
      confirmed: 3,
    }[phase];
    return ['Choose seats', 'Hold', 'Checkout', 'Tickets'].map((label, i) => ({
      label,
      current: i === index,
      done: i < index,
    }));
  });

  protected shortId(id: string): string {
    return id.slice(0, 8).toUpperCase();
  }

  protected expiryTime(iso: string): string {
    return formatTime(iso);
  }

  protected reservationTotal(r: Reservation): string {
    return formatMoney(r.totalAmount, r.currency);
  }

  protected confirmRelease(): void {
    this.confirmingRelease.set(false);
    this.flow.release();
  }

  private priceText(seatId: string): string | null {
    const seat = this.flow.seatsById().get(seatId);
    return seat ? formatMoney(seat.priceAmount, seat.currency) : null;
  }
}

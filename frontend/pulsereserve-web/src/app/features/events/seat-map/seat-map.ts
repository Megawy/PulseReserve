import { Component, ElementRef, computed, inject, input, output, signal } from '@angular/core';
import { SeatingLayout } from '../../../core/api/catalog-api.models';
import { SeatAvailability } from '../../../core/api/reservation-api.models';
import { formatMoney } from '../../../shared/format';

export type SeatState = 'available' | 'selected' | 'mine' | 'held' | 'unavailable';

interface SeatView {
  readonly id: string;
  readonly label: string;
  readonly state: SeatState;
  readonly actionable: boolean;
  readonly description: string;
}

interface RowView {
  readonly id: string;
  readonly label: string;
  readonly seats: readonly SeatView[];
}

interface SectionView {
  readonly id: string;
  readonly name: string;
  readonly rows: readonly RowView[];
}

const STATE_TEXT: Record<SeatState, string> = {
  available: 'available',
  selected: 'selected',
  mine: 'held by you',
  held: 'held by someone else',
  unavailable: 'unavailable',
};
const STATE_ICON: Record<SeatState, string> = {
  available: '',
  selected: '✓',
  mine: '★',
  held: '◩',
  unavailable: '✕',
};

/**
 * Presentational seat map rendered from the real seating layout plus real availability.
 * Seat states never rely on colour alone (icon + pattern + accessible name). Keyboard: Tab enters
 * the map once (roving tabindex); arrow keys move between seats, Home/End jump within a row,
 * Enter/Space toggle. Unavailable seats stay focusable (aria-disabled) so their state can be read.
 */
@Component({
  selector: 'app-seat-map',
  templateUrl: './seat-map.html',
  styleUrl: './seat-map.scss',
})
export class SeatMap {
  readonly layout = input.required<SeatingLayout>();
  readonly availability = input.required<ReadonlyMap<string, SeatAvailability>>();
  readonly selected = input.required<ReadonlySet<string>>();
  readonly mine = input<ReadonlySet<string>>(new Set());
  readonly locked = input(false);
  readonly seatToggle = output<string>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly focusedId = signal<string | null>(null);

  protected readonly icon = STATE_ICON;

  protected readonly sections = computed<readonly SectionView[]>(() => {
    const layout = this.layout();
    const availability = this.availability();
    const selected = this.selected();
    const mine = this.mine();
    const tiers = new Map(layout.priceTiers.map((t) => [t.id, t.name]));
    const rowsBySection = groupBy(layout.rows, (r) => r.sectionId);
    const seatsByRow = groupBy(layout.seats, (s) => s.rowId);

    return [...layout.sections]
      .sort((a, b) => a.displayOrder - b.displayOrder || a.name.localeCompare(b.name))
      .map((section) => ({
        id: section.id,
        name: section.name,
        rows: [...(rowsBySection.get(section.id) ?? [])]
          .sort((a, b) => a.displayOrder - b.displayOrder || a.label.localeCompare(b.label))
          .map((row) => ({
            id: row.id,
            label: row.label,
            seats: [...(seatsByRow.get(row.id) ?? [])]
              .sort((a, b) => a.coordinateX - b.coordinateX || a.label.localeCompare(b.label))
              .map((seat): SeatView => {
                const info = availability.get(seat.id);
                const state: SeatState = mine.has(seat.id)
                  ? 'mine'
                  : selected.has(seat.id)
                    ? 'selected'
                    : info?.status === 'Available'
                      ? 'available'
                      : info?.status === 'Held'
                        ? 'held'
                        : 'unavailable';
                const parts = [`Section ${section.name}`, `Row ${row.label}`, `Seat ${seat.label}`];
                const tier = tiers.get(seat.priceTierId);
                if (tier) {
                  parts.push(tier);
                }
                if (info) {
                  parts.push(formatMoney(info.priceAmount, info.currency));
                }
                parts.push(STATE_TEXT[state]);
                return {
                  id: seat.id,
                  label: seat.label,
                  state,
                  actionable: state === 'available' || state === 'selected',
                  description: parts.join(', '),
                };
              }),
          })),
      }));
  });

  /** Rows flattened in reading order, used for arrow-key navigation. */
  private readonly grid = computed(() =>
    this.sections()
      .flatMap((s) => s.rows)
      .filter((r) => r.seats.length > 0)
      .map((r) => r.seats.map((s) => s.id)),
  );

  protected tabbable(seatId: string): boolean {
    const focused = this.focusedId();
    const flat = this.grid().flat();
    return focused !== null && flat.includes(focused) ? focused === seatId : flat[0] === seatId;
  }

  protected onFocus(seatId: string): void {
    this.focusedId.set(seatId);
  }

  protected onActivate(seat: SeatView): void {
    this.focusedId.set(seat.id);
    if (!this.locked() && seat.actionable) {
      this.seatToggle.emit(seat.id);
    }
  }

  protected onKeydown(event: KeyboardEvent, seatId: string): void {
    const grid = this.grid();
    const rowIndex = grid.findIndex((r) => r.includes(seatId));
    if (rowIndex < 0) {
      return;
    }
    const colIndex = grid[rowIndex].indexOf(seatId);
    let target: string | undefined;
    switch (event.key) {
      case 'ArrowRight':
        target = grid[rowIndex][colIndex + 1];
        break;
      case 'ArrowLeft':
        target = grid[rowIndex][colIndex - 1];
        break;
      case 'ArrowDown':
      case 'ArrowUp': {
        const next = grid[rowIndex + (event.key === 'ArrowDown' ? 1 : -1)];
        target = next?.[Math.min(colIndex, next.length - 1)];
        break;
      }
      case 'Home':
        target = grid[rowIndex][0];
        break;
      case 'End':
        target = grid[rowIndex][grid[rowIndex].length - 1];
        break;
      default:
        return;
    }
    event.preventDefault();
    if (target) {
      this.focusedId.set(target);
      this.host.nativeElement
        .querySelector<HTMLElement>(`[data-seat-id="${CSS.escape(target)}"]`)
        ?.focus();
    }
  }
}

function groupBy<T>(items: readonly T[], key: (item: T) => string): Map<string, T[]> {
  const map = new Map<string, T[]>();
  for (const item of items) {
    const k = key(item);
    const bucket = map.get(k);
    if (bucket) {
      bucket.push(item);
    } else {
      map.set(k, [item]);
    }
  }
  return map;
}

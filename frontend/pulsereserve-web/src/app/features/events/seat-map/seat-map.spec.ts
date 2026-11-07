import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SeatingLayout } from '../../../core/api/catalog-api.models';
import { SeatAvailability } from '../../../core/api/reservation-api.models';
import { formatMoney } from '../../../shared/format';
import { SeatMap } from './seat-map';

const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const layout: SeatingLayout = {
  id: id(100),
  venueId: id(101),
  name: 'Main',
  isLocked: true,
  sections: [{ id: id(1), name: 'A', displayOrder: 1 }],
  rows: [
    { id: id(10), sectionId: id(1), label: '1', displayOrder: 1 },
    { id: id(11), sectionId: id(1), label: '2', displayOrder: 2 },
  ],
  seats: [
    // Deliberately out of order: the map must sort by coordinate.
    {
      id: id(22),
      rowId: id(10),
      sectionId: id(1),
      label: '2',
      coordinateX: 2,
      coordinateY: 1,
      priceTierId: id(50),
    },
    {
      id: id(21),
      rowId: id(10),
      sectionId: id(1),
      label: '1',
      coordinateX: 1,
      coordinateY: 1,
      priceTierId: id(50),
    },
    {
      id: id(23),
      rowId: id(10),
      sectionId: id(1),
      label: '3',
      coordinateX: 3,
      coordinateY: 1,
      priceTierId: id(50),
    },
    {
      id: id(24),
      rowId: id(10),
      sectionId: id(1),
      label: '4',
      coordinateX: 4,
      coordinateY: 1,
      priceTierId: id(50),
    },
    {
      id: id(31),
      rowId: id(11),
      sectionId: id(1),
      label: '1',
      coordinateX: 1,
      coordinateY: 2,
      priceTierId: id(50),
    },
  ],
  priceTiers: [{ id: id(50), name: 'Standard' }],
};

const avail = (n: number, status: string): [string, SeatAvailability] => [
  id(n),
  { seatId: id(n), status, priceAmount: 45, currency: 'USD' },
];

@Component({
  imports: [SeatMap],
  template: `<app-seat-map
    [layout]="layout"
    [availability]="availability"
    [selected]="selected()"
    [mine]="mine"
    [locked]="locked()"
    (seatToggle)="toggled.push($event)"
  />`,
})
class Host {
  layout = layout;
  availability = new Map<string, SeatAvailability>([
    avail(21, 'Available'),
    avail(22, 'Available'),
    avail(23, 'Held'),
    // seat 24 is absent from availability -> unavailable
    avail(31, 'Available'),
  ]);
  selected = signal<ReadonlySet<string>>(new Set([id(22)]));
  mine = new Set<string>();
  locked = signal(false);
  toggled: string[] = [];
}

describe('SeatMap', () => {
  async function render() {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const seat = (n: number) => el.querySelector<HTMLButtonElement>(`[data-seat-id="${id(n)}"]`)!;
    return { fixture, el, seat };
  }

  it('renders seats in coordinate order, grouped by section and row', async () => {
    const { el } = await render();
    expect(el.querySelectorAll('[role="group"][aria-label^="Row"]').length).toBe(2);
    const firstRow = el.querySelector('[aria-label="Row 1"]')!;
    const labels = [...firstRow.querySelectorAll('.seat-label')].map((s) => s.textContent?.trim());
    expect(labels).toEqual(['1', '2', '3', '4']);
  });

  it('distinguishes available, selected, held and unavailable seats in text and attributes', async () => {
    const { seat } = await render();
    expect(seat(21).className).toContain('available');
    expect(seat(21).getAttribute('aria-pressed')).toBe('false');
    expect(seat(21).getAttribute('aria-label')).toBe(
      `Section A, Row 1, Seat 1, Standard, ${formatMoney(45, 'USD')}, available`,
    );
    expect(seat(22).className).toContain('selected');
    expect(seat(22).getAttribute('aria-pressed')).toBe('true');
    expect(seat(23).className).toContain('held');
    expect(seat(23).getAttribute('aria-disabled')).toBe('true');
    expect(seat(23).getAttribute('aria-label')).toContain('held by someone else');
    expect(seat(24).className).toContain('unavailable');
    expect(seat(24).getAttribute('aria-label')).toContain('unavailable');
  });

  it('emits toggles for available and selected seats only', async () => {
    const { fixture, seat } = await render();
    seat(21).click();
    seat(22).click();
    seat(23).click();
    seat(24).click();
    expect(fixture.componentInstance.toggled).toEqual([id(21), id(22)]);
  });

  it('does not emit while locked', async () => {
    const { fixture, seat } = await render();
    fixture.componentInstance.locked.set(true);
    await fixture.whenStable();
    seat(21).click();
    expect(fixture.componentInstance.toggled).toEqual([]);
    expect(seat(21).getAttribute('aria-disabled')).toBe('true');
  });

  it('has a single tab stop (roving tabindex) that follows focus', async () => {
    const { el, seat, fixture } = await render();
    const tabbable = () =>
      [...el.querySelectorAll('.seat')].filter((s) => s.getAttribute('tabindex') === '0');
    expect(tabbable()).toEqual([seat(21)]);
    seat(23).focus();
    await fixture.whenStable();
    expect(tabbable()).toEqual([seat(23)]);
  });

  describe('keyboard', () => {
    const press = (el: HTMLElement, key: string) =>
      el.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));

    it('moves focus with arrow keys, including held and unavailable seats', async () => {
      const { seat } = await render();
      seat(21).focus();
      press(seat(21), 'ArrowRight');
      expect(document.activeElement).toBe(seat(22));
      press(seat(22), 'ArrowRight');
      expect(document.activeElement).toBe(seat(23));
      press(seat(23), 'ArrowLeft');
      expect(document.activeElement).toBe(seat(22));
      press(seat(22), 'ArrowDown');
      expect(document.activeElement).toBe(seat(31)); // next row, column clamped
      press(seat(31), 'ArrowUp');
      expect(document.activeElement).toBe(seat(21));
    });

    it('jumps to the row ends with Home and End and stops at the edges', async () => {
      const { seat } = await render();
      seat(22).focus();
      press(seat(22), 'End');
      expect(document.activeElement).toBe(seat(24));
      press(seat(24), 'ArrowRight');
      expect(document.activeElement).toBe(seat(24));
      press(seat(24), 'Home');
      expect(document.activeElement).toBe(seat(21));
    });

    it('prevents default scrolling for handled keys only', async () => {
      const { seat } = await render();
      seat(21).focus();
      const handled = new KeyboardEvent('keydown', {
        key: 'ArrowRight',
        cancelable: true,
        bubbles: true,
      });
      seat(21).dispatchEvent(handled);
      expect(handled.defaultPrevented).toBe(true);
      const other = new KeyboardEvent('keydown', { key: 'a', cancelable: true, bubbles: true });
      seat(21).dispatchEvent(other);
      expect(other.defaultPrevented).toBe(false);
    });

    it('activates a seat with the native button keys (Enter/Space map to click)', async () => {
      // Buttons turn Enter/Space into click events in browsers; assert the control is a real button.
      const { seat } = await render();
      expect(seat(21).tagName).toBe('BUTTON');
      expect(seat(21).getAttribute('type')).toBe('button');
    });
  });
});

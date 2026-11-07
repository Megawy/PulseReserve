import { SeatingLayout } from '../../core/api/catalog-api.models';

/** "Section A · Row 1 · Seat 3" for every seat of a layout, keyed by seat id. */
export function seatNameMap(layout: SeatingLayout | null | undefined): Map<string, string> {
  const names = new Map<string, string>();
  if (!layout) {
    return names;
  }
  const sections = new Map(layout.sections.map((s) => [s.id, s.name]));
  const rows = new Map(layout.rows.map((r) => [r.id, r.label]));
  for (const seat of layout.seats) {
    names.set(
      seat.id,
      `Section ${sections.get(seat.sectionId) ?? '?'} · Row ${rows.get(seat.rowId) ?? '?'} · Seat ${seat.label}`,
    );
  }
  return names;
}

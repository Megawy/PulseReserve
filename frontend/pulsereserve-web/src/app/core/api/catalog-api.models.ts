import { WireId } from './wire-id';

/*
 * Models for the three verified GET endpoints only. Field names/types are taken from the C#
 * response records (Features/<area>/Get*ById/*Response.cs). camelCase property names and the ID
 * shape were confirmed against live API responses on 2026-10-20. Date-time values stay ISO-8601
 * strings (UTC, `Z` suffix; not parsed). `status` is the EventStatus name (e.g. "Published",
 * "OnSale") and stays a string so new backend states do not break parsing.
 */

// ── Public models (plain string IDs; independent of the wire ID shape) ──

export interface Venue {
  readonly id: string;
  readonly name: string;
  readonly street: string;
  readonly city: string;
  readonly country: string;
  readonly postalCode: string;
}

export interface LayoutSection {
  readonly id: string;
  readonly name: string;
  readonly displayOrder: number;
}
export interface LayoutRow {
  readonly id: string;
  readonly sectionId: string;
  readonly label: string;
  readonly displayOrder: number;
}
export interface LayoutSeat {
  readonly id: string;
  readonly rowId: string;
  readonly sectionId: string;
  readonly label: string;
  readonly coordinateX: number;
  readonly coordinateY: number;
  readonly priceTierId: string;
}
export interface LayoutPriceTier {
  readonly id: string;
  readonly name: string;
}
export interface SeatingLayout {
  readonly id: string;
  readonly venueId: string;
  readonly name: string;
  readonly isLocked: boolean;
  readonly sections: readonly LayoutSection[];
  readonly rows: readonly LayoutRow[];
  readonly seats: readonly LayoutSeat[];
  readonly priceTiers: readonly LayoutPriceTier[];
}

export interface EventPrice {
  readonly priceTierId: string;
  readonly amount: number;
  readonly currency: string;
}
export interface CatalogEvent {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly venueId: string;
  readonly seatingLayoutId: string | null;
  readonly scheduledStartUtc: string;
  readonly scheduledEndUtc: string;
  readonly status: string;
  readonly publishedAtUtc: string | null;
  readonly onSaleAtUtc: string | null;
  readonly cancelledAtUtc: string | null;
  readonly cancellationReason: string | null;
  readonly prices: readonly EventPrice[];
}

// ── Wire models (PROVISIONAL ID shape; see wire-id.ts) ──

export interface VenueWire {
  id: WireId;
  name: string;
  street: string;
  city: string;
  country: string;
  postalCode: string;
}
export interface SeatingLayoutWire {
  id: WireId;
  venueId: WireId;
  name: string;
  isLocked: boolean;
  sections: { id: WireId; name: string; displayOrder: number }[];
  rows: { id: WireId; sectionId: WireId; label: string; displayOrder: number }[];
  seats: {
    id: WireId;
    rowId: WireId;
    sectionId: WireId;
    label: string;
    coordinateX: number;
    coordinateY: number;
    priceTierId: WireId;
  }[];
  priceTiers: { id: WireId; name: string }[];
}
export interface CatalogEventWire {
  id: WireId;
  name: string;
  description: string;
  venueId: WireId;
  seatingLayoutId: WireId | null;
  scheduledStartUtc: string;
  scheduledEndUtc: string;
  status: string;
  publishedAtUtc: string | null;
  onSaleAtUtc: string | null;
  cancelledAtUtc: string | null;
  cancellationReason: string | null;
  prices: { priceTierId: WireId; amount: number; currency: string }[];
}

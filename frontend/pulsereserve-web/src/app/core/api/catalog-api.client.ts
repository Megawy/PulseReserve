import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { API_BASE_URL } from '../config/app-config';
import {
  CatalogEvent,
  CatalogEventWire,
  SeatingLayout,
  SeatingLayoutWire,
  Venue,
  VenueWire,
} from './catalog-api.models';
import { WireFormatError, unwrapOptionalWireId, unwrapWireId } from './wire-id';

/**
 * Client for the Catalog read endpoints ONLY:
 *   GET /api/catalog/venues/{venueId}
 *   GET /api/catalog/seating-layouts/{layoutId}
 *   GET /api/catalog/events           (customer-visible events: Published, OnSale, Closed)
 *   GET /api/catalog/events/{eventId}
 * No write operations are called. HTTP failures surface as
 * ApiError via the interceptor; unexpected response shapes surface as WireFormatError.
 */
@Injectable({ providedIn: 'root' })
export class CatalogApiClient {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(API_BASE_URL)}/catalog`;

  getVenue(venueId: string): Observable<Venue> {
    return this.http
      .get<VenueWire>(`${this.base}/venues/${encodeURIComponent(venueId)}`)
      .pipe(map(toVenue));
  }

  getSeatingLayout(layoutId: string): Observable<SeatingLayout> {
    return this.http
      .get<SeatingLayoutWire>(`${this.base}/seating-layouts/${encodeURIComponent(layoutId)}`)
      .pipe(map(toSeatingLayout));
  }

  listEvents(): Observable<CatalogEvent[]> {
    return this.http.get<CatalogEventWire[]>(`${this.base}/events`).pipe(
      map((items) => {
        if (!Array.isArray(items)) {
          throw new WireFormatError('events');
        }
        return items.map(toCatalogEvent);
      }),
    );
  }

  getEvent(eventId: string): Observable<CatalogEvent> {
    return this.http
      .get<CatalogEventWire>(`${this.base}/events/${encodeURIComponent(eventId)}`)
      .pipe(map(toCatalogEvent));
  }
}

function toVenue(w: VenueWire): Venue {
  return {
    id: unwrapWireId(w.id, 'venue.id'),
    name: w.name,
    street: w.street,
    city: w.city,
    country: w.country,
    postalCode: w.postalCode,
  };
}

function toSeatingLayout(w: SeatingLayoutWire): SeatingLayout {
  return {
    id: unwrapWireId(w.id, 'layout.id'),
    venueId: unwrapWireId(w.venueId, 'layout.venueId'),
    name: w.name,
    isLocked: w.isLocked,
    sections: w.sections.map((s) => ({
      id: unwrapWireId(s.id, 'section.id'),
      name: s.name,
      displayOrder: s.displayOrder,
    })),
    rows: w.rows.map((r) => ({
      id: unwrapWireId(r.id, 'row.id'),
      sectionId: unwrapWireId(r.sectionId, 'row.sectionId'),
      label: r.label,
      displayOrder: r.displayOrder,
    })),
    seats: w.seats.map((s) => ({
      id: unwrapWireId(s.id, 'seat.id'),
      rowId: unwrapWireId(s.rowId, 'seat.rowId'),
      sectionId: unwrapWireId(s.sectionId, 'seat.sectionId'),
      label: s.label,
      coordinateX: s.coordinateX,
      coordinateY: s.coordinateY,
      priceTierId: unwrapWireId(s.priceTierId, 'seat.priceTierId'),
    })),
    priceTiers: w.priceTiers.map((t) => ({
      id: unwrapWireId(t.id, 'priceTier.id'),
      name: t.name,
    })),
  };
}

function toCatalogEvent(w: CatalogEventWire): CatalogEvent {
  return {
    id: unwrapWireId(w.id, 'event.id'),
    name: w.name,
    description: w.description,
    venueId: unwrapWireId(w.venueId, 'event.venueId'),
    seatingLayoutId: unwrapOptionalWireId(w.seatingLayoutId, 'event.seatingLayoutId'),
    scheduledStartUtc: w.scheduledStartUtc,
    scheduledEndUtc: w.scheduledEndUtc,
    status: w.status,
    publishedAtUtc: w.publishedAtUtc ?? null,
    onSaleAtUtc: w.onSaleAtUtc ?? null,
    cancelledAtUtc: w.cancelledAtUtc ?? null,
    cancellationReason: w.cancellationReason ?? null,
    prices: w.prices.map((p) => ({
      priceTierId: unwrapWireId(p.priceTierId, 'price.priceTierId'),
      amount: p.amount,
      currency: p.currency,
    })),
  };
}

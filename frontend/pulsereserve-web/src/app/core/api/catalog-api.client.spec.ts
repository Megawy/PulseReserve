import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { CatalogApiClient } from './catalog-api.client';
import { SeatingLayout } from './catalog-api.models';
import { WireFormatError } from './wire-id';

/**
 * FIXTURES BELOW ARE SOURCE-DERIVED AND PROVISIONAL. They were written from the C# response
 * records and the inferred `{ value }` ID shape; they were NOT captured from a running backend
 * (which currently does not build). Passing tests show the client maps these fixtures; they do
 * not confirm wire compatibility.
 */
const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const wire = (n: number) => ({ value: id(n) });

describe('CatalogApiClient (source-derived provisional fixtures)', () => {
  let client: CatalogApiClient;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    client = TestBed.inject(CatalogApiClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('getVenue: GETs the venue URL and maps IDs to plain strings', () => {
    let result: unknown;
    client.getVenue(id(1)).subscribe((v) => (result = v));
    const req = controller.expectOne(`/api/catalog/venues/${id(1)}`);
    expect(req.request.method).toBe('GET');
    req.flush({ id: wire(1), name: 'Hall', street: 'S', city: 'C', country: 'K', postalCode: 'P' });
    expect(result).toEqual({
      id: id(1),
      name: 'Hall',
      street: 'S',
      city: 'C',
      country: 'K',
      postalCode: 'P',
    });
  });

  it('getSeatingLayout: GETs the layout URL and maps nested IDs', () => {
    let result: SeatingLayout | undefined;
    client.getSeatingLayout(id(2)).subscribe((v) => (result = v));
    const req = controller.expectOne(`/api/catalog/seating-layouts/${id(2)}`);
    expect(req.request.method).toBe('GET');
    req.flush({
      id: wire(2),
      venueId: wire(1),
      name: 'Main',
      isLocked: false,
      sections: [{ id: wire(3), name: 'A', displayOrder: 1 }],
      rows: [{ id: wire(4), sectionId: wire(3), label: 'R1', displayOrder: 1 }],
      seats: [
        {
          id: wire(5),
          rowId: wire(4),
          sectionId: wire(3),
          label: '1',
          coordinateX: 0,
          coordinateY: 0,
          priceTierId: wire(6),
        },
      ],
      priceTiers: [{ id: wire(6), name: 'Standard' }],
    });
    expect(result).toEqual({
      id: id(2),
      venueId: id(1),
      name: 'Main',
      isLocked: false,
      sections: [{ id: id(3), name: 'A', displayOrder: 1 }],
      rows: [{ id: id(4), sectionId: id(3), label: 'R1', displayOrder: 1 }],
      seats: [
        {
          id: id(5),
          rowId: id(4),
          sectionId: id(3),
          label: '1',
          coordinateX: 0,
          coordinateY: 0,
          priceTierId: id(6),
        },
      ],
      priceTiers: [{ id: id(6), name: 'Standard' }],
    });
  });

  it('getEvent: GETs the event URL; maps IDs, optional layout ID and prices', () => {
    let result: unknown;
    client.getEvent(id(7)).subscribe((v) => (result = v));
    const req = controller.expectOne(`/api/catalog/events/${id(7)}`);
    expect(req.request.method).toBe('GET');
    req.flush({
      id: wire(7),
      name: 'Show',
      description: 'D',
      venueId: wire(1),
      seatingLayoutId: null,
      scheduledStartUtc: '2030-01-01T10:00:00Z',
      scheduledEndUtc: '2030-01-01T12:00:00Z',
      status: 'Draft',
      publishedAtUtc: null,
      onSaleAtUtc: null,
      cancelledAtUtc: null,
      cancellationReason: null,
      prices: [{ priceTierId: wire(6), amount: 12.5, currency: 'USD' }],
    });
    expect(result).toMatchObject({
      id: id(7),
      venueId: id(1),
      seatingLayoutId: null,
      status: 'Draft',
      prices: [{ priceTierId: id(6), amount: 12.5, currency: 'USD' }],
    });
  });

  it('surfaces a WireFormatError (not a crash) when an ID has an unexpected shape', () => {
    let error: unknown;
    client.getVenue(id(1)).subscribe({ error: (e: unknown) => (error = e) });
    controller
      .expectOne(`/api/catalog/venues/${id(1)}`)
      .flush({ id: id(1), name: 'Hall', street: '', city: '', country: '', postalCode: '' });
    expect(error).toBeInstanceOf(WireFormatError);
  });

  it('encodes the path parameter', () => {
    let status: number | undefined;
    client.getEvent('a/b').subscribe({ error: (e: { status: number }) => (status = e.status) });
    controller
      .expectOne('/api/catalog/events/a%2Fb')
      .flush({}, { status: 404, statusText: 'Not Found' });
    expect(status).toBe(404);
  });
});

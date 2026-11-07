import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { formatMoney } from '../../../shared/format';
import { EventList } from './event-list';
import { problemDetailsInterceptor } from '../../../core/api/problem-details.interceptor';
import { withInterceptors } from '@angular/common/http';

const guid = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const wire = (n: number) => ({ value: guid(n) });

const wireEvent = (n: number, over: object = {}) => ({
  id: wire(n),
  name: `Event ${n}`,
  description: `About event ${n}`,
  venueId: wire(900),
  seatingLayoutId: wire(800),
  scheduledStartUtc: '2026-12-01T19:00:00Z',
  scheduledEndUtc: '2026-12-01T22:00:00Z',
  status: 'OnSale',
  publishedAtUtc: '2026-10-01T00:00:00Z',
  onSaleAtUtc: '2026-10-02T00:00:00Z',
  cancelledAtUtc: null,
  cancellationReason: null,
  prices: [
    { priceTierId: wire(1), amount: 80, currency: 'USD' },
    { priceTierId: wire(2), amount: 45, currency: 'USD' },
  ],
  ...over,
});

describe('EventList', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([problemDetailsInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render() {
    const fixture = TestBed.createComponent(EventList);
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('shows a loading state while the request is in flight', async () => {
    const { el } = await render();
    expect(el.textContent).toContain('Loading events');
    http.expectOne('/api/catalog/events');
  });

  it('maps the real API fields into cards that link to the detail page', async () => {
    const { fixture, el } = await render();
    http
      .expectOne('/api/catalog/events')
      .flush([wireEvent(1), wireEvent(2, { status: 'Published' })]);
    await fixture.whenStable();

    const cards = el.querySelectorAll('li.card');
    expect(cards.length).toBe(2);
    const link = cards[0].querySelector('a')!;
    expect(link.textContent).toBe('Event 1');
    expect(link.getAttribute('href')).toBe(`/events/${guid(1)}`);
    expect(cards[0].textContent).toContain('About event 1');
    expect(cards[0].textContent).toContain('On sale');
    expect(cards[0].textContent).toContain(`From ${formatMoney(45, 'USD')}`); // lowest REAL price, nothing invented
    expect(cards[1].textContent).toContain('Coming soon');
    expect(el.querySelector('time')!.getAttribute('datetime')).toBe('2026-12-01T19:00:00Z');
  });

  it('shows no price when the event has none, or when currencies differ', async () => {
    const { fixture, el } = await render();
    http.expectOne('/api/catalog/events').flush([
      wireEvent(1, { prices: [] }),
      wireEvent(2, {
        prices: [
          { priceTierId: wire(1), amount: 10, currency: 'USD' },
          { priceTierId: wire(2), amount: 10, currency: 'EUR' },
        ],
      }),
    ]);
    await fixture.whenStable();
    expect(el.querySelector('.price')).toBeNull();
  });

  it('shows an empty state for an empty list', async () => {
    const { fixture, el } = await render();
    http.expectOne('/api/catalog/events').flush([]);
    await fixture.whenStable();
    expect(el.textContent).toContain('No events available');
    expect(el.querySelector('li.card')).toBeNull();
  });

  it('shows an error with retry for network failures and recovers on retry', async () => {
    const { fixture, el } = await render();
    http.expectOne('/api/catalog/events').error(new ProgressEvent('error'));
    await fixture.whenStable();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Could not reach the server');

    el.querySelector<HTMLButtonElement>('[role="alert"] button')!.click();
    await fixture.whenStable();
    http.expectOne('/api/catalog/events').flush([wireEvent(1)]);
    await fixture.whenStable();
    expect(el.querySelectorAll('li.card').length).toBe(1);
    expect(el.querySelector('[role="alert"]')).toBeNull();
  });

  it('does not offer retry for a 4xx answer', async () => {
    const { fixture, el } = await render();
    http
      .expectOne('/api/catalog/events')
      .flush({ title: 'Bad', detail: 'Nope' }, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Nope');
    expect(el.querySelector('[role="alert"] button')).toBeNull();
  });

  it('surfaces a malformed response as an error instead of rendering garbage', async () => {
    const { fixture, el } = await render();
    http.expectOne('/api/catalog/events').flush({ not: 'a list' });
    await fixture.whenStable();
    expect(el.querySelector('[role="alert"]')).not.toBeNull();
  });
});

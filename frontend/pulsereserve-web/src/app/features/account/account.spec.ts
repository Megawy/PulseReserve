import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiError } from '../../core/api/api-error';
import { CatalogApiClient } from '../../core/api/catalog-api.client';
import { OrderApiClient } from '../../core/api/order-api.client';
import { Order } from '../../core/api/order-api.models';
import { formatMoney } from '../../shared/format';
import { OrderDetail } from './order-detail';
import { OrderList } from './order-list';

const order = (n: number, over: Partial<Order> = {}): Order => ({
  orderId: `00000000-0000-4000-8000-00000000000${n}`,
  reservationId: 'r',
  eventId: 'e1',
  status: 'Paid',
  currency: 'USD',
  totalAmount: 91,
  createdAtUtc: '2030-01-01T10:00:00Z',
  paymentDeadlineUtc: '2030-01-01T10:10:00Z',
  lines: [
    { seatId: 's1', quantity: 1, unitPrice: 45.5, lineTotal: 45.5 },
    { seatId: 's2', quantity: 1, unitPrice: 45.5, lineTotal: 45.5 },
  ],
  payment: null,
  ...over,
});

const event = {
  id: 'e1',
  name: 'Smoke Concert',
  seatingLayoutId: 'l1',
  scheduledStartUtc: '2030-03-01T20:00:00Z',
  scheduledEndUtc: '2030-03-01T23:00:00Z',
};
const layout = {
  id: 'l1',
  sections: [{ id: 'sec', name: 'Floor', displayOrder: 1 }],
  rows: [{ id: 'row', sectionId: 'sec', label: 'A', displayOrder: 1 }],
  seats: [
    { id: 's1', rowId: 'row', sectionId: 'sec', label: '1' },
    { id: 's2', rowId: 'row', sectionId: 'sec', label: '2' },
  ],
  priceTiers: [],
};

describe('account pages', () => {
  let orders: Record<string, ReturnType<typeof vi.fn>>;
  let catalog: Record<string, ReturnType<typeof vi.fn>>;

  function setup(orderId = '') {
    orders = {
      listOrders: vi.fn(() => of([order(1), order(2, { status: 'PendingPayment' })])),
      getOrder: vi.fn(() => of(order(1))),
      getTickets: vi.fn(() =>
        of([
          {
            ticketId: 'abcdefghijklmnopqrstuvwxyz',
            seatId: 's1',
            eventId: 'e1',
            status: 'Valid',
            issuedAtUtc: 'x',
          },
        ]),
      ),
    };
    catalog = {
      getEvent: vi.fn(() => of(event)),
      getSeatingLayout: vi.fn(() => of(layout)),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: OrderApiClient, useValue: orders },
        { provide: CatalogApiClient, useValue: catalog },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of({ get: () => orderId }),
            queryParamMap: of({ get: () => null }),
          },
        },
      ],
    });
  }

  it('lists my orders with the event name, seat count, server total and status badge', async () => {
    setup();
    const fixture = TestBed.createComponent(OrderList);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const rows = el.querySelectorAll('li.order-row');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Smoke Concert');
    expect(rows[0].textContent).toContain('2 seats');
    expect(rows[0].textContent).toContain(formatMoney(91, 'USD'));
    expect(rows[0].querySelector('.badge')?.textContent).toBe('Paid (simulated)');
    expect(rows[1].querySelector('.badge')?.textContent).toBe('Awaiting payment');
    expect(rows[0].querySelector('a')?.getAttribute('href')).toBe(
      '/account/orders/00000000-0000-4000-8000-000000000001',
    );
    expect(catalog['getEvent']).toHaveBeenCalledTimes(1); // one lookup per distinct event
  });

  it('falls back to a generic event name when the event lookup fails, instead of failing the page', async () => {
    setup();
    catalog['getEvent'].mockReturnValue(
      throwError(() => new ApiError('http', 500, null, null, 'x')),
    );
    const fixture = TestBed.createComponent(OrderList);
    await fixture.whenStable();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('li.order-row')?.textContent,
    ).toContain('Event');
  });

  it('shows an empty state with a link to events', async () => {
    setup();
    orders['listOrders'].mockReturnValue(of([]));
    const fixture = TestBed.createComponent(OrderList);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('No orders yet');
    expect(el.querySelector('a.btn')?.getAttribute('href')).toBe('/');
  });

  it('shows a retryable error and recovers', async () => {
    setup();
    orders['listOrders'].mockReturnValueOnce(
      throwError(() => new ApiError('network', 0, null, null, 'Could not reach the server.')),
    );
    const fixture = TestBed.createComponent(OrderList);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role="alert"]')?.textContent).toContain(
      'Could not reach the server.',
    );
    el.querySelector<HTMLButtonElement>('[role="alert"] button')!.click();
    await fixture.whenStable();
    expect(el.querySelectorAll('li.order-row')).toHaveLength(2);
  });

  it('order detail shows lines with real seat names, the total and the full ticket ids of a paid order', async () => {
    setup('00000000-0000-4000-8000-000000000001');
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('h1')?.textContent).toContain('Smoke Concert');
    expect(el.querySelector('.lines')?.textContent).toContain('Section Floor · Row A · Seat 1');
    expect(el.querySelector('[data-testid="order-total"]')?.textContent).toBe(
      formatMoney(91, 'USD'),
    );
    expect(el.querySelector('[data-testid="ticket-id"]')?.textContent).toBe(
      'abcdefghijklmnopqrstuvwxyz',
    );
    expect(orders['getTickets']).toHaveBeenCalled();
  });

  it('order detail does not ask for tickets of an unpaid order and explains its state', async () => {
    setup('x');
    orders['getOrder'].mockReturnValue(of(order(3, { status: 'RefundRequired' })));
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(orders['getTickets']).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Refund pending');
    expect(el.textContent).toContain('simulated');
    expect(el.querySelector('[data-testid="ticket-id"]')).toBeNull();
  });

  it('order detail reports a missing or foreign order as not found without retry', async () => {
    setup('nope');
    orders['getOrder'].mockReturnValue(
      throwError(
        () =>
          new ApiError(
            'problem',
            404,
            'Order.NotFound',
            'The order was not found.',
            'The order was not found.',
          ),
      ),
    );
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Order not found');
    expect(el.querySelector('[role="alert"] button')).toBeNull();
  });

  it('says tickets are being prepared when none have been issued yet', async () => {
    setup('1');
    orders['getTickets'].mockReturnValue(of([]));
    const fixture = TestBed.createComponent(OrderDetail);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'Your tickets are being prepared',
    );
  });
});

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const DIST_DIR = path.resolve(__dirname, '../frontend/pulsereserve-web/dist/pulsereserve-web/browser');

const PORT = 4255;

// Mock Data
const eventGuid1 = '11111111-1111-1111-1111-111111111111';
const eventGuid2 = '22222222-2222-2222-2222-222222222222';
const eventGuid3 = '33333333-3333-3333-3333-333333333333';

const venueGuid = 'aaaa1111-1111-1111-1111-111111111111';
const layoutGuid = 'bbbb2222-2222-2222-2222-222222222222';

const tierVipGuid = 'cccc1111-1111-1111-1111-111111111111';
const tierGoldGuid = 'cccc2222-2222-2222-2222-222222222222';
const tierSilverGuid = 'cccc3333-3333-3333-3333-333333333333';

const venueWire = {
  id: { value: venueGuid },
  name: 'Cairo Grand Arena',
  street: 'Olympic City Boulevard, New Administrative Capital',
  city: 'Cairo',
  country: 'EG',
  postalCode: '11835',
};

// Generate Sections, Rows, Seats for Layout
const sections = [
  { id: { value: 'dddd1111-1111-1111-1111-111111111111' }, name: 'Royal VIP Golden Circle', displayOrder: 1 },
  { id: { value: 'dddd2222-2222-2222-2222-222222222222' }, name: 'Orchestra Front', displayOrder: 2 },
  { id: { value: 'dddd3333-3333-3333-3333-333333333333' }, name: 'Grand Balcony Terrace', displayOrder: 3 },
];

const rows = [];
const seats = [];
const availabilitySeats = [];

const sectionConfigs = [
  { secId: sections[0].id.value, secName: 'Royal VIP', tierId: tierVipGuid, price: 250, rowLabels: ['VIP-A', 'VIP-B'], seatsPerRow: 10 },
  { secId: sections[1].id.value, secName: 'Orchestra', tierId: tierGoldGuid, price: 150, rowLabels: ['ORCH-1', 'ORCH-2', 'ORCH-3'], seatsPerRow: 12 },
  { secId: sections[2].id.value, secName: 'Balcony', tierId: tierSilverGuid, price: 80, rowLabels: ['BALC-A', 'BALC-B', 'BALC-C'], seatsPerRow: 14 },
];

let rowIndex = 1;
let seatCount = 0;

for (const cfg of sectionConfigs) {
  for (const rLabel of cfg.rowLabels) {
    const rowGuid = `f000${String(rowIndex).padStart(4, '0')}-0000-0000-0000-000000000000`;
    rows.push({
      id: { value: rowGuid },
      sectionId: { value: cfg.secId },
      label: rLabel,
      displayOrder: rowIndex++,
    });

    for (let sNum = 1; sNum <= cfg.seatsPerRow; sNum++) {
      seatCount++;
      const seatGuid = `e000${String(seatCount).padStart(4, '0')}-0000-0000-0000-000000000000`;
      seats.push({
        id: { value: seatGuid },
        rowId: { value: rowGuid },
        sectionId: { value: cfg.secId },
        label: `${rLabel}-${sNum}`,
        coordinateX: sNum * 40,
        coordinateY: rowIndex * 35,
        priceTierId: { value: cfg.tierId },
      });

      // Realistic mix of availability
      let status = 'Available';
      if (sNum === 3 || sNum === 4) {
        status = 'Held'; // Held by others
      } else if (sNum === 7 || sNum === 8) {
        status = 'Unavailable'; // Sold
      }

      availabilitySeats.push({
        seatId: seatGuid,
        status: status,
        priceAmount: cfg.price,
        currency: 'USD',
      });
    }
  }
}

const seatingLayoutWire = {
  id: { value: layoutGuid },
  venueId: { value: venueGuid },
  name: 'Main Symphony Arena Layout',
  isLocked: true,
  sections,
  rows,
  seats,
  priceTiers: [
    { id: { value: tierVipGuid }, name: 'Royal VIP' },
    { id: { value: tierGoldGuid }, name: 'Gold Orchestra' },
    { id: { value: tierSilverGuid }, name: 'Silver Balcony' },
  ],
};

const eventsWire = [
  {
    id: { value: eventGuid1 },
    name: 'Pulse World Tour — Live Concert 2026',
    description: 'High-concurrency global concert flash sale. Featuring immersive spatial audio, interactive stadium seat selection, zero double-booking concurrency engine, and instant verified mobile ticketing.',
    venueId: { value: venueGuid },
    seatingLayoutId: { value: layoutGuid },
    scheduledStartUtc: '2026-11-20T19:00:00Z',
    scheduledEndUtc: '2026-11-20T23:30:00Z',
    status: 'OnSale',
    publishedAtUtc: '2026-10-01T10:00:00Z',
    onSaleAtUtc: '2026-10-15T12:00:00Z',
    cancelledAtUtc: null,
    cancellationReason: null,
    prices: [
      { priceTierId: { value: tierVipGuid }, amount: 250, currency: 'USD' },
      { priceTierId: { value: tierGoldGuid }, amount: 150, currency: 'USD' },
      { priceTierId: { value: tierSilverGuid }, amount: 80, currency: 'USD' },
    ],
  },
  {
    id: { value: eventGuid2 },
    name: 'UEFA Champions League Finals 2026 Screening & Fan Arena',
    description: 'The biggest sporting night of the year broadcasted live on ultra-giant 8K LED stadium displays with pitch-side sound and live cheering zones.',
    venueId: { value: venueGuid },
    seatingLayoutId: { value: layoutGuid },
    scheduledStartUtc: '2026-12-05T18:00:00Z',
    scheduledEndUtc: '2026-12-05T22:00:00Z',
    status: 'OnSale',
    publishedAtUtc: '2026-10-10T10:00:00Z',
    onSaleAtUtc: '2026-10-20T12:00:00Z',
    cancelledAtUtc: null,
    cancellationReason: null,
    prices: [
      { priceTierId: { value: tierVipGuid }, amount: 180, currency: 'USD' },
      { priceTierId: { value: tierGoldGuid }, amount: 110, currency: 'USD' },
      { priceTierId: { value: tierSilverGuid }, amount: 60, currency: 'USD' },
    ],
  },
  {
    id: { value: eventGuid3 },
    name: 'Global Cloud & Software Architecture Summit 2026',
    description: 'Premier tech conference bringing together world-class software architects, distributed systems engineers, and cloud pioneers for 3 intensive days.',
    venueId: { value: venueGuid },
    seatingLayoutId: { value: layoutGuid },
    scheduledStartUtc: '2027-01-15T09:00:00Z',
    scheduledEndUtc: '2027-01-17T17:00:00Z',
    status: 'Published',
    publishedAtUtc: '2026-10-22T08:00:00Z',
    onSaleAtUtc: null,
    cancelledAtUtc: null,
    cancellationReason: null,
    prices: [
      { priceTierId: { value: tierVipGuid }, amount: 499, currency: 'USD' },
      { priceTierId: { value: tierGoldGuid }, amount: 299, currency: 'USD' },
    ],
  },
];

const activeReservation = {
  reservationId: 'res-a1b2c3d4-0000-0000-0000-000000000001',
  eventId: eventGuid1,
  customerId: 'u-admin-1',
  status: 'Held',
  createdAtUtc: new Date(Date.now() - 60 * 1000).toISOString(),
  expiresAtUtc: new Date(Date.now() + 9 * 60 * 1000).toISOString(),
  totalAmount: 500,
  currency: 'USD',
  seats: [
    { seatId: seats[0].id.value, priceAmount: 250, currency: 'USD' },
    { seatId: seats[1].id.value, priceAmount: 250, currency: 'USD' },
  ],
};

const adminOverview = {
  paymentExceptionsOpen: 0,
  paymentAnomaliesOpen: 0,
  reconciliationNeedsReview: 0,
  reconciliationPending: 2,
  refundsNeedingAttention: 0,
  refundsOpen: 3,
  outboxNeedsReview: 0,
  outboxPending: 4,
  ordersAwaitingRefund: 1,
  oldestOpenPaymentAnomalyAgeSeconds: null,
  oldestOpenPaymentExceptionAgeSeconds: null,
  tasksByState: { Done: 8940, Pending: 2, Processing: 0 },
  refundsByState: { Succeeded: 342, Pending: 3, Requested: 1 },
  outboxByState: { Done: 45210, Pending: 4 },
};

const mimeTypes = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.woff2': 'font/woff2',
};

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const pathname = url.pathname;

  // Hubs
  if (pathname.startsWith('/hubs')) {
    res.writeHead(404);
    return res.end();
  }

  // API Responses
  if (pathname === '/api/identity/session') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify({
      isAuthenticated: true,
      user: {
        id: 'u-admin-1',
        email: 'ahmed.engineer@pulsereserve.com',
        displayName: 'Ahmed Mostafa (Admin)',
      },
      isAdmin: true,
    }));
  }

  if (pathname === '/api/catalog/events') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify(eventsWire));
  }

  if (pathname.startsWith('/api/catalog/events/')) {
    const id = pathname.replace('/api/catalog/events/', '');
    const found = eventsWire.find((e) => e.id.value === id) ?? eventsWire[0];
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify(found));
  }

  if (pathname.startsWith('/api/catalog/venues/')) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify(venueWire));
  }

  if (pathname.startsWith('/api/catalog/seating-layouts/')) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify(seatingLayoutWire));
  }

  if (pathname.includes('/availability')) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify({
      eventId: eventGuid1,
      seats: availabilitySeats,
    }));
  }

  if (pathname === '/api/reservations' && req.method === 'POST') {
    res.writeHead(201, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify({
      reservation: activeReservation,
      replayed: false,
    }));
  }

  if (pathname.startsWith('/api/reservations/')) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify(activeReservation));
  }

  if (pathname === '/api/orders' && req.method === 'POST') {
    res.writeHead(201, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify({
      orderId: 'ord-8832',
      reservationId: activeReservation.reservationId,
      eventId: eventGuid1,
      status: 'PendingPayment',
      currency: 'USD',
      totalAmount: 500,
      createdAtUtc: new Date().toISOString(),
      paymentDeadlineUtc: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
      lines: [
        { seatId: seats[0].id.value, quantity: 1, unitPrice: 250, lineTotal: 250 },
        { seatId: seats[1].id.value, quantity: 1, unitPrice: 250, lineTotal: 250 },
      ],
      payment: null,
    }));
  }

  if (pathname.includes('/payment-options')) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify({
      currency: 'USD',
      eligible: true,
      providers: [
        { provider: 'Stripe', displayName: 'Credit Card (Stripe)', isSimulated: false },
        { provider: 'Paymob', displayName: 'Paymob E-Wallet & Cards', isSimulated: false },
        { provider: 'Simulator', displayName: 'Development Simulator', isSimulated: true },
      ],
    }));
  }

  if (pathname === '/api/orders' || pathname === '/api/account/orders') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify([
      {
        orderId: 'ord-8831',
        reservationId: 'res-old-1',
        eventId: eventGuid1,
        status: 'Paid',
        currency: 'USD',
        totalAmount: 300,
        createdAtUtc: '2026-11-06T15:30:00Z',
        paymentDeadlineUtc: '2026-11-06T15:45:00Z',
        lines: [
          { seatId: seats[2].id.value, quantity: 1, unitPrice: 150, lineTotal: 150 },
          { seatId: seats[3].id.value, quantity: 1, unitPrice: 150, lineTotal: 150 },
        ],
        payment: {
          attemptId: 'att-1',
          attemptNumber: 1,
          status: 'Succeeded',
          provider: 'Stripe',
          isSimulated: false,
          simulatorToken: null,
          createdAtUtc: '2026-11-06T15:32:00Z',
          completedAtUtc: '2026-11-06T15:33:00Z',
        },
      },
    ]));
  }

  if (pathname.includes('/tickets')) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify([
      { ticketId: 'TCK-VIP-9941', seatId: seats[0].id.value, eventId: eventGuid1, status: 'Valid', issuedAtUtc: '2026-11-06T15:33:00Z' },
      { ticketId: 'TCK-VIP-9942', seatId: seats[1].id.value, eventId: eventGuid1, status: 'Valid', issuedAtUtc: '2026-11-06T15:33:00Z' },
    ]));
  }

  if (pathname === '/api/admin/payments/overview') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify(adminOverview));
  }

  if (pathname === '/api/admin/payments/providers') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify([
      { name: 'Stripe', isSimulated: false, isAvailable: true, supportsPaymentLookup: true, supportsRefunds: true, supportsIdempotentInitiation: true },
      { name: 'Paymob', isSimulated: false, isAvailable: true, supportsPaymentLookup: true, supportsRefunds: true, supportsIdempotentInitiation: true },
      { name: 'Simulator', isSimulated: true, isAvailable: true, supportsPaymentLookup: true, supportsRefunds: true, supportsIdempotentInitiation: true },
    ]));
  }

  if (pathname === '/api/admin/payments/reconciliation-tasks') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify([
      { id: 'rec-001', kind: 'FinalizeSuccessfulPayment', subjectId: 'ord-8831', state: 'Done', attempts: 1, lastOutcome: 'Settled & Reconciled', lastError: null, nextAttemptAtUtc: '2026-11-07T12:00:00Z', createdAtUtc: '2026-11-07T06:00:00Z', completedAtUtc: '2026-11-07T06:01:00Z', provider: 'Stripe' },
      { id: 'rec-002', kind: 'ResolveStaleAttempt', subjectId: 'ord-8830', state: 'Done', attempts: 1, lastOutcome: 'Expired & Released', lastError: null, nextAttemptAtUtc: '2026-11-07T12:00:00Z', createdAtUtc: '2026-11-07T05:30:00Z', completedAtUtc: '2026-11-07T05:35:00Z', provider: 'Paymob' },
      { id: 'rec-003', kind: 'FinalizeSuccessfulPayment', subjectId: 'ord-8829', state: 'Pending', attempts: 0, lastOutcome: null, lastError: null, nextAttemptAtUtc: '2026-11-07T07:10:00Z', createdAtUtc: '2026-11-07T07:05:00Z', completedAtUtc: null, provider: 'Stripe' },
    ]));
  }

  if (pathname === '/api/admin/payments/refunds') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify([
      { refundId: 'ref-001', orderId: 'ord-8799', paymentAttemptId: 'att-1', amount: 150, currency: 'USD', reason: 'AdminRequested', state: 'Succeeded', provider: 'Stripe', isSimulated: false, submissionNumber: 1, attempts: 1, lastError: null, requestedBy: 'Ahmed Mostafa', createdAtUtc: '2026-11-06T14:20:00Z', completedAtUtc: '2026-11-06T14:22:00Z', nextAttemptAtUtc: '2026-11-06T14:20:00Z', updatedAtUtc: '2026-11-06T14:22:00Z' },
      { refundId: 'ref-002', orderId: 'ord-8785', paymentAttemptId: 'att-2', amount: 80, currency: 'USD', reason: 'PaymentAfterSaleFailed', state: 'Pending', provider: 'Paymob', isSimulated: false, submissionNumber: 1, attempts: 1, lastError: null, requestedBy: null, createdAtUtc: '2026-11-07T02:10:00Z', completedAtUtc: null, nextAttemptAtUtc: '2026-11-07T07:15:00Z', updatedAtUtc: '2026-11-07T02:10:00Z' },
    ]));
  }

  if (pathname === '/api/admin/payments/outbox-events') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify([
      { id: 'evt-101', eventType: 'SeatsReservedIntegrationEvent', aggregateType: 'Reservation', payloadExcerpt: '{"ReservationId":"res-901","SeatsCount":2}', state: 'Done', attempts: 1, createdAtUtc: '2026-11-07T06:40:00Z', processedAtUtc: '2026-11-07T06:40:01Z' },
      { id: 'evt-102', eventType: 'PaymentCompletedIntegrationEvent', aggregateType: 'Payment', payloadExcerpt: '{"OrderId":"ord-8831","Amount":300}', state: 'Done', attempts: 1, createdAtUtc: '2026-11-07T06:42:00Z', processedAtUtc: '2026-11-07T06:42:01Z' },
    ]));
  }

  // Static File Serving
  let filePath = path.join(DIST_DIR, pathname);
  if (!fs.existsSync(filePath) || fs.statSync(filePath).isDirectory()) {
    filePath = path.join(DIST_DIR, 'index.html');
  }

  const ext = path.extname(filePath).toLowerCase();
  const contentType = mimeTypes[ext] || 'application/octet-stream';

  fs.readFile(filePath, (err, content) => {
    if (err) {
      res.writeHead(500);
      res.end('Server Error');
    } else {
      res.writeHead(200, { 'Content-Type': contentType });
      res.end(content);
    }
  });
});

server.listen(PORT, () => {
  console.log(`Showcase server running on http://localhost:${PORT}`);
});

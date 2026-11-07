// k6 scenario: many customers try to hold the SAME seat at the same moment. Exactly one may win.
//
// STATUS: WRITTEN, NEVER RUN (see live-latency.k6.js for the caveats). Run only against a dedicated test environment.
//   k6 run -e BASE_URL=http://localhost:5080 -e EVENT_ID=<guid> -e SEAT_ID=<guid> -e VUS=50 tests/load/hold-contention.k6.js
//
// The invariant under test is "no oversell": the database decides, so across all VUs there must be exactly one 201 and every
// other attempt a 409 (Reservation.SeatsUnavailable). Anything else (a second 201, a 5xx) is a defect to investigate.

import http from 'k6/http';
import { check, fail } from 'k6';
import { Counter } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://localhost:5080';
const EVENT_ID = __ENV.EVENT_ID || fail('EVENT_ID is required');
const SEAT_ID = __ENV.SEAT_ID || fail('SEAT_ID is required');
const VUS = Number(__ENV.VUS || 50);

const won = new Counter('holds_won');
const lost = new Counter('holds_refused_unavailable');
const unexpected = new Counter('holds_unexpected');

export const options = {
  scenarios: { race: { executor: 'per-vu-iterations', vus: VUS, iterations: 1, maxDuration: '2m' } },
  thresholds: {
    holds_won: ['count==1'],
    holds_unexpected: ['count==0'],
  },
};

export default function () {
  const csrf0 = http.get(`${BASE}/api/identity/session`).json('csrfToken');
  const registered = http.post(
    `${BASE}/api/identity/register`,
    JSON.stringify({ email: `race-${__VU}-${Date.now()}@example.com`, password: 'correct-horse-battery-1', displayName: `Race ${__VU}` }),
    { headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': csrf0 } },
  );
  check(registered, { 'registered': (r) => r.status === 201 }) || fail(`register failed: ${registered.status}`);
  const csrf = registered.json('csrfToken');

  // All VUs register first, then race: k6 offers no barrier, so the spread of registration times limits how simultaneous this is.
  const res = http.post(
    `${BASE}/api/reservations`,
    JSON.stringify({ eventId: EVENT_ID, seatIds: [SEAT_ID] }),
    { headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': csrf, 'Idempotency-Key': `${__VU}-${Date.now()}` } },
  );

  if (res.status === 201) won.add(1);
  else if (res.status === 409 && res.json('title') === 'Reservation.SeatsUnavailable') lost.add(1);
  else unexpected.add(1);
}

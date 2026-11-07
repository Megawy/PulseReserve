// k6 scenario: how long does a committed seat hold take to reach other browsers over the live hub?
//
// STATUS: WRITTEN, NEVER RUN (no k6, no database and no running API were available to the author). Treat every detail - the SignalR
// framing, the CSRF flow, the cookie handling of ws.connect - as unverified until it has been run once against a disposable stack.
//
// Run only against a dedicated test environment (never production, never a shared database):
//   k6 run -e BASE_URL=http://localhost:5080 -e EVENT_ID=<guid> -e SEAT_IDS=<guid,guid,...> -e VUS=20 tests/load/live-latency.k6.js
//
// Prerequisites (see docs/reports/realtime-load-test-plan.md):
//  * an event that is ON SALE with at least VUS seats, created beforehand (the script does not create catalog data);
//  * Identity:RateLimit:PermitsPerMinute raised for the run (each VU registers a user from the same address);
//  * Live:ConnectsPerMinute raised for the run (each VU opens one connection from the same address).
//
// Each VU: registers a customer, opens a hub connection (WebSocket, SignalR JSON protocol), subscribes to the event, holds ITS OWN seat
// over HTTP, and measures the time from the hold response to the `availability` message that shows that seat as Held. Every VU also
// receives the other VUs' seats, so the fan-out is VUS x VUS messages (before coalescing).

import http from 'k6/http';
import ws from 'k6/ws';
import { check, fail } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://localhost:5080';
const EVENT_ID = __ENV.EVENT_ID || fail('EVENT_ID is required');
const SEATS = (__ENV.SEAT_IDS || fail('SEAT_IDS is required')).split(',');
const VUS = Number(__ENV.VUS || 20);
const RS = String.fromCharCode(0x1e); // SignalR record separator

export const options = {
  scenarios: { live: { executor: 'per-vu-iterations', vus: VUS, iterations: 1, maxDuration: '3m' } },
  thresholds: {
    // Deliberately no latency threshold: the owner has not set a target, and inventing one would be a made-up requirement.
    live_hold_to_message_ms: ['p(95)>=0'],
    live_missed_messages: ['count==0'],
  },
};

const holdToMessage = new Trend('live_hold_to_message_ms', true);
const received = new Counter('live_messages_received');
const missed = new Counter('live_missed_messages');

function register() {
  const session = http.get(`${BASE}/api/identity/session`);
  let csrf = session.json('csrfToken');
  const email = `load-${__VU}-${Date.now()}@example.com`;
  const res = http.post(
    `${BASE}/api/identity/register`,
    JSON.stringify({ email, password: 'correct-horse-battery-1', displayName: `Load ${__VU}` }),
    { headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': csrf } },
  );
  check(res, { 'registered': (r) => r.status === 201 }) || fail(`register failed: ${res.status}`);
  return res.json('csrfToken');
}

export default function () {
  const csrf = register();
  const mySeat = SEATS[(__VU - 1) % SEATS.length];
  const url = `${BASE.replace(/^http/, 'ws')}/hubs/live`; // WebSockets only: no negotiate call needed
  let heldAt = null;
  let sawMine = false;

  const response = ws.connect(url, { jar: http.cookieJar() }, (socket) => {
    socket.on('open', () => {
      socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
    });

    socket.on('message', (data) => {
      for (const frame of String(data).split(RS).filter((f) => f.length > 0)) {
        const message = JSON.parse(frame);
        if (message.type === undefined) {
          // Handshake accepted: identify, subscribe, then hold.
          socket.send(JSON.stringify({ type: 1, invocationId: '1', target: 'Hello', arguments: [] }) + RS);
          socket.send(JSON.stringify({ type: 1, invocationId: '2', target: 'SubscribeEvent', arguments: [EVENT_ID] }) + RS);
        } else if (message.type === 3 && message.invocationId === '2') {
          const hold = http.post(
            `${BASE}/api/reservations`,
            JSON.stringify({ eventId: EVENT_ID, seatIds: [mySeat] }),
            {
              headers: {
                'Content-Type': 'application/json',
                'X-XSRF-TOKEN': csrf,
                'Idempotency-Key': `${__VU}-${Date.now()}`,
              },
            },
          );
          check(hold, { 'held': (r) => r.status === 201 });
          heldAt = Date.now();
        } else if (message.type === 1) {
          received.add(1, { target: message.target });
          if (message.target === 'availability' && heldAt !== null && !sawMine) {
            const seats = message.arguments[0].seats || [];
            if (seats.some((s) => s.seatId === mySeat && s.status === 'Held')) {
              sawMine = true;
              holdToMessage.add(Date.now() - heldAt);
              socket.close();
            }
          }
        } else if (message.type === 6) {
          socket.send(JSON.stringify({ type: 6 }) + RS); // keep-alive ping
        }
      }
    });

    socket.setTimeout(() => socket.close(), 30000);
  });

  check(response, { 'upgraded': (r) => r && r.status === 101 });
  if (heldAt !== null && !sawMine) {
    missed.add(1); // the announcement of this VU's own hold never arrived within 30 s
  }
}

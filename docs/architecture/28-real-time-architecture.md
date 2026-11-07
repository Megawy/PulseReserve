# 28 · Real-time architecture (live seat availability, reservations, orders, operations)

Status: implemented on branch `upgrade/dotnet-10`; **not committed**. Verification status is stated per capability in section 12 and
in `docs/reports/realtime-functionality-matrix.md`. The SQL, triggers and listener have been exercised against PostgreSQL (2026-10-23);
it was also exercised in a real browser on the dev stack (2026-10-23, `docs/reports/browser-verification-report.md`); nothing has been exercised against a provider sandbox (see section 12).

## 1. What it is, and what it is not

A single SignalR hub, `/hubs/live`, pushes **hints** to browsers that something they are looking at has changed:

| Audience | What changed | Message |
|---|---|---|
| Anyone watching an event page (signed in or not) | The status of seats (Available / Held / Sold) | `availability` |
| The owner of a reservation | Their reservation (Held, Checkout, Confirmed, Released, Expired) | `reservation` |
| The owner of an order | Their order (status, payment attempt status, number of tickets) | `order` |
| Administrators | An operations area changed (overview, orders, refunds, exceptions, anomalies, tasks, outbox) | `admin` |
| Everyone | Notifications may have been missed: read again | `resync` |

It is **not**:

* a source of truth. Every message tells a page *what to re-read*; the page re-reads the authenticated REST API and shows only what
  that answers. No message is ever rendered as "payment succeeded", "refund done" or "tickets issued" (invariants 1, 2, 12 and 15 of
  the financial rules).
* a durable queue. Delivery is best effort and in-process. There is no replay; a client that missed something re-reads (section 6).
* a reason to trust a client. The hub accepts no data that changes state, and a subscription grants nothing that is not already public.

## 2. Pipeline

```
 write transaction (reservation / payments schema)
        │  AFTER INSERT/UPDATE triggers call pg_notify('pulsereserve_live', {"k":..,"id":..})   ← delivered only if the transaction COMMITS
        ▼
 PostgresChangeListener  (one dedicated, unpooled LISTEN connection, keep-alive 30 s)
        │  LiveSignalParser: payload → signals (ids and coarse kinds only; junk is ignored)
        ▼
 LiveDispatcher  (bounded queue, 150 ms coalescing, de-duplication per entity)
        │  re-reads the CURRENT state from the database (database clock) through the modules' own readers
        ▼
 IHubContext<LiveHub>.Clients.Group(...)   event:{id} | user:{ownerId} | admins     (resync: all)
        ▼
 browser: LiveService → notices → pages re-read the REST API
```

Design decisions and their reasons:

* **Triggers + `NOTIFY`, not application code after `SaveChanges`.** PostgreSQL delivers a notification only when the sending
  transaction commits, so an announcement can never describe a change that was rolled back, and it covers every write path (HTTP
  handlers, the payment workflow worker, webhooks, the hold-expiry worker, operators' SQL). Application-level "publish after save"
  would miss the paths that do not go through the same code and cannot be tied to the commit.
* **Triggers can never fail a write.** Each notification function wraps `pg_notify` in a nested `EXCEPTION WHEN OTHERS THEN NULL` block.
  The migrations are `Reservation/.../20261022190301_LiveChangeNotifications` and `Payments/.../20261022190304_LiveChangeNotifications`
  and only add functions and triggers (no data is touched; `Down` drops them).
* **Signals are invalidations.** A signal carries ids and a coarse kind. Content *and routing* (who is the owner, what is the status)
  come from a fresh database read at dispatch time, so a late, duplicated or reordered signal can only cause a harmless extra read.
  The owner is never taken from the signal.
* **Multi-instance capable without a backplane.** Every instance LISTENs and serves its own connections. A client always talks to one
  instance, so no cross-instance fan-out is needed. Redis, Kafka or a SignalR backplane are *not* introduced (no demonstrated need).

### Signal mapping

| Source table / row | Signal | Delivered to |
|---|---|---|
| `reservation.reservations` insert, or `status` change | reservation | availability → `event:{id}`; reservation → owner |
| `payments.financial_transitions` (`kind = 'Transition'`) `Order` / `PaymentAttempt` | order | owner; admin `orders` + `overview` |
| … `PaymentException` | order | owner; admin `exceptions` + `overview` |
| … `Refund` | – | admin `refunds` + `overview` (customers see no refund record) |
| … `PaymentAnomaly` | – | admin `anomalies` + `overview` |
| `payments.tickets` insert | order | owner (ticket count) |
| `payments.reconciliation_tasks` / `outbox_events` (every insert and every state change; the overview shows a count per state) | – | admin `tasks` / `outbox` + `overview` |

## 3. Wire contract (JSON, camelCase)

Every server message carries `epoch` (a GUID identifying the server process) and `seq` (increasing within that process).

```
availability  { eventId, seats: [{ seatId, status }], epoch, seq }
reservation   { reservationId, eventId, status, expiresAtUtc, epoch, seq }
order         { orderId, reservationId, status, paymentStatus|null, ticketCount, epoch, seq }
admin         { area, epoch, seq }
resync        { reason, epoch, seq }
```

Client → server (the only methods): `Hello()` → `{ epoch, serverTimeUtc, authenticated, admin, maxConnectionSeconds }`,
`SubscribeEvent(eventId)`, `UnsubscribeEvent(eventId)`. Errors are `HubException` messages `Live.InvalidEvent` and
`Live.TooManySubscriptions`; detailed errors are off.

What is deliberately **absent**: customer ids in availability, amounts, currencies, providers, references, refund records, evidence,
tickets' content. The order message carries exactly what the owner already sees in `GET /api/orders/{id}`.

## 4. Authorization and abuse limits

| Control | Rule | Where |
|---|---|---|
| Identity | The session cookie authenticates the connection request; the principal is rebuilt from the database, so roles are current when the connection opens. Anonymous connections are allowed (public availability). | `LiveHub.OnConnectedAsync` |
| Groups | `user:{id}` and `admins` are joined from the authenticated principal only. **The hub has no method that takes a user id or a group name.** `event:{id}` is the only group a client chooses, and it carries public data. | `LiveHub`, pinned by a reflection test |
| Admin | Membership of `admins` needs the Admin role claim at connect time. An administrator revoked later keeps the connection only until it is closed by the maximum age. | `Live:MaxConnectionMinutes` |
| Max connection age | Default 10 min; the client reconnects (and is authenticated again). Bounds how long a revoked admin or a signed-out user keeps listening. A tab that stays open in another window after the user signed out elsewhere keeps its old identity until its next rotation (the cookie is shared, the connection is not told). | `LiveConnectionReaper` |
| Origin | A browser whose `Origin` differs from the request host (and is not in `Live:AllowedOrigins`) is refused with 403 `Live.OriginNotAllowed`, before the hub. WebSockets are not covered by CORS. The session cookie is also `SameSite=Strict`. A request without `Origin` (non-browser) is allowed: it cannot borrow a cookie. | `LiveOrigin` |
| Connect rate | `live-connect` policy, default 60 / minute / client address (the real address behind a trusted proxy, see doc 23). | rate limiter |
| Subscriptions | At most 5 events per connection (configurable 1–20); re-subscribing is idempotent. | `LiveConnectionRegistry` |
| Message size | Hub receive limit 4 KiB (clients only ever send subscription requests). | SignalR options |
| Stateful reconnect | **Never offered.** CVE-2026-70159 / GHSA-334f-vvp4-7g6p: the stateful-reconnect buffer can be driven to unbounded memory; fixed in ASP.NET Core 10.0.13. The hub does not use the feature, and a unit test pins that a client asking for it is not given it. Run ≥ 10.0.13 anyway. | `StatefulReconnect_StaysDisabled…` |
| Status | `GET /api/admin/live/status` (administrators only): counters and timestamps, no secrets. | `LiveRegistration` |
| Database role | The live layer needs **no extra privilege** (doc 24): `LISTEN` and `pg_notify` need none, the snapshot reads select from tables the application already reads, and the hold-expiry statement updates the same two reservation tables the booking path updates. The trigger functions are created by the migration (owner) role. | migrations `…LiveChangeNotifications` |

## 5. Hold expiry (`HoldExpiryWorker`)

Expiry stays **lazy** for correctness (doc 13): a hold past its expiry is already reported as expired and its seats are reclaimable
without any worker. The worker only makes the end of a hold a *committed fact* so it can be announced: every
`Reservation:HoldExpiry:IntervalSeconds` (5) it closes up to `BatchSize` (100) reservations that are `Held` and past `expires_at_utc`
(database clock) and frees their seats, in one statement under `FOR UPDATE SKIP LOCKED` (several instances may run it).
A reservation in `Checkout` is **never** closed by it: its order belongs to the payment workflow. A failing pass is logged by exception
type only and the loop continues. It starts after a random 200–2000 ms offset (also a test-safety property, pinned by a unit test).

## 6. Guarantees, non-guarantees and failure behaviour

| Situation | What happens | What the user sees |
|---|---|---|
| Normal change | commit → NOTIFY → dispatcher → message within about the coalescing window (150 ms) plus the read | seat map / order page updates |
| Rolled-back change | nothing is announced | nothing |
| Burst on one entity | de-duplicated; one read and one message per entity per window | one update |
| Duplicate or out-of-order message | the client drops any message whose `(epoch, seq)` is not newer | nothing |
| Listener connection lost | reconnect with exponential backoff (1 s → 30 s, capped); on reconnect **every client is told to resync** | brief "Reconnecting" only if the gap lasts > 2 s; pages re-read |
| Dispatch queue overflow (10 000 signals) | oldest signals dropped, counted, **every client told to resync** | pages re-read |
| Database read fails for one entity | that entity's message is skipped and logged by type; the batch continues | page re-reads on its next refresh or hint |
| Server restart / another instance | new `epoch` → client treats its baseline as gone and re-reads | pages re-read |
| Client offline / tab asleep | no replay | on reconnect `connected` resync → pages re-read |
| Live disabled or refused (`Live:Enabled=false`, origin not allowed) | hub not mapped / 403–404 | "Live updates off"; pages use their normal refresh; slow fallback poll of the seat map |
| Listener disabled (`Live:ChangeFeed:Enabled=false`) | hub works, nothing is pushed | as above |

Ordering: within one connection and one `epoch`, messages are delivered in `seq` order (one sequential dispatcher). Nothing is
promised across epochs. Delivery is **at most once** per connection; correctness never depends on receiving a message, only on the
re-read that follows a gap, which is announced.

Limits that are real: a push reaches a client only if its connection is open at that moment; with 10 minutes maximum age the
connection is replaced regularly (reconnect within about 300 ms plus the handshake) and the replacement is followed by a `resync`.

## 7. Frontend behaviour

`core/live/` (Angular, zoneless, signals):

* `LiveService` — one connection per tab. Opened after the session is known (`provideAppInitializer`), not awaited by the first
  route. Reopened when the signed-in user changes (identity is the cookie at connect time). Automatic reconnect of the library is **off**:
  the service owns it (backoff 1 s doubling to 30 s with 25 % jitter; a connection that lived ≥ 30 s is a *rotation* and is replaced
  after 300 ms; 401/403/404 mean "refused" and are retried every 5 minutes; the browser's `online` event retries at once).
  The SignalR library is a lazy chunk (12.7 kB transferred) and logging is off.
* `notices` — typed, validated messages. Malformed or stale messages are counted (`ignored`) and dropped.
* **Freshness rule.** Every (re)connect, every subscription that becomes active, every server `resync` and every epoch change is
  announced as a `resync` notice carrying a monotonic time `at`. A page re-reads when its data was **requested no later than** `at`
  (`createLiveRefresh`). A page that loads after the connection is live does not read twice; a page that loaded a moment before the
  connection existed does read once more. This closes the gap between "loaded" and "subscribed".
* `createLiveRefresh` debounces (300 ms) matching notices into one read; reads are *quiet* (the screen keeps the last server answer
  until the new one arrives; a failed quiet read changes nothing on screen).
* **Cost to know about:** a hard page load (or a reconnect) reads once more than a polling page would, because the connection is confirmed after the page has already started its first read; the announced "connected" gap then triggers one re-read (about 350 ms later in the dev stack). That is the price of leaving no window between "loaded" and "subscribed".
* Pages: **event page** applies seat statuses as they arrive and confirms with one extra read if a change landed while a read was in
  flight, re-reads *my* reservation on a notice, and polls availability every 30 s *only while live updates are not running*;
  **checkout panel** re-reads the order (no busy state, queued while its own request runs) and loads tickets when a paid order has none;
  **order page / order list** re-read quietly (the bounded post-redirect polling stays as the fallback); **admin overview and lists**
  re-read the matching area quietly.
* `LiveIndicator` says "Live" only after the server identified the connection, "Reconnecting" after > 2 s down, "Live updates off"
  when refused; announces to assistive technology only when updates stop. Static (no animation), uses badge tokens (light/dark).

## 8. Configuration

| Setting | Default | Meaning |
|---|---|---|
| `Live:Enabled` | `true` | map the hub; `false` → pages work with refresh/polling only |
| `Live:ChangeFeed:Enabled` | `true` | run the LISTEN connection; off for hosts that must not connect at startup (integration tests) |
| `Reservation:HoldExpiry:Enabled` / `:IntervalSeconds` / `:BatchSize` | `true` / 5 / 100 | proactive closing of expired `Held` holds (1–300 s, 1–1000) |
| `Live:MaxConnectionMinutes` | 10 | connection age limit (1–60) |
| `Live:MaxEventSubscriptionsPerConnection` | 5 | (1–20) |
| `Live:CoalesceMilliseconds` | 150 | (10–5000) |
| `Live:ChannelCapacity` | 10 000 | dispatch queue (100–1 000 000) |
| `Live:ConnectsPerMinute` | 60 | per client address (1–10 000) |
| `Live:AllowedOrigins` | empty | comma/semicolon separated absolute origins (`https://app.example.com`) for cross-origin SPAs |

A flag or number that cannot be understood **fails startup** (it is never read as on/off). Reverse proxies must pass WebSocket upgrades on
`/hubs/` (`Upgrade`/`Connection` headers) and allow idle reads of at least 60 s (server keep-alive 15 s, client timeout 45 s); sticky
sessions are not required. The development proxy (`proxy.conf.mjs`) forwards `/hubs` with `ws: true` and `changeOrigin: false` (the Origin
check compares with the Host the browser used).

## 9. Observability

* .NET meter `PulseReserve.Live` (no package, no exporter): `pulsereserve.live.connections` (gauge), `.signals{kind}`,
  `.signals.dropped`, `.messages{target}`, `.resyncs{reason}`, `.listener.reconnects`.
* `GET /api/admin/live/status`: enabled flags, `epoch`, `connections`, `listenerConnected`, `listenerReconnects`,
  `lastNotificationUtc`, counters, `maxConnectionSeconds`.
* Logs (by exception type only, never connection strings, payloads or user data): listener lost/connected, batch failure, entity
  skipped, hold-expiry pass failed / closed N holds.
* Suggested alerts (extends doc 26; thresholds are for the operator to choose): listener disconnected for > 1 min; `signals.dropped`
  increasing; `listener.reconnects` rising quickly; `connections` falling to 0 while traffic exists.

## 10. Runbook

| Symptom | Check | Action |
|---|---|---|
| Pages show "Live updates off" | `status.enabled`; browser 403 → `Live:AllowedOrigins`/proxy Host; 404 → `Live:Enabled=false` | fix configuration; pages keep working meanwhile |
| "Reconnecting" for long | proxy upgrade/timeouts; `status.listenerConnected` | restore network/proxy; the client retries by itself |
| Updates stop but hub connected | `listenerConnected=false` or `lastNotificationUtc` stale | check database connectivity and the `pulsereserve-live` session in `pg_stat_activity`; the listener reconnects by itself and announces a resync |
| Many resyncs | `resyncs{reason}`: `queue-overflow` → capacity/burst; `change-feed-connected` → flapping database link | investigate the cause; correctness is unaffected |
| Holds never expire visibly | `Reservation:HoldExpiry:Enabled`, worker logs | correctness does not depend on it (lazy expiry) |
| After deploy, every client re-reads once | expected: new `epoch` | none |

## 11. Not done, deliberately

* No event replay / durable log, no read-your-writes token, no per-user message history. A durable log would need an owner decision on
  retention and is not needed while every message is a hint.
* No backplane; if a deployment ever needs more than PostgreSQL's LISTEN fan-out per instance, measure first.
* Capacity numbers (connections per instance, signals per second) are **not measured**. A load-test harness is described in
  `docs/reports/realtime-load-test-plan.md` and was not run.
* The payment-history search page (`/admin/payments/history`) is a lookup by order id and is not live.

## 12. Verification status

| Layer | Evidence | Status |
|---|---|---|
| Signal parsing, options, origin rules | unit tests (`LiveBasicsTests`) | executed, passing |
| Hub identity, groups, routing, privacy of payloads, ordering, dedupe, overflow, DB-failure tolerance, subscription cap, max-age abort, origin 403, connect rate limit 429, stateful reconnect off, admin status endpoint, disabled mode | unit tests over real SignalR clients against the real hub with a fake database reader (`LiveHubTests`) | executed, passing |
| Listener reconnect/backoff/junk handling, hold-expiry batching and failure handling | unit tests with fakes (`ChangeFeedAndHoldExpiryTests`) | executed, passing |
| Trigger SQL, NOTIFY-after-commit, rollback silence, snapshot SQL, hold-expiry SQL, listener against a real server, end-to-end through the real pipeline | `LivePostgreSqlTests` (7 tests) | **executed 2026-10-23, passing** (repeated runs stable): NOTIFY only after commit, silence on rollback, hold-expiry closes an expired hold and announces it (and leaves `Checkout` alone), listener outage and recovery, end to end through the real hub and cookie sign-in |
| Angular service, refresh rule, indicator, pages, active-hold recovery | 83 new specs with a fake hub connection | executed, passing |
| Real browser through the dev proxy and real cookies: seats, my reservation, my order, tickets, hold expiry, outage and recovery, admin overview, light/dark, 375 px | manual run on the dev stack, simulator payments (`docs/reports/browser-verification-report.md`) | **verified 2026-10-23**; found and fixed a duplicate connection per signed-in load, stale admin queue counts (fix covered by a PostgreSQL test, not yet re-checked in the browser) and a 404 console error |
| WebSocket frames themselves, production nginx image, other browsers, assistive technology, reduced motion | – | **not verified** |
| Provider sandboxes | – | not contacted; the live layer is provider-agnostic |

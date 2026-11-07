# Real-time functionality matrix

Companion to `docs/architecture/28-real-time-architecture.md`. Status vocabulary as in `feature-completion-matrix.md`.
"U" = executed unit test (real hub/dispatcher, fake database reader, real SignalR client); "PG" = test written against real
PostgreSQL, **executed on 2026-10-23 and passing** (7 tests; repeated runs stable); "NG" = Angular spec (executed); "B" = real browser on the dev stack, **verified 2026-10-23** where a row says "B verified" (see `browser-verification-report.md`), otherwise not verified.

| Capability | Mechanism | Evidence | Status |
|---|---|---|---|
| Announce only committed changes | Triggers + `pg_notify` (delivered on commit) | PG `AChangeThatIsRolledBack_IsNeverAnnounced`; SQL syntax-parsed | PostgreSQL integration-tested |
| Triggers cannot fail a write | nested `EXCEPTION WHEN OTHERS` in each function | SQL review; every write in the 266-test PostgreSQL suite ran with the triggers installed and none failed | Implemented; no test forces a `pg_notify` failure |
| Change feed survives loss | reconnect + backoff (1→30 s), resync on every connect | U `ChangeFeedListenerTests` (backoff sequence, reset, junk, shutdown); PG `ALostListenerConnection_IsReestablished…` | PostgreSQL integration-tested |
| Malformed notification payloads | parser ignores, never throws | U `LiveSignalParserTests` (14 shapes + oversize) | Unit-tested |
| Authoritative content | dispatcher re-reads current state (DB clock) | U `TheContentIsWhatTheDatabaseSaysNow…`; snapshot SQL | Unit-tested; snapshot SQL exercised by the PostgreSQL live tests (reservation and order messages arrived with the database's values) |
| Public seat availability | group `event:{id}`, no holder identity | U `AnAnonymousVisitor_HearsSeatAvailabilityOnly…` (payload property list, no ids) | Unit-tested |
| Reservation/order only to the owner | group `user:{id}` from the authenticated principal | U `AReservationAndAnOrder_GoOnlyToTheirOwner…`, `AnAdministrator_ReceivesTheirOwnPrivate…` | Unit-tested |
| Administrator areas only to admins | group `admins` (Admin role at connect) | U `AdministratorAreaMessages_GoToAdministratorsOnly…` (incl. a forged admin header without a user) | Unit-tested |
| No way to join another group | hub exposes only `Hello`/`SubscribeEvent(eventId)`/`UnsubscribeEvent(eventId)` | U reflection pin + calls to non-existent methods fail | Unit-tested |
| Messages carry no money data | order message = status, payment status, ticket count | U `AnOrderMessage_IsAHintThatCarriesNoAmounts…` | Unit-tested |
| Duplicate / out-of-order safety | `(epoch, seq)` on every message; client drops older | U (server: unique increasing seq under concurrency); NG `drops a duplicate or out-of-order message` | Unit-tested |
| New server process | epoch change ⇒ client resync | NG `treats a message from another server process…`, `announces a new server process when Hello…` | Unit-tested |
| Coalescing and dedupe | 150 ms window, one read/message per entity | U `ABurstOfDuplicateSignals_CostsOneRead…`, background-loop test | Unit-tested |
| Overload is visible | bounded queue, overflow ⇒ resync to everyone | U `WhenTheQueueOverflows_EveryClientIsToldToReRead…` | Unit-tested |
| One failing read does not stop a batch | per-entity guard | U `AnUnreachableDatabase_NeverStopsTheBatch…` | Unit-tested |
| Subscription cap, idempotence, release | 5 per connection | U `ASubscription_IsCapped…`, `…FollowsTheConfiguration`, `AfterUnsubscribing…` | Unit-tested |
| Identity re-checked regularly | max connection age 10 min, abort + reconnect | U `AConnectionOlderThanTheMaximumAge_IsClosed…` (registry), NG rotation reconnect | Unit-tested (the reaper's timer loop itself is not tested) |
| Cross-site WebSocket refused | Origin check before the hub, allow-list | U `ABrowserPageOfAnotherSite_IsRefused…`, `LiveOriginTests` (13 cases) | Unit-tested; B not verified (a cross-site page was not tried) |
| Connect flood | `live-connect` rate limit per address | U `OpeningConnectionsIsRateLimited…` (429) | Unit-tested |
| Stateful reconnect never offered | not enabled; negotiate ignores the request | U `StatefulReconnect_StaysDisabled…` | Unit-tested |
| Operator visibility | meter + admin-only status endpoint | U `TheStatusEndpoint_IsForAdministratorsOnly…` (401/403/200, no secrets) | Unit-tested |
| Proactive hold expiry | worker, batches, SKIP LOCKED, not `Checkout` | U batching/failure/log tests; PG `TheWorker_ClosesAnExpiredHold…`, `…LeavesAReservationInCheckout…` | PostgreSQL integration-tested |
| Configuration is strict | invalid values fail startup | U `LiveOptionsTests` | Unit-tested |
| Disabled mode | `Live:Enabled=false` ⇒ no hub, status still answers | U `WhenDisabled_NoHubIsMapped…` | Unit-tested |
| Test hosts cannot touch a database at startup | factory turns workers off; start delay pinned | unit pin `TheIntegrationHostKeepsTheLiveWorkersOff…` | Unit-tested |
| Browser connection lifecycle | LiveService: connect after session, identity-bound, backoff+jitter, rotation, refused ⇒ slow retry, `online` retry | NG `live.service.spec` (28) | Unit-tested; B verified (connect, identity change, rotation, outage, recovery) |
| No gap between "loaded" and "subscribed" | freshness rule (`at` vs request time) | NG `live-refresh.spec` (7), booking/checkout/admin specs | Unit-tested |
| Event page | deltas applied, confirm-read if a change landed mid-read, my reservation re-read, slow poll only when not live | NG `booking-flow-live.spec` (14) | Unit-tested; B verified |
| Checkout / order / orders list | quiet re-read on hint; queued while busy; tickets on hint | NG `checkout-flow-live.spec` (9) | Unit-tested; B verified (payment in progress, paid, tickets appeared by themselves) |
| Admin overview and lists | quiet re-read per area; rows kept on failure | NG `admin-live.spec` (8) | Unit-tested; B verified (event page, order page, admin console) |
| Truthful indicator | "Live" only after Hello; "Reconnecting" after 2 s; "Live updates off" when refused | NG `live-indicator.spec` (6) | Unit-tested; B verified ("Live", "Reconnecting") |
| WebSocket through the dev proxy / nginx | `ws: true`; upgrade headers in `docker/nginx.conf` | none | **Blocked** (browser, Docker) |
| Real-time under load | – | none | **Not measured** — plan only |
| Multi-instance behaviour | each instance LISTENs; epoch per instance | design only | **Not tested** |

## Guarantees in one paragraph

A committed change is announced at most once to each open connection that is allowed to hear it, in order within one server process,
typically within a few hundred milliseconds. If anything may have been missed — the listener reconnected, the queue overflowed, the
server restarted, the browser was offline — every affected page is told so and re-reads from the REST API before it relies on its
data. Nothing the client receives is treated as a fact about money or inventory; the authenticated API and the database decide.
There is no replay and no durability, by design.

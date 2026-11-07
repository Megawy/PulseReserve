# PulseReserve — real-time engineering, end-to-end integration and release-completion report

Date: 2026-10-23 (updated after the PostgreSQL run and the browser run) · Branch: `upgrade/dotnet-10` · **Nothing is staged, committed or pushed.** All work is in the working tree.

Read this first: the product now has a real-time layer, a "find my hold" capability, a completed set of live-updating customer and
administrator screens, production image templates and a full documentation set. After the owner authorized Docker/Testcontainers on
2026-10-23, **all 266 PostgreSQL integration tests were executed and pass** (four consecutive full runs after fixes). The first run failed 17
tests and exposed **one real product defect** (provider HTTP timeouts mishandled, section 10) — fixed. **Nothing has been run against a
payment-provider sandbox.** The owner then authorized building the production images and a browser pass on the dev stack (section 11): both production images build, and the customer and administrator journeys, live updates, outage and recovery were exercised in a real browser (desktop and 375 px, light and dark) — which found and fixed three more issues. What was proven is stated precisely in section 3; what was not is in sections 4–6.

## 1. What was implemented in this phase

### Real-time (first-class feature, doc 28)

* **Database signals, commit-accurate.** Two migrations add notification functions and triggers (`reservation` and `payments` schemas)
  that call `pg_notify` inside a nested exception block (a notification can never fail a write; a rolled-back change is never
  announced). Down migrations drop only functions and triggers.
* **Backend layer** (`src/PulseReserve.AppHost/Live/`, nine files): strict options; signal parser; one SignalR hub `/hubs/live` that only
  pushes and accepts three calls (`Hello`, `SubscribeEvent`, `UnsubscribeEvent`); groups derived from the authenticated principal
  (`user:{id}`, `admins`) plus public `event:{id}`; a dispatcher that re-reads authoritative state (database clock) from the Reservation and
  Payments modules' own live readers, coalesces, de-duplicates, bounds its queue and announces overflow as a `resync`; a dedicated
  `LISTEN` connection with reconnect/backoff and resync on every connect; per-connection subscription cap; maximum connection age;
  Origin check; connect rate limit; admin-only status endpoint; a .NET `Meter`. Stateful reconnect is never offered (GHSA-334f-vvp4-7g6p).
* **Proactive hold expiry** (`HoldExpiryWorker`): closes expired `Held` reservations in batches under `FOR UPDATE SKIP LOCKED`; never
  touches `Checkout`; lazy expiry remains the correctness mechanism.
* **Angular** (`core/live/`): `LiveService` (identity-bound connection, backoff with jitter, rotation handling, refused ⇒ slow retry,
  epoch/sequence rules, ref-counted event subscriptions), `createLiveRefresh` (debounced quiet re-reads and the "re-read if your data is
  older than the announced gap" rule), `LiveIndicator`. Wired into the event page (seat deltas, my reservation, slow fallback poll only
  when not live), checkout panel, order page, orders list, administrator overview and every administrator list. The library is a lazy chunk.
* **Dev proxy** forwards `/hubs` with WebSocket upgrade; **production nginx template** does too.

### Reservation lifecycle completion

* **`GET /api/reservations/active?eventId=`** — the signed-in customer's own live hold (Held/Checkout, not past expiry on the database
  clock). Closes a real gap: a hold made in another browser or after clearing storage was invisible and blocked any new hold with
  `Reservation.ActiveHoldExists`. The event page now restores it on load, after that conflict, and when a live notice about the event
  arrives while it shows none.

### Deployment and documentation

* `docker/api.Dockerfile`, `docker/web.Dockerfile`, `docker/nginx.conf`, `.dockerignore` (written, **not built**); `compose.dev.yaml`
  and `.env.example` pass the two live switches.
* Docs: 28 (real-time architecture, runbook), 29 (deployment and release readiness), `docs/reports/` (feature-completion matrix,
  real-time functionality matrix, load-test plan, this report), corrected SignalR statements in `README.md`/`PROJECT.md`, a SignalR
  section in `docs/DEVELOPMENT.md`.
* `tests/load/` — two k6 scripts and a README (**never run**).
* Safety nets: the integration factory keeps the listener and the hold-expiry worker off by default, and a unit test pins that and
  their start delays, so a test host cannot touch a database before the target-database guard.

## 2. Phase report (per the brief)

| Phase | Features completed | Main files | Tests executed | Blockers | Next |
|---|---|---|---|---|---|
| A. Backend signals and live layer | triggers, hub, dispatcher, listener, hold expiry, options, status | `AppHost/Live/*`, two `…LiveChangeNotifications` migrations, module live readers | 104 live unit tests + 1 pin | none | done: 7 PostgreSQL live tests pass |
| B. Test infrastructure | in-memory real hub with real SignalR clients | `tests/PulseReserve.UnitTests/Live/*` | included above | – | – |
| C. Angular live client and wiring | service, refresh rule, indicator, 8 pages | `core/live/*`, page files | 85 new specs (497 total) | none | done: browser pass on the dev stack (section 11) |
| D. Reservation recovery | active-hold lookup end to end | `ReservationEndpoints`, store, handler, client, `BookingFlow` | 4 + 11 specs | none | done: PostgreSQL test passes |
| E. Deployment and docs | image templates, docs, matrices, load scripts | `docker/*`, `docs/*`, `tests/load/*` | none possible | Docker | build images in CI |

## 3. Tests actually executed (this machine, 2026-10-22/23)

| Suite | Command | Result |
|---|---|---|
| .NET build, Debug and Release, whole solution | `dotnet build PulseReserve.sln [-c Release]` | 0 errors, 0 warnings (warnings are errors) |
| .NET unit tests | `dotnet test tests/PulseReserve.UnitTests` | **770 passed**, 0 failed (765 + 5 timeout regression tests) |
| Architecture tests | `dotnet test tests/PulseReserve.ArchitectureTests` | **72 passed** |
| Integration tests, PostgreSQL via Testcontainers (one throw-away `postgres:17.11-alpine` per test class) | `env -u PULSERESERVE_TEST_DATABASE dotnet test tests/PulseReserve.IntegrationTests --no-build` | first full run **249 passed / 17 failed**; after the fixes in section 10 and the browser findings in section 11: **267 / 267** (266 + the queue-announcement test), 4 consecutive full runs of 266 earlier and a final full run of 267 (one earlier run had one flaky *test* — a race in my own new test — fixed and then stable); the 7 live tests also 5× in isolation |
| EF model drift | `dotnet ef migrations has-pending-model-changes` for Catalog, Identity, Reservation, Payments | no pending changes in any |
| Migration SQL syntax (the two new migrations, earlier in this phase) | offline script + libpg_query parse | parsed (32 statements, 2 PL/pgSQL bodies) — syntax only |
| Angular unit tests | `npx ng test --watch=false` | **497 passed** (412 before this phase) |
| Angular lint / format / production build | `ng lint`, `prettier --check .`, `ng build` | clean; initial bundle 309.79 kB (83.87 kB transferred); SignalR is a lazy 12.7 kB chunk |
| Dependency audit | `npm audit --omit=dev` | 0 vulnerabilities (`@microsoft/signalr` 10.0.11, MIT) |
| Secret scan of the repository (excluding build output) | pattern scan for key/token shapes | none found |

What the new .NET live tests prove (details in `realtime-functionality-matrix.md`): over **real SignalR client connections against the real
hub and dispatcher** (database replaced by a fake reader), who hears what — anonymous visitors only public availability of subscribed
events, owners only their own reservation/order messages, administrators only area hints, a forged admin header gets nothing; the hub
cannot be asked to join any other group; payloads carry no money or holder data; ordering/dedupe; overflow ⇒ resync; DB failure does
not stop a batch; subscription cap; max-age abort; cross-origin 403; connect rate limit 429; stateful reconnect not offered; status
endpoint 401/403/200.

## 4. PostgreSQL, provider and browser status

| Area | Status |
|---|---|
| PostgreSQL | **Executed 2026-10-23, 267 / 267 passing.** Includes the new `LivePostgreSqlTests` (7: NOTIFY after commit, rollback silence, listener outage and recovery, hold expiry, end to end through the real hub) and the active-hold test. Caveats: runs as the container's superuser (one dedicated test covers the least-privilege role), providers are faked, race tests show contention not a controlled interleaving, no populated real database, no pooling-under-concurrency check (doc 27 §5). |
| Stripe / Paymob | **Not contacted.** Covered by unit tests with fake HTTP only. |
| Browser | **Verified on the dev stack, 2026-10-23** (section 11, `browser-verification-report.md`): customer and administrator journeys, live updates, outage and recovery, desktop and 375 px, light and dark, simulator payments. **Not verified:** other browsers/devices, assistive technology, reduced motion, the WebSocket frames themselves, the production nginx image, any provider page. |
| End-to-end | None. |

## 5. Files changed

Counts of what this phase touched (the repository is largely untracked, so `git diff` is not a useful measure):

* **Backend source created:** `AppHost/Live/` (9), `Reservation/Features/Live/ReservationLive.cs`, `Reservation/Persistence/IReservationLiveStore.cs`,
  `Reservation/Persistence/Postgres/ReservationStore.Live.cs`, `Payments/Features/Live/OrderLive.cs`, `Payments/Persistence/IPaymentsLiveStore.cs`,
  `Payments/Persistence/Postgres/PaymentsLiveStore.cs`, two migrations with designers.
* **Backend source modified:** `Program.cs`, `ReservationEndpoints.cs`, `ReservationModuleExtensions.cs`, `PaymentsModuleExtensions.cs`,
  `IReservationStore.cs`, `ReservationStore.cs`, `ReservationErrors.cs`, `ReservationQueriesHandler.cs`, both model snapshots,
  `Directory.Packages.props`.
* **Tests created:** `tests/PulseReserve.UnitTests/Live/` (4), `Reservation/ActiveReservationHandlerTests.cs`,
  `IntegrationTests/Api/LivePostgreSqlTests.cs`; **modified:** `HoldSeatsHandlerTests.cs` (fake gained one method),
  `ReservationApiHttpTests.cs` (+1 test), `FinancialDatabaseBoundaryTests.cs` (+1 pin), `CatalogApiFactory.cs`, two test `.csproj` files.
* **Frontend created:** `core/live/` (4 files + 2 specs), `shared/ui/live-indicator/` (component + spec), 5 spec files for the wiring;
  **modified:** `app.config.ts`, `reservation-api.client.ts(+spec)`, `booking-flow.ts`, `checkout-flow.ts`, `order-detail.ts`, `order-list.ts`,
  `event-detail.ts/.html`, `ops-flow.ts`, `overview.ts`, `admin-layout.ts`, five admin list pages, two existing admin specs (stubs
  gained a `user` signal), `booking-flow.spec.ts` (stub gained one method), `proxy.conf.mjs`, `package.json`/`package-lock.json`
  (`@microsoft/signalr`). An empty stray file `src/app/man2.py`, left by an earlier edit script of mine, was removed.
* **Deployment/docs created:** `.dockerignore`, `docker/api.Dockerfile`, `docker/web.Dockerfile`, `docker/nginx.conf`, `tests/load/*`,
  docs 28 and 29, `docs/reports/*`; **modified:** `compose.dev.yaml`, `.env.example`, `README.md`, `PROJECT.md`, `docs/DEVELOPMENT.md`.

## 6. Remaining defects and risks (honest list)

1. The PostgreSQL run found and fixed one real defect; similar defects can remain in paths no test reaches (the provider adapters have
   only ever talked to fakes).
2. The real SignalR JS client was exercised in a browser for connect, rotation, outage and recovery; its error shape for a *refused* negotiate
   (403/404, which the service reads from the message text) was not (the "Live updates off" state was not reproduced).
3. The dispatcher is one sequential loop per instance: a slow read delays everyone on that instance. Unmeasured.
4. The max-age reaper's timer loop is not unit-tested (its registry action is).
5. `LiveService` retries a refused connection (401/403/404) every 5 minutes; with `Live:Enabled=false` that is a harmless periodic 404.
6. Existing stale prose in `README.md` / `PROJECT.md` beyond the SignalR lines was left as it was.
7. The production images build, but neither has been run and the nginx config has never been loaded; expect adjustments on first run.
8. The k6 scripts are unverified templates.

## 7. Blocked operations

Running the production images and the load scripts; restarting the owner's dev API container (needed to re-check browser finding B);
Stripe/Paymob sandbox calls; committing or pushing; touching the native PostgreSQL or `PULSERESERVE_TEST_DATABASE`.

## 8. Decisions that need authorization or an owner decision

1. (Granted and used 2026-10-23: PostgreSQL suites, image builds, browser pass on the dev stack.) Still to decide: may I restart the dev stack's API (`scripts/dev.ps1 migrate`) so it applies `LiveQueueStateNotifications`, and may I run the production images?
2. (Done.) Browser verification of the journeys.
3. Allow contact with the Stripe and Paymob **test** environments (credentials only through the environment).
4. Allow committing the working tree (it is one very large uncommitted change; suggest splitting by area).
5. Product decisions: catalog administration UI, ticket QR/gate scanning, a real notification channel, observability backend,
   evidence-retention durations, administrator role management, performance targets for the load tests.

## 9. Exact next steps

1. (Done) PostgreSQL suites: `env -u PULSERESERVE_TEST_DATABASE dotnet test tests/PulseReserve.IntegrationTests --no-build`.
2. Build the images (needs a yes): `docker build -f docker/api.Dockerfile -t pulsereserve-api .` and `docker build -f docker/web.Dockerfile -t pulsereserve-web .`.
3. (Authorization 2) start the dev stack, open the event page with two browser profiles, hold a seat in one, watch the other; then the checkout/order/admin pages; desktop and 375 px, light and dark.
4. (Authorization 3) Stripe/Paymob test-mode pass: checkout, webhook delivery, redirect return, late success, refund.
5. Run `tests/load` scenarios 1–3 on a staging stack and record the environment next to the numbers.
6. Update `docs/reports/feature-completion-matrix.md` with whatever the runs establish.

## 10. What the PostgreSQL run found, and a side effect to know about

**Defects found (first full run: 17 failures).**

1. **Real product defect — provider timeouts.** An `HttpClient` whose timeout elapses throws `TaskCanceledException`, which *is an*
   `OperationCanceledException`. Nine `catch (Exception ex) when (ex is not OperationCanceledException)` sites in the payment service,
   webhook service and workers therefore let a provider timeout escape: starting a payment answered **500** instead of the documented
   **503 `Payment.ProviderTemporarilyUnavailable`**, and in the workers a timeout during a status lookup, refund or outbox step would
   have bypassed the "outcome unknown, retry later" bookkeeping. Fixed with `CancellationRules.IsCallerCancellation(ex, token)` (only a
   cancellation the caller requested propagates); 5 unit regression tests (initiation for both idempotent and non-idempotent providers,
   caller cancellation still stops the request, status-lookup and refund timeouts). The earlier unit tests used `TimeoutException`, which is why
   they missed it.
2. **Test isolation (not product).** Classes sharing one container asserted global counts; `PostgreSqlDatabaseFixture.ResetAsync()` now
   empties the four module schemas before each test of the two affected classes.
3. **Test expectations (not product).** `True` vs `true`; a leak guard that rejected the legitimate code `Operations.ExceptionNotConfirmed`;
   two tests that depended on rows left by other tests; two races in my own new live tests. All fixed without weakening any assertion.

**Side effect.** Starting Docker Desktop also restarted the owner's own development stack (`pulsereserve-dev-*`, `restart: unless-stopped`).
That stack bind-mounts this working tree and migrates **its own dev database volume** on start, so it applied the pending migrations to
that database by itself: the two live-layer migrations (`…LiveChangeNotifications`, additive functions and triggers; `Down` drops them) and
the five earlier financial migrations from this workstream (`AttemptCheckoutUrl`, `ManualRefundRecord`, `LateSuccessPaymentExceptions`,
`FinancialTransitionHistory`, `PaymentAnomalies`). It was observed read-only (`docker ps`, `docker logs`) and left running and untouched; the
test runs used their own containers and never touched it or the native PostgreSQL. If you do not want that dev database migrated, tell me and
I will stop treating it as expected; the rollbacks refuse while financial rows exist, by design.

## 11. Production images and the browser pass (2026-10-23)

**Images.** `docker build -f docker/api.Dockerfile` and `-f docker/web.Dockerfile` both succeeded on the first attempt, no warnings:
`pulsereserve-api:verify` 355 MB (ASP.NET Core 10.0.13 runtime, non-root user 1654, `Production`, port 8080) and `pulsereserve-web:verify` 91 MB
(unprivileged nginx, user 101, port 8080). Neither was run; the nginx config has never been loaded. The tags are local only (nothing pushed).

**Browser pass.** Full report: `docs/reports/browser-verification-report.md` (22 checks, simulator payments, two real accounts and a scripted
second customer, desktop and 375 px, light and dark). Headline: the live layer works end to end in a real browser through the dev proxy and real
cookies — another customer's hold appears and disappears on the seat map, my own order page follows payment, paid and ticket issuance in a second tab,
a real expired hold is released by the worker and the open page updates without a request, an outage shows "Reconnecting", the slow poll covers
it, and recovery returns to "Live" with the missed change shown within about 14 s.

**What the browser found that 770 unit and 266 integration tests had not:**

| | Finding | Status |
|---|---|---|
| A | Two hub connections per signed-in page load (the identity effect reopened the connection `start()` had just opened) | Fixed + regression spec; re-verified (one `negotiate`) |
| B | The administrator overview went stale while the workers moved outbox/reconciliation items between queue states (only `NeedsReview` was announced; the page shows a count per state) | Fixed with migration `20261022232110_LiveQueueStateNotifications` + a PostgreSQL test; **browser re-check pending**: the dev API hot-reloaded the code but applies migrations only on restart, and restarting your container was not authorized |
| C | `GET /api/reservations/active` answered 404 for "no hold", putting two red errors in every event-page console | Changed to `204 No Content` (older-server 404 still read as "none"); verified |
| D/E | One extra read per hard page load (by design); a signed-out tab in another window keeps its identity until rotation | Documented in doc 28 |

**State left behind on your dev stack:** two `@pulsereserve.test` customers, a few reservations and two simulator-paid orders in its database;
the dev API hot-reloaded this working tree's code; no container was restarted, stopped or reconfigured; the browser is signed out.

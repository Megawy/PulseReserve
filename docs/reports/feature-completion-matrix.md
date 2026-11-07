# PulseReserve — feature-completion matrix

Living document. Last updated 2026-10-23 on branch `upgrade/dotnet-10` (uncommitted working tree).

**How to read the status column.** A feature carries the *highest level of evidence that was actually produced*, in this order:
`Missing` < `Partially implemented` < `Implemented` < `Unit-tested` < `PostgreSQL integration-tested` < `End-to-end tested` <
`Provider-sandbox-tested` < `Browser-verified`. Two further markers are not levels: `Blocked` (cannot be advanced without an
authorization or an environment that is not available) and `Requires owner decision`.

**Evidence actually produced in this environment** (see the final report for the commands and counts):

* `Unit-tested` — executed and passing: 770 .NET unit tests, 72 architecture tests, 497 Angular tests.
* `PostgreSQL integration-tested` — **executed on 2026-10-23** after the owner authorized Docker/Testcontainers: all 267 integration tests
  pass against throw-away `postgres:17.11-alpine` containers (four consecutive full runs after the fixes in doc 27 §0; the first run
  failed 17 tests and exposed a real timeout-handling defect, now fixed). Caveats that apply to every row marked this way: the suite runs as
  the container's superuser (the least-privilege behaviour is shown by one dedicated test), provider calls are faked, and the race tests
  show observed contention rather than a controlled interleaving.
* `Browser-verified` — **done for the journeys listed in `browser-verification-report.md`** (2026-10-23, built-in browser on the dev stack,
  simulator payments, desktop and 375 px, light and dark): event list, registration, sign-in/out, seat map with live updates from another
  customer, hold, "find my hold", checkout, simulated payment, tickets, orders pages, all administrator console pages, hold expiry, outage and
  recovery. Rows below say "Browser-verified" only where that run covered them; everything else in the table is not browser-verified.
* `End-to-end tested` (as a formal suite) and `Provider-sandbox-tested` — **none**. Stripe/Paymob were not contacted.

| # | Feature | Backend | Frontend | Highest evidence | Notes / blockers |
|---|---|---|---|---|---|
| **Identity and access** | | | | | |
| 1 | Registration, sign-in, sign-out, session mirror | Implemented | Implemented | PostgreSQL integration-tested | `IdentityApiHttpTests` passed. Cookie auth, antiforgery on every POST, SameSite=Strict (doc 12). |
| 2 | Administrator role | Implemented (deployment configuration `Identity:AdminUserIds`) | Implemented (guard + hint) | Unit-tested | **Requires owner decision**: role management through an identity provider or admin screen is not built; today an admin is a configured user id. |
| 3 | Rate limits, forwarded headers, simulator isolation | Implemented | n/a | Unit-tested | Doc 23. |
| **Catalog** | | | | | |
| 4 | Venues, layouts, events, prices, publish, start sale | Implemented (admin API) | Missing (no admin UI) | PostgreSQL integration-tested | **Requires owner decision**: a catalog administration UI was not in the approved scope. |
| 5 | Public event list and detail | Implemented | Implemented | Unit-tested | |
| **Reservation** | | | | | |
| 6 | Seat availability (public, no holder identity) | Implemented | Implemented | PostgreSQL integration-tested | |
| 7 | Atomic, idempotent seat hold (≤ 8 seats, one active hold per event) | Implemented | Implemented | PostgreSQL integration-tested (race tests show contention, not a controlled interleaving) | No oversell is enforced by the database (doc 13). |
| 8 | Release; lazy expiry | Implemented | Implemented | PostgreSQL integration-tested | |
| 9 | **Proactive hold expiry** (new) | Implemented (`HoldExpiryWorker`, `FOR UPDATE SKIP LOCKED`) | n/a | PostgreSQL integration-tested; **Browser-verified** (a real hold expired and the open page updated by itself) | Never closes `Checkout` reservations. Correctness does not depend on it. |
| 10 | **Find my live hold** (new, `GET /api/reservations/active`, `204` when none) | Implemented | Implemented (event page restores it; conflict path shows it) | PostgreSQL integration-tested; **Browser-verified** (local state cleared, hold found) | Closes a gap: a hold made in another browser or after clearing storage was invisible and blocked a new hold. |
| 11 | Countdown from server expiry, server-clock offset | n/a | Implemented | Unit-tested | |
| **Checkout, orders, payments** | | | | | |
| 12 | Order from a hold; server-computed totals; immutable snapshot | Implemented | Implemented | PostgreSQL integration-tested | Clients cannot choose trusted amounts. |
| 13 | Payment options per order; provider choice | Implemented | Implemented | Unit-tested | |
| 14 | Payment simulator (development only) | Implemented | Implemented, labelled "(simulated)" | PostgreSQL integration-tested | Refuses to run outside Development/Testing or beside a real provider. |
| 15 | Stripe hosted Checkout | Implemented | Implemented (redirect, return page) | Unit-tested with a fake HTTP layer | **Provider-sandbox-tested: no.** Blocked on explicit authorization to contact the Stripe test environment. |
| 16 | Paymob Unified Checkout | Implemented | Implemented | Unit-tested with a fake HTTP layer | **Provider-sandbox-tested: no.** Same blocker. |
| 17 | Webhooks (signature, acknowledgement semantics, fail-closed mapping) | Implemented | n/a | Unit-tested | A 200 never means "order paid" (doc 19). |
| 18 | Late success exceptions; payment anomalies | Implemented | Implemented (admin) | PostgreSQL integration-tested | |
| 19 | Financial transition history; evidence protection | Implemented (append-only triggers) | Implemented (lookup by order) | PostgreSQL integration-tested | Retention durations: **Requires owner decision** (doc 25). |
| 20 | Reconciliation worker, outbox, tickets, notifications | Implemented (development notification adapter only) | Implemented (tickets on order page) | PostgreSQL integration-tested | A real e-mail/SMS sender is **Missing — Requires owner decision** (vendor, templates, consent). |
| 21 | Refunds (provider) and manual completion by an administrator | Implemented | Implemented (admin) | PostgreSQL integration-tested | A manual refund is never shown as provider-confirmed. |
| 22 | Least-privilege database roles | Documented runbook | n/a | PostgreSQL integration-tested (the runbook's grants executed in a throw-away container) | Doc 24; the real deployment role still needs an operator and a populated database. |
| **Customer account** | | | | | |
| 23 | Orders list, order detail, payment confirmation page, tickets | Implemented | Implemented | Unit-tested | Ticket QR rendering and gate scanning: **Missing — Requires owner decision** (scope not approved). |
| **Administrator console** | | | | | |
| 24 | Payment operations: overview, reconciliation, refunds, exceptions, anomalies, history, outbox, audit log | Implemented | Implemented | Unit-tested | Operator actions are audited with the administrator id. |
| **Real-time (new)** | | | | | |
| 25 | Live seat availability, my reservation, my order, administrator areas (**Browser-verified** except admin queue-state counts, see the browser report finding B) | Implemented (doc 28) | Implemented | Unit-tested (real hub over real SignalR clients); PostgreSQL integration-tested (7 tests: NOTIFY after commit, rollback silence, listener outage, hold expiry, end to end through the real hub) | Full detail in `realtime-functionality-matrix.md`. |
| **Platform** | | | | | |
| 26 | Health endpoints (`/health/live`, `/health/ready`) | Implemented | n/a | Unit-tested | |
| 27 | Operational signals | Partially implemented | n/a | Unit-tested | Logs by type only; a .NET `Meter` for the live layer; no exporter. **OpenTelemetry/collector: Missing — Requires owner decision** (backend to export to). Doc 26 lists suggested alerts. |
| 28 | Development stack (Compose) | Implemented | Implemented | Observed healthy (`docker ps`) after Docker Desktop restarted the owner's stack on 2026-10-23; no journey was exercised | The stack bind-mounts the working tree and migrates its own dev database on start, so it applied the new migrations by itself (see the final report). |
| 29 | Production images and reverse proxy | Written (`docker/api.Dockerfile`, `docker/web.Dockerfile`, `docker/nginx.conf`) | | **Built 2026-10-23** (API 355 MB non-root on ASP.NET Core 10.0.13; web 91 MB), **never run** | Running them (and loading the nginx config) was not authorized. A CI image-build job was deliberately not added (an unrun job could redden the pipeline). |
| 30 | CI | Implemented (build, unit, architecture, integration via Testcontainers; frontend lint/test/build; compose config) | | not run here | Picks up all new tests automatically. |
| 31 | Load and contention tests | Plan and scripts written (`docs/reports/realtime-load-test-plan.md`, `tests/load/`) | | **not executed** | No capacity number in any document is measured. |
| 32 | PostgreSQL integration verification | | | **Done (2026-10-23)** | 266 / 266 passed, four consecutive full runs after fixes; doc 27 §0 lists what the first run found. Not covered: behaviour on a populated real database, connection pooling under real concurrency, true parallel unique-index arbitration (doc 27 §5). |
| 33 | Browser verification (layout, light/dark, keyboard, mobile, reduced motion, WebSocket through the proxy) | | | **Done 2026-10-23 for the customer and administrator journeys** (`browser-verification-report.md`) | Not covered: other browsers/devices, screen readers, reduced motion, the WebSocket frames, the production nginx image. Findings A and C fixed; B fixed and PostgreSQL-tested, browser re-check pending a restart of the dev API. |
| 34 | Provider sandbox verification | | | **Blocked** | Stripe/Paymob were not contacted. |

## Known defects and risks (not hidden)

* The first PostgreSQL run found one real defect (an HTTP-client timeout during a provider call escaped as a 500 or bypassed the
  "outcome unknown" handling at nine sites); it is fixed with regression tests. Other defects of that kind can still exist in paths the
  suite does not reach; the provider adapters have only ever talked to fakes.
* The live layer's capacity (connections per instance, notifications per second) is unmeasured.
* The listener and the dispatcher live in the same process as the API. A very slow database read inside the dispatcher delays
  messages for everyone on that instance (reads are per changed entity and coalesced, but sequential).
* `README.md` / `PROJECT.md` carried many stale status statements; only the SignalR-related ones were corrected here.

## Decisions that need the owner

1. (Done 2026-10-23) Docker/Testcontainers authorized; the PostgreSQL suites ran. Still to authorize: building the production images.
2. (Done 2026-10-23.) Still to authorize: restarting the dev stack's API (`scripts/dev.ps1 migrate`) so it applies `LiveQueueStateNotifications`, to re-check the admin overview in the browser; running the production images.
3. Authorize contacting the Stripe and Paymob **test** environments, and provide test credentials through the environment (never in the repository).
4. Choose: catalog administration UI; ticket QR/gate scanning; real notification channel; OpenTelemetry backend; evidence retention durations; role management model.

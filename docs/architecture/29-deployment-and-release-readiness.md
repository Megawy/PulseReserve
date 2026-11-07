# 29 · Deployment and release readiness (assessment)

Status 2026-10-23, branch `upgrade/dotnet-10`, working tree uncommitted. This is an **assessment of what exists and what has not been
verified**, not a certificate. The PostgreSQL integration suites have been executed (266 / 266, doc 27 §0). Nothing in the deployment
assets has been built or run, and no browser or provider sandbox was used.

## 1. Assets

| Asset | State | Verified |
|---|---|---|
| `compose.dev.yaml`, `docker/api.dev.Dockerfile`, `docker/web.dev.Dockerfile`, `docker/web-entrypoint.sh` | development stack (PostgreSQL 17, API with `dotnet watch`, Angular dev server with `/api` and `/hubs` proxies) | worked in earlier sessions; **not re-run** after the live settings were added (two env pass-throughs: `LIVE_ENABLED`, `HOLD_EXPIRY_ENABLED`) |
| `docker/api.Dockerfile` | production image: SDK build → `aspnet:10.0` runtime, `Production`, non-root, no secrets baked in | **built 2026-10-23** (355 MB; runtime ASP.NET Core 10.0.13, user 1654, port 8080, `Production`); **never run** |
| `docker/web.Dockerfile`, `docker/nginx.conf` | production web image: Angular build → unprivileged nginx; proxies `/api/` and `/hubs/` (WebSocket upgrade), SPA fallback, long cache for hashed assets | **built 2026-10-23** (91 MB, user 101, port 8080); **never run, nginx config never loaded** |
| `.dockerignore` | keeps `.git`, `.env*`, `bin/obj`, `node_modules`, tests and docs out of build contexts | not exercised |
| `.github/workflows/ci.yml` | build, unit, architecture, Testcontainers integration; frontend lint/test/build; `docker compose config` | not run here; it runs every new test automatically |
| `tests/load/*.k6.js` | load and contention scripts | **never run** |
| `scripts/` | developer helper scripts (earlier work) | unchanged |

Decision: no CI job builds the production images. A job that has never run could break the pipeline; once someone has built the images
once by hand, adding `docker build` steps is two lines each.

## 2. What a production deployment must provide

| Concern | Requirement | Source |
|---|---|---|
| Database | PostgreSQL with the four schemas migrated by a **separate identity** (`Database:MigrateOnStartup` is honoured only in Development); application connects as a least-privilege role | docs 24, 27 |
| Connection | `ConnectionStrings__Catalog` (secret) | `docs/DEVELOPMENT.md`, doc 24 |
| Topology | `Security__ForwardedHeaders__Mode` (`Direct` or `Proxy` + `KnownProxies`/`KnownNetworks`). The API **will not start** without it outside Development/Testing | doc 23 |
| TLS | terminated in front; the auth cookie is `Secure` (HttpOnly, SameSite=Strict) outside Development, so the hub must also be reached over `wss://` on the same origin | doc 12 |
| Administrators | `Identity__AdminUserIds` (comma-separated user ids) | doc 12 |
| Payments | exactly one of simulator or real providers; the simulator **refuses** to run in Production. Stripe/Paymob keys, webhook secrets, return URLs via environment/secret store only | docs 16, 22 |
| Webhooks | the API must be reachable by the providers on `/api/webhooks/{provider}`; ack semantics in doc 19 | doc 19 |
| Real time | proxy passes WebSocket upgrades on `/hubs/`; idle read timeout ≥ 60 s; optional `Live__AllowedOrigins` if the SPA is on another origin | doc 28 |
| Runtime version | ASP.NET Core **≥ 10.0.13** (GHSA-334f-vvp4-7g6p; the hub never enables stateful reconnect, but run the fix) | doc 28 |
| Workers | one process hosts API + payment workers + live listener + hold-expiry worker; multiple instances are safe by design (`SKIP LOCKED`, leases), but this was **not tested** | docs 14, 28 |
| Notifications | only the development adapter exists: a real channel must be built and chosen before launch | feature matrix #20 |
| Secrets | never in the repository, images, logs or documents; `.env` is git-ignored | all |
| Observability | logs by exception type; .NET meter `PulseReserve.Live`; health at `/health/live`, `/health/ready`; **no exporter** | doc 26 |

## 3. Release gates that are NOT met

1. ~~PostgreSQL integration suites have never been executed~~ — done 2026-10-23 (266 / 266). Still open from doc 27 §5: a populated real database, pooling under real concurrency, true parallel unique-index arbitration.
2. ~~No browser verification~~ — done 2026-10-23 on the dev stack (`docs/reports/browser-verification-report.md`). Still open: other browsers, assistive technology, reduced motion, the WebSocket through the production nginx image.
3. No provider sandbox run for Stripe or Paymob.
4. The production images build, but they and the nginx configuration have never been **run** (building was authorized, running was not).
5. No load or contention run; no capacity statement can be made.
6. Open product decisions: catalog admin UI, ticket QR/gate scanning, a real notification channel, an observability backend, evidence
   retention durations, role management (feature matrix).
7. Nothing is committed: the whole body of work is an uncommitted working tree on `upgrade/dotnet-10`.

## 4. First deployment checklist (once the gates above have owners)

1. (Done 2026-10-23) PostgreSQL suites run and fixed; next, doc 27 §5's checks that need a long-lived database.
2. (Images build, 2026-10-23.) Start them against a disposable database; confirm `/health/ready`; load `docker/nginx.conf`.
3. Apply migrations with the owner role; apply the grant template of doc 24 for the application role; re-run the least-privilege test.
4. Configure `Security:ForwardedHeaders`, administrators, one payment provider in **test mode**, then do the provider sandbox pass (webhook
   delivery, redirect return, late success, refund) with the owner present.
5. Walk the customer and administrator journeys in a real browser on desktop and a 375 px viewport, light and dark, keyboard only once.
6. Run `tests/load` scenarios 1–3 against a staging stack and record the environment next to the numbers.
7. Only then decide about live payment keys, which this repository will never contain.

## 5. Rollback

Application images are stateless: roll back by redeploying the previous image. Migrations that would destroy financial or customer
records **refuse** to roll back while such records exist (doc 24, "migrations: a rollback may not silently destroy financial evidence");
the two real-time migrations only add functions and triggers, and their `Down` drops them without touching data. Turning the live layer
off needs no rollback: `Live__Enabled=false` (pages fall back to their normal refresh), and `Reservation__HoldExpiry__Enabled=false`
returns holds to lazy expiry.

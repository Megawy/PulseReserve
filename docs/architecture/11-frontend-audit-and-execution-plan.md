# 11 – Frontend Audit and Execution Plan

Status: **Audit complete. Implementation NOT started. Awaiting explicit approval.**
Date: 2026-10-20. Scope: read-only audit of the repository at branch `master` (HEAD `41b2ef4` plus uncommitted backend work).
Backend counterpart: `docs/architecture/10-backend-audit-and-execution-plan.md` – **not present** at the time of this audit (see §8).

## 1. Method and commands

Read-only inspection only. No source files changed, no network calls made, no packages installed.

| # | Command / action | Result |
|---|---|---|
| 1 | `git ls-files`, `ls -la`, `find` for `package.json`, `angular.json`, `tsconfig*.json`, `*.ts`, `Dockerfile*`, `docker-compose*`, `*.yml`, `*.yaml` (excluding `bin/obj/node_modules`) | **No matches.** No frontend, container, or CI files. |
| 2 | `ls -a .github` | Does not exist. |
| 3 | `node -v` / `npm -v` / `dotnet --version` | v24.19.0 / 11.17.0 / 10.0.401 (toolchain is available). |
| 4 | Read `src/PulseReserve.AppHost/Program.cs`, `Api/CatalogEndpoints.cs`, `Api/ApiContracts.cs`, `Api/ErrorMapping.cs`, Catalog `*Response.cs` files | Contract inventory in §4. |
| 5 | Grep for Angular / SignalR / JWT / CORS / auth in `*.cs`, `*.md`, `*.csproj`, `*.json` | Only documentation references (PROJECT.md, README.md, docs 01 and 09). No code. |
| 6 | Frontend build / lint / test | **Not run – nothing to run.** There is no `package.json`, so `npm ci`, `ng build`, `ng lint`, `ng test` do not apply. Not run is different from passed. |
| 7 | Backend build / tests | **Not run** (outside frontend scope; backend owner is auditing). |

## 2. What actually exists

- **No Angular application.** No `package.json`, `angular.json`, `tsconfig`, `.ts` file, route, component, service, interceptor, environment file, or frontend test anywhere in the repo.
- **Only frontend trace:** `.gitignore` lines 45–48 (`## Node / Frontend (Planned)`, `node_modules/`, `.angular/`).
- **No Docker or CI:** no Dockerfile, compose file, or `.github/workflows`. Doc 09 reached the same conclusion; I independently confirmed it.
- **Backend HTTP API exists (Catalog only):** .NET Minimal API under `/api/catalog`, with Swagger/OpenAPI registered in Development (`Program.cs`: `AddSwaggerGen`, `UseSwagger`, `UseSwaggerUI`).
- **Not present on the backend** (matches doc 09): authentication/authorization, CORS configuration, SignalR hub, Reservation/Booking/Payments/Identity endpoints (those modules are `Module.cs` marker stubs), health endpoint beyond `GET /`.
- **Design intent (docs only):** Angular SPA for customer, admin and gate scanner; JWT; SignalR deltas; Angular phases 8 and 9 in doc 09 §11 (`09-system-audit-and-target-architecture.md:299,317,318,1074,1099`).

## 3. What is missing

Everything on the frontend: workspace, tooling, routing, state, API client, auth, UI, seat map, booking flow, payment UI, SignalR client, tests, Docker, CI. Attribution of gaps:

| Gap | Frontend-only? | Blocked by backend? |
|---|---|---|
| Angular workspace, lint, unit-test, build setup | Yes | No |
| Typed API client for existing Catalog endpoints | Yes | Partially (see §4 issues) |
| Event browse / event detail / seat-map display | Yes | **Yes** – no list endpoint, no availability data |
| Login / registration / route guards / token handling | Yes | **Yes** – no Identity API |
| Seat hold, checkout, payment, tickets | Yes | **Yes** – Reservation/Payments not implemented |
| Real-time updates | Yes | **Yes** – no hub |
| Admin console (venue, layout, event management) | Yes | Mostly no – Catalog write endpoints exist, but unauthenticated |

## 4. API integration status

### 4.1 Existing endpoints (verified in `CatalogEndpoints.cs`)

Base `/api/catalog`. All anonymous. Errors use `ProblemDetails` (`ErrorMapping.cs`): `title` = domain error code, `detail` = message, status 409 if code contains "Concurrency"/"Stale", 404 if it contains "NotFound", otherwise 400.

| Method and path | Request | Success | Documented errors |
|---|---|---|---|
| POST `/venues` | `CreateVenueApiRequest` | 201 `CreatedIdResponse{Id}` | 400 |
| GET `/venues/{venueId}` | – | 200 `VenueResponse` | 404 |
| POST `/seating-layouts` | `CreateSeatingLayoutApiRequest` | 201 `CreatedIdResponse` | 400, 404 |
| GET `/seating-layouts/{layoutId}` | – | 200 `SeatingLayoutResponse` | 404 |
| POST `/seating-layouts/{id}/sections` `/rows` `/seats` `/price-tiers` `/lock` | Add\*/Lock requests | 200 | 400, 404, 409 |
| POST `/events` | `CreateEventApiRequest` | 201 `CreatedIdResponse` | 400, 404 |
| GET `/events/{eventId}` | – | 200 `EventResponse` | 404 |
| POST `/events/{id}/prices` `/publish` `/start-sale` | Request records | 200 | 400, 404, 409 |

Response shapes: `EventResponse` (id, name, description, venueId, seatingLayoutId?, scheduled start/end UTC, status string, published/onSale/cancelled timestamps, cancellationReason, prices[priceTierId, amount, currency]); `SeatingLayoutResponse` (id, venueId, name, isLocked, sections, rows, seats[id, rowId, sectionId, label, coordinateX, coordinateY, priceTierId], priceTiers).

### 4.2 Contract issues affecting the frontend (questions, not decisions)

1. **No list/search endpoint for events or venues.** A customer cannot browse events. Only GET by id exists.
2. **No seat availability.** `SeatResponse` is static layout data. Held/reserved/sold state does not exist anywhere in the API.
3. **Strongly-typed IDs** (`EventId`, `SeatId`, …) – JSON wire format (plain GUID string vs. object) is not confirmed. Needs a captured OpenAPI document or integration test, not a guess.
4. **Status strings** (`EventResponse.Status`) – allowed values are not published as an enum in the response record.
5. **Error codes** are free-form strings; no documented stable list. The 400/404/409 mapping is by substring, which the UI must not depend on for logic.
6. **No CORS policy**; an Angular dev server on another origin cannot call the API except through a dev proxy.
7. **No authentication**; `Program.cs` has no auth middleware.
8. **No OpenAPI artifact is committed**; it is only served at runtime in Development. A committed or build-generated `openapi.json` would make contract testing possible.
9. No `Idempotency-Key` convention or `ETag`/version concept is exposed for writes (relevant to double-click safety in booking).

## 5. UI/UX, accessibility, responsiveness, tests

Nothing exists to assess, so there are no findings against existing UI. Requirements the plan carries forward are in §6 (acceptance criteria). Test coverage today: **0 frontend tests**. Existing .NET tests (unit, architecture, integration) are backend-only and were not run.

## 6. Proposed architecture (subject to approval)

- **Angular**: current supported major at implementation time (verify with `ng version` / Angular release docs before scaffolding; Node 24.19 must be on that version's supported list). Standalone components, signals, built-in `HttpClient`, functional guards/interceptors, lazy-loaded feature routes. No NgModules, no NgRx unless a concrete need appears.
- **Layout**: `frontend/pulsereserve-web/` at repo root, organized by feature: `core/` (http, auth, error handling, config), `shared/` (ui primitives actually reused), `features/{catalog,seatmap,booking,account,admin}/`.
- **Free-only dependencies**: Angular, RxJS, `@microsoft/signalr` (MIT, only after hub is verified), Angular CDK (MIT) for a11y utilities if useful, Jasmine/Karma or the Angular-default runner, Playwright (Apache-2.0) for E2E, `@axe-core/playwright` (MPL-2.0) for accessibility checks, ESLint with `angular-eslint`. No commercial UI kits.
- **API models**: hand-written typed interfaces initially, with a proposal to generate them from a committed OpenAPI document once backend agrees (§7). Generated code is only adopted if backend publishes the spec.
- **State**: server is authoritative. Seat selection is a local, tentative *intent*; a seat is shown as "yours" only after a successful server hold response; every hold/booking error triggers an authoritative refetch.
- **Seat map**: SVG or CSS-grid rendered from `coordinateX/Y`; statuses (available, held, reserved, unavailable) distinguished by shape/pattern plus text label, not color alone; each seat a focusable control with accessible name ("Section A, Row 3, Seat 12, available, 45.00 USD"); arrow-key navigation (roving tabindex); zoom/pan with keyboard alternative; list-view fallback for screen readers and small screens.
- **Booking/payment safety**: disable-on-submit plus an in-flight guard; client-generated idempotency key *only if* backend adopts one; timeouts show an "uncertain" state that resolves by querying reservation status, never by assuming failure or success; page refresh restores state from the server.
- **Auth**: token storage strategy (memory vs. httpOnly cookie) depends on backend decision (§7). Guards are UX only.
- **SignalR**: reconnect with backoff; on reconnect or sequence gap, refetch availability snapshot; polling fallback.

## 7. Dependencies on backend decisions (open questions)

| ID | Question for backend owner | Blocks phase |
|---|---|---|
| B1 | Will an OpenAPI document be committed or generated in CI? Can IDs be serialised as plain GUID strings? | 1 |
| B2 | CORS policy for the SPA origin, or should the SPA be served same-origin / behind a dev proxy? | 1 |
| B3 | Event/venue list and search endpoint (filters, paging, sorting) | 2 |
| B4 | Seat availability read model: endpoint shape, statuses, versioning/sequence number | 3, 6 |
| B5 | Auth model: login/refresh endpoints, token type and lifetime, storage (bearer vs. cookie), roles/claims, CSRF stance | 4 |
| B6 | Hold / checkout / confirm / cancel endpoints, hold expiry semantics, idempotency, conflict error codes | 5 |
| B7 | Payment flow: provider-hosted redirect vs. embedded, webhook-driven status, polling endpoint for outcome | 5 |
| B8 | SignalR hub path, auth on hub, message schema, ordering/sequence guarantees, reconnect semantics | 6 |
| B9 | Stable machine-readable error codes in `ProblemDetails` (e.g. `code` extension) | 1 |
| B10 | Ticket/QR retrieval and gate validation endpoints | 7 |

Proposed minimal contracts (proposals only, **not implemented against**):
- `GET /api/catalog/events?status=OnSale&page=&pageSize=` → `{ items: EventSummary[], total }`
- `GET /api/events/{eventId}/availability` → `{ version: long, seats: [{ seatId, state: "Available"|"Held"|"Reserved"|"Unavailable" }] }`
- `ProblemDetails` extension member `code` (stable string) alongside `title`.

## 8. Backend report status

`docs/architecture/10-backend-audit-and-execution-plan.md` does not exist. This plan was derived from the code and doc 09 only. When it appears I will read it, reconcile B1–B10, and not alter its decisions. Doc 09 already states a target of Angular in Phase 8 and SignalR in Phase 9 and lists Identity (G2) and Reservation (G3/G4) as prerequisites. That agrees with the sequencing below.

## 9. Prioritized phases and acceptance criteria

Each phase ends with: build, lint, unit tests pass; changed files, results and unresolved issues reported. No feature is called complete without tests.

**Phase 0 – Decisions (no code).** Approve this plan; answer B1, B2, B5 at minimum; pick Angular major.
*Accept:* written answers or an updated doc 10.

**Phase 1 – Workspace and API foundation.** Scaffold Angular app under `frontend/`; strict TypeScript; ESLint; test runner; dev proxy or CORS; typed `ApiClient`; HTTP error interceptor mapping `ProblemDetails`; `AppConfig` for base URL; app shell with accessible layout, skip link, responsive nav; shared loading/empty/error/retry components.
*Accept:* `npm ci`, `ng build`, `ng lint`, `ng test` all pass in a clean checkout; interceptor and error-mapping unit tests; axe check on shell reports no violations; documented run instructions.

**Phase 2 – Read-only catalog (Events).** Event detail page via existing `GET /events/{id}` (and list page only after B3). Venue/layout display.
*Accept:* component tests for loading/empty/404/error-retry/success; service tests with `HttpTestingController` against fixtures taken from the verified contract; responsive at 320/768/1280 px.

**Phase 3 – Seat map (static, then availability).** Render layout from `SeatingLayoutResponse`; keyboard navigation; status legend; list fallback. Availability states after B4.
*Accept:* tests for keyboard nav, focus visibility, ARIA names, non-color status cues, selection limit; stale-availability test (server says taken → UI updates and informs user); axe passes.

**Phase 4 – Authentication UI (after B5).** Login/logout, token handling, auth interceptor, 401/403 handling, route guards matching backend roles.
*Accept:* tests for expired token, 401 redirect with return URL, 403 view, guard behavior; docs state that guards are not a security boundary.

**Phase 5 – Hold, checkout, payment UI (after B6/B7).** Hold countdown (server expiry authoritative), duplicate-click guard, timeout/uncertain state, refresh recovery, payment outcome polling.
*Accept:* tests for double submit (one request), timeout then status resolution, 409 conflict refetch, hold expiry, refresh restoring state; no real payments in any test.

**Phase 6 – SignalR (after B8).** Connection service, per-event group join, delta apply with sequence check, reconnect and snapshot refresh, polling fallback.
*Accept:* tests with a fake hub connection for reconnect, out-of-order/gap, disconnect banner; manual verification against the real hub documented.

**Phase 7 – Tickets, admin, gate scanner (after B10 and auth).** Separate approval before starting.

**Phase 8 – Delivery.** Playwright E2E for critical flows against mocked API (`page.route`) and, later, an isolated compose environment; CI workflow `frontend-ci.yml`: `npm ci`, lint, unit tests, build, E2E on mocks, upload artifacts. **Verification only; deployment is a separate, not-yet-designed workflow** and needs no credentials now. Dockerfile for the SPA (nginx static) is optional and coordinated with backend compose work (doc 09 G11).
*Accept:* CI green on a pull request; no secrets in repo; deploy explicitly out of scope.

## 10. Test strategy summary

| Area | Tool | Phase |
|---|---|---|
| Unit (services, interceptors, pure logic) | Angular test runner | 1+ |
| Component + service integration | TestBed + `HttpTestingController` | 2+ |
| API contract compatibility | Fixtures validated against committed OpenAPI (needs B1) | 1+ |
| Auth/authorization UI | Component tests + E2E | 4 |
| Seat selection, booking errors, duplicate clicks, stale availability | Component tests + E2E | 3, 5 |
| SignalR reconnect/refresh | Fake `HubConnection` | 6 |
| Accessibility and responsive | axe via Playwright, viewport matrix | 1+ |
| Critical-flow E2E | Playwright with mocked API | 8 |

## 11. Risks

- Building the UI before availability, auth and reservation contracts exist risks rework; hence Phases 3–6 are gated.
- Contract drift: mitigated by a committed OpenAPI document and contract tests.
- Node 24 support depends on the chosen Angular major; verify first.

## 12. Request for approval

Please confirm: (a) the plan and phase order, (b) approval of Phase 1 to start now (it only depends on B1/B2 and can use a dev proxy and hand-typed models), (c) who answers B1–B10. Nothing will be implemented until you approve.

# PulseReserve

> Seat reservation and ticketing platform under construction as a **Modular Monolith** on **.NET 10 / ASP.NET Core 10** and **PostgreSQL**. The **Catalog** module and the first **Reservation** slice (per-event seat inventory and seat holds) are implemented. Payments, ticketing, messaging, and the frontend product features are planned (see [doc 09](docs/architecture/09-system-audit-and-target-architecture.md)).

---

## 1. Project Overview

PulseReserve is a high-concurrency event reservation system designed to withstand extreme flash-sale spikes (such as major stadium concerts or championship matches) while providing absolute guarantees against double-booking.

The platform is structured as a **Modular Monolith** with strictly decoupled bounded contexts, schema-per-module database isolation, and clear event-driven asynchronous integration boundaries.

### Technology Stack

**In use today (verified in code):**
* **Runtime & Framework:** .NET 10 LTS (`net10.0`), ASP.NET Core 10 Minimal APIs, C# 13. SDK pinned to 10.0.401 via `global.json`.
* **Authoritative Persistence:** PostgreSQL (`catalog` schema only), EF Core 10.0.12 + Npgsql EF provider 10.0.3 with aggregate-level optimistic concurrency
* **API Documentation:** Swashbuckle (Swagger UI in Development)
* **Testing:** xUnit, FluentAssertions 6.x, NetArchTest, Testcontainers (disposable PostgreSQL for integration tests)
* **CI:** GitHub Actions (`.github/workflows/ci.yml`)

**Implemented since this paragraph was first written:** cookie-authenticated Identity, seat reservations, checkout and payments (simulator, Stripe, Paymob), reconciliation, refunds, the transactional outbox with in-process dispatch, tickets, the administrator operations console, the Angular product features, and **real-time updates over ASP.NET Core SignalR** (`/hubs/live`, see `docs/architecture/28-real-time-architecture.md`). **Still planned / not implemented:** ticket QR check-in at the gate, OpenTelemetry export (the live layer publishes a .NET `Meter` only), Kafka. Redis is **not** planned (ADR-009 working baseline); it will be adopted only on documented triggers.

**Frontend foundation:** `frontend/pulsereserve-web/` contains an Angular 22 Phase 1 shell. It has a typed read-only Catalog client (venue, seating layout, and event GET endpoints), ProblemDetails error handling, a dev proxy to `http://localhost:5000`, and home and not-found pages. No UI route calls the API yet.
* Verified 2026-10-20: `npm run lint`, `npm run test:ci` (43 tests), `npm run build`, and `npm run format:check` all pass.
* The client's wire contract was confirmed against the running API.

---

## 2. Core Engineering Goals

These are design goals. Goals 1, 2, and 5 are **not yet implemented**; no reservation code exists.

1. **Zero Double-Booking Guarantee (planned):** Authoritative PostgreSQL enforcement through a per-event seat inventory, atomic conditional updates, and database constraints (ADR-009 working baseline). This ensures a seat cannot have more than one active hold or sale under concurrency.
2. **Short, Bounded Critical Path (planned):** The synchronous hold path is a single PostgreSQL transaction (seat state + reservation + outbox record). No latency target is claimed until it is measured by load tests.
3. **Strict Bounded Context Isolation:** Zero cross-module direct database queries or foreign keys. Modules communicate solely through public contract packages (`Contracts`) or published domain/integration events.
4. **Authoritative Source of Truth:** PostgreSQL is the only persistent authority. Any future cache (e.g. Redis) would be ephemeral and never authoritative for business state.
5. **Decoupled Critical Workflows:** Reservation holds and checkout are isolated. Asynchronous side-effects—such as payment authorization, notification dispatch, and real-time seat grid broadcasting—are processed outside the primary reservation hold transaction.

---

## 3. Current Implementation Status

| Phase | Description | Status | Active Tests |
| :--- | :--- | :---: | :---: |
| **Phase 0** | Foundation & Modular Monolith Scaffolding | **Completed** | 1 |
| **Phase 1** | Catalog Domain Foundation & Invariant Boundary | **Completed** | 32 |
| **Phase 2A** | Catalog PostgreSQL Persistence Design | **Approved** | — |
| **Phase 2B.1**| Catalog EF Core Foundation (`CatalogDbContext`, Npgsql, Schema) | **Completed** | 6 (Arch) |
| **Phase 2B.2**| Catalog Entity Mappings & Relational Constraints | **Completed** | 39 (Arch) |
| **Phase 2B.3**| Aggregate-Level Optimistic Concurrency (`CatalogOptimisticConcurrencyInterceptor`) | **Completed** | 9 (Arch) |
| **Phase 2B.4**| Catalog Migrations & PostgreSQL Integration Validation | **Implemented** | 6 (Integ) |
| **Phase 2C** | Catalog Application Layer (11 commands, 3 queries) | **Completed** | 3 (Integ) + unit |
| **Phase 2D** | Catalog Minimal API (14 routes, no authentication yet) | **Implemented** | 4 (HTTP Integ) |
| **Stabilization (Phase 0 of doc 09)** | Build fix, lifecycle invariant fix, typed HTTP error mapping, Testcontainers (digest-pinned), CI, SDK pin | **Completed** | 39 (Unit) |
| *Next* | Roadmap Phases 1–11 in [doc 09 §11](docs/architecture/09-system-audit-and-target-architecture.md#11-phased-implementation-roadmap) | *Planned* | — |

**Verified test results (2026-10-20, clean Release build on .NET 10, `net10.0`):** **212 passed, 0 failed**:
* 112 unit tests
* 63 architecture tests
* 37 integration tests against a disposable PostgreSQL container:
  * 10 Catalog persistence tests
  * 4 Catalog HTTP API tests
  * 11 database-guard tests
  * 12 Reservation HTTP tests, including concurrency and idempotency

### Reservation (seat holds), first slice
* **Endpoints:**
  * `GET /api/events/{eventId}/availability`
  * `POST /api/reservations` (requires an `Idempotency-Key` header)
  * `GET /api/reservations/{id}?customerId=`
  * `POST /api/reservations/{id}/release?customerId=`
* **Guarantees (PostgreSQL, ADR-009):**
  * One row per event seat.
  * All-or-nothing holds via ordered `FOR UPDATE SKIP LOCKED`, so concurrent holds never double-book or deadlock; contended seats return `409 Reservation.SeatsUnavailable` with `unavailableSeatIds`.
  * Database-enforced uniqueness: one active hold per customer per event, and one reservation per idempotency key.
  * Fixed 10-minute holds that expire lazily (expired holds are reclaimable immediately), plus an idempotent release.
* **Not yet implemented:** confirmation/purchase (payments), authentication, a background sweeper, and UI. `customerId` is client-supplied until the Identity module exists.

Per-phase counts above are approximate attributions.

---

## 4. Architecture Overview

```text
                               ┌───────────────────────────────────────────────────────────┐
                               │                    PulseReserve.AppHost                   │
                               │                (ASP.NET Core Web Host)                    │
                               └─────────┬───────────────────┬───────────────────┬─────────┘
                                         │                   │                   │
                     ┌───────────────────▼────────┐ ┌────────▼────────┐ ┌────────▼────────────────┐
                     │          Catalog           │ │   Reservation   │ │       Payments         │
                     │  (Events, Venues, Layouts) │ │ (Holds, Leases) │ │(Checkout, Transactions)│
                     └──────────────┬─────────────┘ └────────┬────────┘ └────────┬───────────────┘
                                    │                        │                   │
                     ┌──────────────▼─────────────┐ ┌────────▼────────┐ ┌────────▼───────────────┐
                     │    PostgreSQL (catalog)    │ │PostgreSQL (res) │ │PostgreSQL (payments)   │
                     └────────────────────────────┘ └────────┬────────┘ └────────────────────────┘
                                                             │
                                                    ┌────────▼────────┐
                                                    │   Redis Cache   │
                                                    │(Admission/Lease)│
                                                    └─────────────────┘
```

### Module Boundary Rules
* **Independent Schemas:** PostgreSQL schemas (`catalog`, `reservation`, `payments`, `identity`) are strictly isolated. Cross-schema relational joins or foreign key constraints are prohibited.
* **Vertical 2-Project Slices:** Every bounded context is partitioned into an internal implementation project (`PulseReserve.Modules.<Module>`) and an immutable public contracts project (`PulseReserve.Modules.<Module>.Contracts`).
* **Contract-Only Dependency:** A module may reference another module's `.Contracts` assembly, but never its internal implementation assembly.
* **Architecture Guardrails:** Automated boundary tests in `PulseReserve.ArchitectureTests` enforce these dependency rules on every build using `NetArchTest.Rules`.

---

## 5. Catalog Bounded Context Architecture

The Catalog bounded context manages event discovery, physical venue topologies, structural seating configurations, and pricing models.

```text
                  ┌──────────────────────┐
                  │        Venue         │  (Physical location, metadata)
                  │   [Aggregate Root]   │
                  └──────────▲───────────┘
                             │
                             │ (VenueId reference)
                             │
                  ┌──────────┴───────────┐
                  │    SeatingLayout     │  (Structural topology, seats, price tiers)
                  │   [Aggregate Root]   │  * Reusable across multiple Events
                  └──────────▲───────────┘
                             │
                             │ (SeatingLayoutId reference)
                             │
                  ┌──────────┴───────────┐
                  │        Event         │  (Event lifecycle: Draft -> Published -> OnSale)
                  │   [Aggregate Root]   │  * Owns EventPrice collection (Event-scoped pricing)
                  └──────────────────────┘
```

### Aggregate Roots & Invariants

1. **`Venue` (`PulseReserve.Modules.Catalog.Domain.Venues.Venue`)**
   * Represents physical locations and structural space boundaries.
   * Invariants ensure a non-empty name and a valid postal address (street, city, country).

2. **`SeatingLayout` (`PulseReserve.Modules.Catalog.Domain.SeatingLayouts.SeatingLayout`)**
   * Reusable structural blueprint consisting of Sections, Rows, Seats, and PriceTier definitions.
   * **Layout Immutability:** A layout is structural and independent of any single event. Once an event referencing this layout transitions to `OnSale`, the layout is formally locked (`IsLocked = true`) preventing any subsequent structural mutations.

3. **`Event` (`PulseReserve.Modules.Catalog.Domain.Events.Event`)**
   * Owns the event lifecycle state machine: `Draft` $\rightarrow$ `Published` $\rightarrow$ `OnSale` $\rightarrow$ `Closed`, with `Cancelled` reachable from any non-terminal state. Publication is allowed only from `Draft`, so an `OnSale` event can never regress to `Published`.
   * **Event-Scoped Pricing:** Seat pricing is an event-level concern, not a layout concern. Multiple events can share the same `SeatingLayout` while establishing distinct ticket prices per `PriceTierId`.
   * **Relational Pricing Integrity:** Database persistence enforces pricing integrity via dual composite foreign keys: `(event_id, seating_layout_id)` against `catalog.events` and `(seating_layout_id, price_tier_id)` against `catalog.price_tiers`, guaranteeing prices cannot reference price tiers outside the event's assigned layout.

---

## 6. Concurrency & Data Consistency Model

### Authoritative Concurrency Control
* **Aggregate Versioning:** Catalog aggregate roots (`Venue`, `SeatingLayout`, `Event`) carry an integer `version` column, mapped as an EF Core **shadow property** concurrency token (it is not a property on `AggregateRoot<TId>`). `CatalogOptimisticConcurrencyInterceptor` advances it exactly once per save, including for child-entity changes.
* **Optimistic Concurrency:** Writes use `WHERE id = @id AND version = @expectedVersion`. Stale writes raise `DbUpdateConcurrencyException`, which the API maps to `409 Conflict`. GET responses do not yet expose the version (planned).

### HTTP Error Mapping
Domain errors carry an explicit `ErrorType`, and `ErrorMapping` maps that type to a status. Error codes are never parsed. Errors are returned as RFC 7807 ProblemDetails (`title` = error code, `detail` = message).

| `ErrorType` | HTTP status | Examples |
| :--- | :---: | :--- |
| `Conflict` | 409 | `Event.AlreadyPublished`, `Event.NotPublished`, `Event.TerminalStatus`, `Event.NotOnSale`, `Event.AlreadyFinished`, `Event.CannotModifyOnSalePricing`, `Event.CannotModifyOnSaleLayout`, `SeatingLayout.Locked` |
| `NotFound` | 404 | `Event.NotFound`, `Venue.NotFound`, `SeatingLayout.NotFound` (the resource addressed by the URL) |
| `Validation` (default) | 400 | Invalid input, including references inside the request body such as `SeatingLayout.ParentSectionNotFound` |

**Intentional behaviour changes (stabilization):**
* Publishing an already-`Published` event used to succeed. It now returns `409 Event.AlreadyPublished`.
* Lifecycle errors that used to return 400 now return 409.
* Request-body reference errors (`ParentSectionNotFound`, `ParentRowNotFound`, `PriceTierNotFound`) that used to return 404 now return 400.

### Reservation Concurrency Strategy (planned, not implemented)
The approved working baseline (ADR-009, [doc 09 §8.3](docs/architecture/09-system-audit-and-target-architecture.md#83-concurrency-controls-and-what-postgresql-guarantees)) uses a PostgreSQL per-event seat inventory (one row per event seat). Holds are atomic conditional updates using ordered `FOR UPDATE SKIP LOCKED`, and database constraints enforce the invariants. **Redis is not required initially.** This supersedes the earlier partial-unique-index + Redis Lua lease design.

---

## 7. Future Roadmap

The authoritative, approved-in-part roadmap is [doc 09 §11](docs/architecture/09-system-audit-and-target-architecture.md#11-phased-implementation-roadmap). Each phase requires explicit approval before it starts.

| Phase | Scope |
| :--- | :--- |
| Phase 1 | Platform essentials: ProblemDetails, health checks, OpenTelemetry, Catalog persistence fixes |
| Phase 2 | Identity, cookie authentication with CSRF protection, authorization, audit |
| Phase 3 | Transactional outbox/inbox with in-process dispatch |
| Phases 4–5 | Catalog sales readiness, then Reservation core on PostgreSQL seat inventory |
| Phases 6–7 | Simulated payments, then ticketing and QR check-in |
| Phases 8–10 | Angular application, SignalR availability, notifications |
| Phase 11 | k6 load and contention verification (the source of any performance claims) |
| Optional | Kafka relay, multi-instance SignalR scale-out, Redis (only on documented triggers) |

The upgrade to .NET 10 LTS (EF Core 10.0.12, Npgsql EF provider 10.0.3) was applied as a separate change after the green .NET 9 baseline. The full build and all test suites were re-verified on `net10.0`.

---

## 8. Testing Strategy

The repository follows a strict testing hierarchy verified through continuous build assertions:

* **Unit Tests (`PulseReserve.UnitTests`, 99):**
  * Covers Catalog domain invariants (`Venue`, `SeatingLayout`, `Event`) and the application handlers, using in-memory fakes.
  * An exhaustive lifecycle transition matrix (`EventLifecycleTransitionTests`) covers every `(state, action)` pair, including the regression that prevents republishing an `OnSale` event.
  * `ErrorMappingTests` covers the typed error → HTTP status mapping. `CatalogConnectionStringTests` checks that configuration always wins over the legacy `PULSERESERVE_TEST_DATABASE` fallback.
* **Architecture Tests (`PulseReserve.ArchitectureTests`, 59):**
  * NetArchTest boundary rules, EF Core model metadata checks, and optimistic-concurrency interceptor tests. These never open a database connection.
* **Integration Tests (`PulseReserve.IntegrationTests`, 14):**
  * Real PostgreSQL tests: optimistic concurrency with competing `DbContext`s, multi-aggregate atomicity, and the end-to-end Catalog lifecycle.
  * HTTP API tests (`CatalogApiHttpTests`, via `WebApplicationFactory`) cover 409/404/400 ProblemDetails responses through the real pipeline, including a guard asserting that the host resolved only the disposable test database.
  * Each test class runs against its **own disposable PostgreSQL container** (Testcontainers, `postgres:17.11-alpine` pinned by digest `sha256:b0f9560a…52b24`), which is migrated from scratch and destroyed afterwards. There is intentionally no fallback to a local or configured database.

Last verified run (2026-10-20, `--configuration Release`, .NET 10):
```text
Passed!  - Failed:     0, Passed:    99, Skipped:     0, Total:    99 - PulseReserve.UnitTests.dll (net10.0)
Passed!  - Failed:     0, Passed:    59, Skipped:     0, Total:    59 - PulseReserve.ArchitectureTests.dll (net10.0)
Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14 - PulseReserve.IntegrationTests.dll (net10.0)
```

---

## 9. Getting Started

### Prerequisites
* [.NET SDK 10.0.401](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned by `global.json`; later 10.0.4xx patches are accepted)
* The projects target `net10.0`; the .NET 10 SDK above includes the required runtime.
* Local Docker development stack (PostgreSQL + API with hot reload + Angular HMR): see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)
* Docker (a running engine is required for the integration tests; no local PostgreSQL installation is needed)
* Git 2.30+

### Build & Verification Commands

```bash
# Clone the repository
git clone <repository-url>
cd PulseReserve

# Restore solution dependencies
dotnet restore

# Build solution with zero warnings/errors
dotnet build --no-restore

# Execute all domain, architectural, and integration tests
# (integration tests start disposable PostgreSQL containers via Docker)
dotnet test
```

CI (`.github/workflows/ci.yml`) runs the same restore, build, and three test suites on every push and pull request.

Running the API locally requires a PostgreSQL connection string in `ConnectionStrings:Catalog`, for example via the `ConnectionStrings__Catalog` environment variable. Never commit credentials.

---

## 10. Architecture Decision Records (ADRs)

Key architectural decisions are documented under [`docs/decisions/`](docs/decisions/):

* [ADR-001: Modular Monolith Architecture](docs/decisions/adr-001-modular-monolith-architecture.md) — Structural strategy, bounded contexts, and boundary isolation.
* [ADR-002: PostgreSQL as Authoritative Source of Truth](docs/decisions/adr-002-postgresql-authoritative-source-of-truth.md) — State authority, transactional safety, and cache invalidation policies.
* [ADR-003: Aggregate Boundaries in Catalog Module](docs/decisions/adr-003-aggregate-boundaries-in-catalog.md) — Aggregate decomposition across `Venue`, `SeatingLayout`, and `Event`.
* [ADR-004: Event-Scoped Pricing Model](docs/decisions/adr-004-event-scoped-pricing.md) — Reusable layouts with event-scoped ticket pricing and relational integrity constraints.
* [ADR-005: Aggregate-Level Optimistic Concurrency Control](docs/decisions/adr-005-aggregate-level-optimistic-concurrency.md) — Version tracking and concurrent update conflict prevention.
* [ADR-006: SeatingLayout Lock Semantics at OnSale](docs/decisions/adr-006-seatinglayout-lock-at-onsale.md) — Decoupling draft publication from structural layout lock enforcement.

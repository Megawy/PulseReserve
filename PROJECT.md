# PulseReserve — Project Specification & Technical Charter

## 1. Project Overview

PulseReserve is an enterprise-grade flash-sale and seat reservation engine designed to withstand extreme traffic spikes, race conditions, and double-booking attempts.

### Core Technology Stack
* **Language & Runtime:** C# 13 / .NET 10 LTS (`net10.0`), built with SDK 10.0.401 pinned in `global.json` (upgraded from .NET 9 per ADR-017 as a separate, separately verified change).
* **Web Host:** ASP.NET Core 10 Minimal APIs / Endpoints
* **Database:** PostgreSQL (with isolated schema per module; only `catalog` exists today)
* **Package Management:** Central Package Management (`Directory.Packages.props`)
* **Architecture Testing:** `NetArchTest.Rules`
* **Integration Testing:** Testcontainers (disposable PostgreSQL per test class)
* **CI:** GitHub Actions (`.github/workflows/ci.yml`)
* **Planned, not implemented:**
  * Transactional outbox with in-process dispatch (ADR-013)
  * ASP.NET Core SignalR
  * OpenTelemetry
  * Cookie-based authentication (ADR-007)
  * Angular product features. A Phase 1 foundation exists in `frontend/pulsereserve-web/`: Angular 22 shell, typed read-only Catalog client, ProblemDetails handling, and a dev proxy. Lint, 43 tests, build, and format all pass.
  * Grafana k6 load testing
* **Optional, adopted only on documented triggers:** Apache Kafka (outbox relay), Redis (SignalR backplane or global admission control). Redis is not required for the reservation guarantees (ADR-009).

---

## 2. Architectural Blueprint & Bounded Contexts

PulseReserve adopts a **Modular Monolith** architecture with a **2-Project Vertical Slice** pattern per bounded context:

```text
PulseReserve.Modules.{Module}            -> Internal domain, features, infrastructure
PulseReserve.Modules.{Module}.Contracts  -> Public DTOs, integration events, contracts
```

### Module Responsibilities
1. **Identity:** Authentication, credentials, JWT issuance, user profile queries.
2. **Catalog:** Venues, sections, seating layouts, event metadata, pricing tiers.
3. **Reservation:** High-throughput seat lease acquisition, hold timeouts, double-booking prevention, reservation records.
4. **Payments:** Explicit checkout handling, transaction logging, gateway integration, refunds.
5. **Notifications:** Transactional email, SMS, and user alert delivery.

---

## 3. Strict Concurrency & Persistence Rules

> These rules describe the **planned** Reservation design. No reservation code exists yet. The approved working baseline is ADR-009 ([doc 09 §8](docs/architecture/09-system-audit-and-target-architecture.md#8-data-and-consistency-design)). It supersedes the earlier partial-unique-index plus Redis Lua lease design.

1. **PostgreSQL as Authoritative Source of Truth:**
   * One event + one seat can have **at most one active hold or sale**.
   * Enforced in PostgreSQL by a per-event seat inventory (one row per event seat) whose state changes only through atomic conditional updates (ordered `FOR UPDATE SKIP LOCKED`), backed by database constraints.
2. **No Redis Initially:**
   * PostgreSQL alone provides the double-booking guarantee. Redis is adopted only on documented triggers (multi-instance SignalR backplane or measured database admission saturation).
   * Holds expire on a fixed, non-renewable TTL. The value is to be confirmed in ADR-010.
3. **Decoupled Asynchronous Path:**
   * The synchronous hold endpoint validates input, then commits one database transaction (seat state + reservation + outbox record) and returns HTTP 201 Created.
   * Outbox processing (in-process first; Kafka optional later), SignalR real-time projection, and Notifications run asynchronously.
4. **Independent Payment State Machine:**
   * Seat holds do not initiate payments. Payment begins solely upon explicit user checkout intent.

---

## 4. Engineering Guardrails

* Modules must never reference internal implementation projects of other modules.
* Cross-module dependencies are restricted to `.Contracts` projects.
* SharedKernel and Contracts must not depend on database or messaging packages (`Npgsql`, `EF Core`, `StackExchange.Redis`, `Confluent.Kafka`).
* Verified automatically via `PulseReserve.ArchitectureTests` on every build.

---

## 5. Implementation Roadmap & Current Status

* **Phase 0 (Foundation & Scaffolding):** COMPLETE.
  * Solution, 16 projects, NetArchTest rules, CI properties, Central Package Management.
* **Phase 1 (Domain-First: SharedKernel & Catalog):** COMPLETE.
  * Pure SharedKernel primitives (`Entity`, `AggregateRoot`, `ValueObject`, `IDomainEvent`, `Result`, `Error`).
  * Catalog domain model (`Venue`, `SeatingLayout`, `Event`, `Section`, `Row`, `Seat`, `PriceTier`, `Money`, `Address`, `SeatCoordinate`).
  * Catalog public contracts (`EventId`, `SeatId`, DTOs, Integration Events).
  * 32 Unit tests covering domain invariants, state transitions, and event-scoped pricing.
  * 23 Architecture tests ensuring boundary isolation.
  * Repository organization & structure pass completed (clean folder/namespace alignment).
* **Phase 2A (Catalog Persistence Design):** APPROVED & COMPLETE (DESIGN ONLY).
  * Comprehensive PostgreSQL `catalog.*` schema specification (`03-catalog-persistence-design.md`).
  * Event-scoped pricing model (`catalog.event_prices`) separating reusable physical layouts from commercial pricing.
  * Dual composite foreign keys enforcing that event prices strictly reference price tiers belonging to the event's assigned layout.
  * Table definitions, UUID mapping, `NUMERIC(18, 4)` money storage, and functional uniqueness indexes.
  * Explicit aggregate-level optimistic concurrency strategy (`version`) and lock race mitigation at `OnSale`.
  * Formally documented via ADR-001 through ADR-006 in `docs/decisions/`.
* **Phase 2B.1 (Catalog EF Core Foundation):** COMPLETE.
  * EF Core 9.0.4 & Npgsql.EntityFrameworkCore.PostgreSQL centrally managed in `Directory.Packages.props`.
  * `CatalogDbContext` established in `PulseReserve.Infrastructure.Persistence.Catalog` with `catalog` default schema.
  * Dependency direction established (`Infrastructure -> Catalog Domain`), preserving domain purity.
  * DI registration extension `AddCatalogPersistence` configured for `ConnectionStrings:Catalog`.
  * Documented in `docs/architecture/04-catalog-ef-core-foundation.md`.
* **Phase 2B.2 (Catalog Entity Mappings & Relational Constraints):** COMPLETE.
  * Explicit `IEntityTypeConfiguration<T>` for all 8 catalog tables (`venues`, `seating_layouts`, `sections`, `rows`, `seats`, `price_tiers`, `events`, `event_prices`).
  * Database-enforced composite foreign keys (`fk_rows_sections_composite`, `fk_seats_rows_composite`, `fk_seats_price_tiers_composite`, `fk_event_prices_events_composite`, `fk_event_prices_price_tiers_composite`).
  * Strongly typed ID value converters via `ConfigureConventions`.
  * Value objects (`Address`, `SeatCoordinate`, `Money`) mapped with `ComplexProperty` (`NUMERIC(18, 4)` and `VARCHAR(3)`).
  * Aggregate-level optimistic concurrency tokens prepared on `venues`, `seating_layouts`, and `events`.
  * Documented in `docs/architecture/05-catalog-ef-core-mappings.md`.
  * 45 Architecture & metadata tests enforcing boundary isolation and relational model integrity.
* **Phase 2B.3 (Aggregate-Level Optimistic Concurrency):** COMPLETE.
  * Infrastructure-level `CatalogOptimisticConcurrencyInterceptor` in `PulseReserve.Infrastructure.Persistence.Catalog.Interceptors`.
  * Pre-save change tracking intercepts child entity mutations (`Section`, `Row`, `Seat`, `PriceTier`, `EventPrice`) and advances the parent Aggregate Root (`SeatingLayout`, `Event`, `Venue`) integer `version` property exactly once.
  * Preserves `OriginalValues["Version"]` for deterministic optimistic concurrency predicate generation (`WHERE id = @id AND version = @expectedVersion`).
  * Enforces aggregate isolation: untracked parent aggregates throw `InvalidOperationException`.
  * Documented in `docs/architecture/06-catalog-aggregate-optimistic-concurrency.md`.
  * 9 dedicated concurrency unit/architecture tests verifying single increment, child propagation, and idempotency.
* **Phase 2B.4 (Catalog Migrations & Database Validation):** COMPLETE.
  * EF Core migration `20261003131943_InitialCatalogCreate` generated with design-time factory `CatalogDbContextFactory`.
  * Verified 8 tables, composite foreign keys, UUID PKs, check constraints, unique indexes, and `NUMERIC(18, 4)` precision in `catalog` schema.
  * Integration test infrastructure in `PulseReserve.IntegrationTests`: `PostgreSqlDatabaseFixture` and 6 multi-connection optimistic concurrency tests (`CatalogOptimisticConcurrencyPostgreSqlTests`).
  * Real PostgreSQL 18 verification passed completely against `pulsereserve_test` (historical, as originally recorded for this phase; that local-database setup has since been removed and was not re-verified).
  * Since the stabilization phase, the fixture runs against a **disposable Testcontainers PostgreSQL 17.11** instance (image pinned by digest). It has no fallback to any local or configured database.
  * Documented in `docs/architecture/07-catalog-migrations-and-postgresql-validation.md`.
* **Phase 2C (Catalog Application Layer / Use-Case Orchestration):** IMPLEMENTED.
  * Vertical-slice application/use-case layer across Venues, SeatingLayouts, and Events (11 commands, 3 queries).
  * Catalog-specific persistence boundaries (`IVenueRepository`, `ISeatingLayoutRepository`, `IEventRepository`, `ICatalogUnitOfWork`).
  * Pure domain and application layers free of EF Core, Npgsql, ASP.NET, Redis, and Kafka.
  * Multi-aggregate orchestration for `PublishEvent` and `StartSale` with atomic PostgreSQL transactions.
  * Documented in `docs/architecture/08-catalog-application-layer.md`.
  * 129 tests passing across solution (60 Unit, 59 Architecture, 10 Integration). This is the Phase 2C scope; it became reproducible only after the stabilization build fix.
* **Phase 2D (Catalog Endpoints & API Layer):** IMPLEMENTED (status-code mapping covered by 4 HTTP-level tests; most success paths are exercised only within those tests).
  * ASP.NET Core Minimal API endpoints and contracts for Catalog: 14 routes under `/api/catalog` (`src/PulseReserve.AppHost/Api/`).
  * No authentication or authorization yet. No global exception handling yet (doc 09 Phase 1–2).
* **Stabilization (doc 09 Phase 0):** COMPLETE.
  * Fixed the AppHost compilation errors: removed unsupported `.WithOpenApi()` calls and resolved the `EventId` ambiguity with a using alias.
  * Fixed the Event lifecycle invariant: `Publish` is allowed only from `Draft`, so an `OnSale` event can no longer regress to `Published`. Covered by an exhaustive `(state, action)` transition matrix.
  * Integration tests isolated on disposable Testcontainers PostgreSQL (`postgres:17.11-alpine`, pinned by digest).
  * Typed HTTP error mapping. Domain errors carry `ErrorType` (Validation/NotFound/Conflict) instead of being classified by substring. Lifecycle conflicts now return 409.
    * Intentional changes: republishing a Published event is now `409 Event.AlreadyPublished` (previously success).
    * Request-body reference errors such as `SeatingLayout.ParentSectionNotFound` now return 400 (previously 404).
  * Connection-string resolution: `ConnectionStrings:Catalog` always wins. `PULSERESERVE_TEST_DATABASE` is only a legacy fallback. HTTP tests assert that the host resolved only the disposable database.
  * SDK pinned (`global.json`). GitHub Actions CI workflow added (not yet run remotely).
  * Verified on 2026-10-20: clean Release build with 0 warnings, and 172 tests passing (99 Unit, 59 Architecture, 14 Integration) on .NET 9.

---

## 6. Executive Status Summary

* **Phase 2A Status:** Approved
* **Phase 2B Status:** Complete (2B.1, 2B.2, 2B.3, 2B.4 Approved & Verified)
* **Phase 2C Status:** Implemented (Catalog Application Layer / Use-Case Orchestration)
* **Phase 2D Status:** Implemented (Catalog Minimal API; no auth; status-code mapping covered by 4 HTTP-level tests in `CatalogApiHttpTests`)
* **Stabilization (doc 09 Phase 0):** Complete
* **Total Tests:** 212 passing (112 Unit, 63 Architecture, 37 Integration on disposable PostgreSQL). Verified 2026-10-20 with a clean Release build on .NET 10 (`net10.0`). Before the Reservation slice, 172 passed on both .NET 9 and .NET 10.
* **Reservation, first slice:** IMPLEMENTED.
  * Module-owned `reservation` schema (ADR-008) with migration `InitialReservationCreate`.
  * Per-event seat inventory, seeded lazily from the immutable OnSale snapshot.
  * Atomic, all-or-nothing seat holds (ADR-009), idempotency keys, one active hold per customer per event, lazy expiry, and idempotent release.
  * Not yet: payments/confirmation, authentication (`customerId` is client-supplied), and UI.
* **.NET 10 Upgrade (ADR-017):** Applied separately after the green .NET 9 baseline: `net10.0`, EF Core 10.0.12, Npgsql EF provider 10.0.3, Mvc.Testing 10.0.12. EF Core reports no pending model changes; the existing migration is unchanged.
* **Architecture Decision Records:** ADR-001 to ADR-006 active in `docs/decisions/`. ADR-007/008/009/013/017 approved as the working baseline in doc 09 §12, not yet written as ADR files. ADR-009 supersedes the reservation mechanism described in ADR-002.
* **Not implemented (as of the real-time work, doc 28):** ticket QR check-in at the gate, Kafka, Redis, OpenTelemetry export. Identity, reservations with purchase, payments (simulator, Stripe, Paymob), notifications (development adapter), the outbox, tickets, the Angular product features and SignalR real-time updates exist; see docs 12–28. This bullet list predates them and is kept only as a pointer.

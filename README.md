# PulseReserve

> High-throughput seat reservation and flash-sale ticketing platform engineered as a **Modular Monolith** using **.NET 9 / ASP.NET Core 9**, **PostgreSQL**, **Redis**, and **Apache Kafka**.

---

## 1. Project Overview

PulseReserve is a high-concurrency event reservation system designed to withstand extreme flash-sale spikes (such as major stadium concerts or championship matches) while providing absolute guarantees against double-booking.

The platform is structured as a **Modular Monolith** with strictly decoupled bounded contexts, schema-per-module database isolation, and clear event-driven asynchronous integration boundaries.

### Technology Stack
* **Runtime & Framework:** .NET 9, ASP.NET Core 9, C# 13
* **Authoritative Persistence:** PostgreSQL 16+ (isolated schemas: `catalog`, `reservation`, `payments`, `identity`)
* **ORM & Querying:** EF Core 9 (writes with optimistic concurrency) / Dapper (read models & queries)
* **High-Throughput Acceleration:** Redis (ephemeral admission gate & seat-lease hold cache)
* **Asynchronous Integration:** Apache Kafka + Transactional Outbox pattern
* **Real-Time Projection:** ASP.NET Core SignalR
* **Observability:** OpenTelemetry (distributed tracing, metrics, structured logs)
* **Testing:** xUnit, FluentAssertions, NetArchTest (architectural boundary guardrails)

---

## 2. Core Engineering Goals

1. **Zero Double-Booking Guarantee:** Authoritative database enforcement via PostgreSQL partial unique constraints combined with transactional isolation, ensuring a seat cannot have more than one active reservation under any concurrency condition.
2. **Sub-100ms Critical Path:** The synchronous reservation path is minimized to Redis admission verification, ephemeral seat hold acquisition, and staging an Outbox record within a single PostgreSQL ACID transaction.
3. **Strict Bounded Context Isolation:** Zero cross-module direct database queries or foreign keys. Modules communicate solely through public contract packages (`Contracts`) or published domain/integration events.
4. **Authoritative Source of Truth:** PostgreSQL is the only persistent authority. Redis serves purely as an ephemeral admission filter and performance accelerator; cache invalidation or complete Redis cold restarts never corrupt business state.
5. **Decoupled Critical Workflows:** Reservation holds and checkout are isolated. Asynchronous side-effects—such as payment authorization, notification dispatch, and real-time seat grid broadcasting—are processed outside the primary reservation hold transaction.

---

## 3. Current Implementation Status

| Phase | Description | Status | Active Tests |
| :--- | :--- | :---: | :---: |
| **Phase 0** | Foundation & Modular Monolith Scaffolding | **Completed** | 1 |
| **Phase 1** | Catalog Domain Foundation & Invariant Boundary | **Completed** | 32 |
| **Phase 2A** | Catalog PostgreSQL Persistence Design | **Approved** | 23 (Arch) |
| **Phase 2B** | Catalog Persistence Implementation (EF Core & Migrations) | **Ready to Start** | — |
| **Phase 3** | Reservation Core, Concurrency & Redis Seat-Lease | *Planned* | — |
| **Phase 4** | Payments Integration & Checkout Workflow | *Planned* | — |
| **Phase 5** | Asynchronous Messaging & Kafka Outbox Pipeline | *Planned* | — |
| **Phase 6** | Observability (OpenTelemetry) & SignalR Live Projections | *Planned* | — |
| **Phase 7** | Flash Sale Hardening & Distributed Stress Testing | *Planned* | — |

**Total Active Tests:** **56 passed** (32 Unit tests, 23 Architecture guardrail tests, 1 Integration scaffold test).

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
   * Invariants ensure valid venue metadata, geographical coordinates, and timezone consistency.

2. **`SeatingLayout` (`PulseReserve.Modules.Catalog.Domain.SeatingLayouts.SeatingLayout`)**
   * Reusable structural blueprint consisting of Sections, Rows, Seats, and PriceTier definitions.
   * **Layout Immutability:** A layout is structural and independent of any single event. Once an event referencing this layout transitions to `OnSale`, the layout is formally locked (`IsLocked = true`) preventing any subsequent structural mutations.

3. **`Event` (`PulseReserve.Modules.Catalog.Domain.Events.Event`)**
   * Owns the event lifecycle state machine: `Draft` $\rightarrow$ `Published` $\rightarrow$ `OnSale` $\rightarrow$ `Completed` / `Cancelled`.
   * **Event-Scoped Pricing:** Seat pricing is an event-level concern, not a layout concern. Multiple events can share the same `SeatingLayout` while establishing distinct ticket prices per `PriceTierId`.
   * **Relational Pricing Integrity:** Database persistence enforces pricing integrity via dual composite foreign keys: `(event_id, seating_layout_id)` against `catalog.events` and `(seating_layout_id, price_tier_id)` against `catalog.price_tiers`, guaranteeing prices cannot reference price tiers outside the event's assigned layout.

---

## 6. Concurrency & Data Consistency Model

### Authoritative Concurrency Control
* **Aggregate Versioning:** All aggregate roots derive from `AggregateRoot<TId>` and maintain a monotonic `int Version` incremented on domain modifications.
* **Optimistic Concurrency:** State changes in persistence verify expected versions at write time (`ExpectedVersion == PersistedVersion`), failing fast with concurrency conflict exceptions on stale writes.

### Reservation Concurrency Strategy (Phase 3 Preview)
* **Database Level:** PostgreSQL enforces a partial unique index:
  ```sql
  CREATE UNIQUE INDEX uq_reservations_event_seat_active
  ON reservation.reservations (event_id, seat_id)
  WHERE status IN ('Held', 'Confirmed');
  ```
  This guarantees database-level safety even under high concurrency or edge-case bypasses.
* **Redis Admission & Hold Lease:** In high-volume flash-sales, incoming seat-hold requests acquire short-lived distributed leases via atomic Redis Lua scripts (`res:hold:{eventId}:{seatId}`), preventing database connection exhaustion and mitigating lock contention.

---

## 7. Future Roadmap

* **Phase 2B (Current Next Step):** Implement PostgreSQL persistence, EF Core configurations, dual composite relational integrity constraints, and test migrations for the Catalog context.
* **Phase 3:** Reservation aggregate, distributed seat locking with Redis Lua admission filtering, hold expiration management, and partial unique constraint enforcement.
* **Phase 4:** Payments integration using idempotent payment requests, webhook ingestion, and state reconciliation.
* **Phase 5:** Kafka event publishing pipeline using the Transactional Outbox and Idempotent Consumer patterns.
* **Phase 6:** SignalR real-time seat status broadcasting and OpenTelemetry distributed tracing with Prometheus & Jaeger/Aspire dashboard.
* **Phase 7:** Automated stress testing, JMeter / k6 concurrency benchmarks, and flash-sale load hardening.

---

## 8. Testing Strategy

The repository follows a strict testing hierarchy verified through continuous build assertions:

* **Unit Tests (`PulseReserve.UnitTests`):**
  * 32 comprehensive tests verifying Catalog domain invariants (`Venue`, `SeatingLayout`, `Event`).
  * Validates lifecycle transitions (`Draft` $\rightarrow$ `Published` $\rightarrow$ `OnSale`), price completeness, structural layout validation, and error semantics.
* **Architecture Tests (`PulseReserve.ArchitectureTests`):**
  * 23 automated rules leveraging `NetArchTest`.
  * Verifies module isolation, forbidding cross-module assembly references outside `.Contracts`, and ensuring clean domain/infrastructure boundaries.
* **Integration Tests (`PulseReserve.IntegrationTests`):**
  * Infrastructure and host composition test harness ready for Testcontainers-based PostgreSQL and Redis verification.

```text
Passed!  - Failed:     0, Passed:    56, Skipped:     0, Total:    56, Duration: 442 ms
```

---

## 9. Getting Started

### Prerequisites
* [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (C# 13 support)
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
dotnet test
```

---

## 10. Architecture Decision Records (ADRs)

Key architectural decisions are documented under [`docs/decisions/`](docs/decisions/):

* [ADR-001: Modular Monolith Architecture](docs/decisions/adr-001-modular-monolith-architecture.md) — Structural strategy, bounded contexts, and boundary isolation.
* [ADR-002: PostgreSQL as Authoritative Source of Truth](docs/decisions/adr-002-postgresql-authoritative-source-of-truth.md) — State authority, transactional safety, and cache invalidation policies.
* [ADR-003: Aggregate Boundaries in Catalog Module](docs/decisions/adr-003-aggregate-boundaries-in-catalog.md) — Aggregate decomposition across `Venue`, `SeatingLayout`, and `Event`.
* [ADR-004: Event-Scoped Pricing Model](docs/decisions/adr-004-event-scoped-pricing.md) — Reusable layouts with event-scoped ticket pricing and relational integrity constraints.
* [ADR-005: Aggregate-Level Optimistic Concurrency Control](docs/decisions/adr-005-aggregate-level-optimistic-concurrency.md) — Version tracking and concurrent update conflict prevention.
* [ADR-006: SeatingLayout Lock Semantics at OnSale](docs/decisions/adr-006-seatinglayout-lock-at-onsale.md) — Decoupling draft publication from structural layout lock enforcement.

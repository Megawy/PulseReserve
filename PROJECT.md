# PulseReserve — Project Specification & Technical Charter

## 1. Project Overview

PulseReserve is an enterprise-grade flash-sale and seat reservation engine designed to withstand extreme traffic spikes, race conditions, and double-booking attempts.

### Core Technology Stack
* **Language & Runtime:** C# 13 / .NET 9 (`net9.0`)
* **Web Host:** ASP.NET Core 9 Minimal APIs / Endpoints
* **Database:** PostgreSQL (with isolated schema per module)
* **High-Speed Cache & Lease:** Redis 7+
* **Event Streaming:** Apache Kafka (KRaft mode)
* **Real-Time Projection:** ASP.NET Core SignalR
* **Package Management:** Central Package Management (`Directory.Packages.props`)
* **Architecture Testing:** `NetArchTest.Rules`
* **Performance Testing:** Grafana k6 (planned Phase 3)

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

1. **PostgreSQL as Authoritative Source of Truth:**
   * One event + one seat can have **at most one active reservation** (`Held` or `Confirmed`).
   * Enforced independently in PostgreSQL via partial unique index:
     ```sql
     CREATE UNIQUE INDEX uq_active_seat_reservation 
     ON reservation.seat_reservations (event_id, seat_id) 
     WHERE status IN ('Held', 'Confirmed');
     ```
2. **Redis Admission & Seat Lease:**
   * Key: `res:hold:{eventId}:{seatId}`
   * Value: Cryptographically random hold token.
   * TTL: 600 seconds (10 minutes).
   * Renewal: Strictly forbidden.
   * Release: Token-guarded atomic Lua script.
3. **Decoupled Asynchronous Path:**
   * The synchronous hold endpoint validates input, checks/leases in Redis, commits the database transaction (inserting hold + outbox record), and returns HTTP 201 Created.
   * Outbox relay, Kafka event distribution, SignalR real-time projection, and Notifications run asynchronously.
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
* **Phase 2B (Catalog EF Core Implementation & Migrations):** READY TO START (NEXT).
  * PostgreSQL `catalog.*` schema migrations via EF Core.
  * `CatalogDbContext` and entity type configurations.
* **Phase 2C (Reservation Core & Redis Leases):** PLANNED.
  * Reservation bounded context domain model and Redis atomic lease coordination.

---

## 6. Executive Status Summary

* **Phase 2A Status:** Approved
* **Phase 2B Status:** Ready to Start
* **Active Tests:** 56 total (32 Unit, 23 Architecture, 1 Integration) — 100% Passing
* **Architecture Decision Records:** ADR-001 to ADR-006 active in `docs/decisions/`

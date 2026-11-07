# Architecture Document 04: Catalog EF Core Foundation Specification

**Bounded Context:** Catalog  
**Phase:** 2B.1 (EF Core Foundation)  
**Status:** Implemented  
**Date:** October 2026  

---

## 1. Where Catalog Persistence Lives

Catalog persistence infrastructure resides strictly within:

```text
src/Shared/PulseReserve.Infrastructure/
└── Persistence/
    └── Catalog/
        ├── CatalogDbContext.cs
        └── CatalogPersistenceExtensions.cs
```

### Component Details:
* **`CatalogDbContext`:** Inherits from `Microsoft.EntityFrameworkCore.DbContext`. It establishes the EF Core boundary and sets the PostgreSQL model default schema to `"catalog"`.
* **`CatalogPersistenceExtensions`:** Provides clean service collection registration (`AddCatalogPersistence`) connecting `CatalogDbContext` to the Npgsql PostgreSQL provider using the named connection string key `ConnectionStrings:Catalog`.

---

## 2. Why the Domain Project Does Not Reference EF Core

`PulseReserve.Modules.Catalog` remains completely persistence-ignorant (Pure Domain):

* **Dependency Inversion:** Domain entities (`Venue`, `SeatingLayout`, `Event`), Value Objects (`Money`, `Address`, `SeatCoordinate`), and domain events must not take dependencies on database drivers, ORM abstractions, or relational metadata attributes (`[Table]`, `[Key]`, etc.).
* **Longevity & Testability:** Pure domain logic can be tested in-memory in milliseconds without mock databases or EF Core in-memory providers.
* **Separation of Concerns:** Relational schemas and ORM mapping concerns change independently of core business rules and invariants.
* **Architecture Guardrails:** NetArchTest rules in `PulseReserve.ArchitectureTests` strictly forbid any reference from `PulseReserve.Modules.Catalog` to `Microsoft.EntityFrameworkCore`, `Npgsql`, or `PulseReserve.Infrastructure`.

---

## 3. Why PostgreSQL is the Authoritative Store

In PulseReserve, PostgreSQL is the single authoritative source of truth:

* **Transactional ACID Guarantees:** State transitions, seat locks, and reservation records require durable, transactional atomicity that only an ACID relational database guarantees.
* **Relational Referential Integrity:** Cross-table invariants (such as dual composite foreign keys guaranteeing that event prices reference only price tiers belonging to the event's seating layout) are enforced at the database level.
* **Ephemeral Role of Redis:** Redis serves solely as an admission gate and ephemeral lease cache. If Redis experiences a cold start or cache invalidation, authoritative business state is safely preserved in and rebuilt from PostgreSQL.

---

## 4. Why the `catalog` Schema is Isolated

PostgreSQL schema isolation (`catalog` schema) enforces the Modular Monolith boundary at the database level:

* **No Cross-Schema Foreign Keys:** No foreign key or relational join is permitted across bounded context schemas (`catalog`, `reservation`, `payments`, `identity`).
* **Microservice Extraction Path:** If the Catalog module is extracted into an independent microservice in the future, the `catalog` schema can be moved to an isolated database instance with zero schema refactoring.
* **Migration Autonomy:** Migration history (`__EFMigrationsHistory`) is scoped to the `catalog` schema, ensuring independent evolutionary schema management per module.

---

## 5. What Phase 2B.1 Intentionally Does NOT Implement

To maintain strict incremental discipline, this phase deliberately excludes:

* **Entity Mappings (`IEntityTypeConfiguration<T>`):** Detailed relational column mappings, value conversions, and composite foreign keys are deferred to Phase 2B.2.
* **`DbSet<T>` Entity Properties:** Complex aggregate roots (`Venue`, `SeatingLayout`, `Event`) use private constructors and DDD value objects. Exposing `DbSet<T>` without their full Phase 2B.2 configurations would trigger invalid EF Core convention inference.
* **EF Core Migrations & SQL Scripts:** No migration files or raw DDL scripts are generated in this phase.
* **Repositories & Unit of Work:** Application query/command handlers and repositories are deferred until entity mapping is verified.
* **Design-Time Factory:** `IDesignTimeDbContextFactory<CatalogDbContext>` is intentionally omitted until the migration workflow is introduced.
* **Other Module Persistence:** No persistence infrastructure for Reservation, Identity, Payments, or Notifications.

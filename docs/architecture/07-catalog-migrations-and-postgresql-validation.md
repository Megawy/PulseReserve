# Architecture Document 07: Catalog Migrations & PostgreSQL Concurrency Validation

**Bounded Context:** Catalog  
**Phase:** 2B.4 (Catalog Migrations & Real PostgreSQL Validation)  
**Status:** Implemented & Documented  

---

## 1. Database Setup & Architecture Target

PulseReserve targets PostgreSQL 16+ / 18 as its authoritative persistence engine.
* **Database Name (Development):** `pulsereserve`
* **Database Name (Testing):** `pulsereserve_test`
* **Dedicated Schema:** `catalog`
* **Migrations History Table:** `catalog.__EFMigrationsHistory`

By isolating the Catalog bounded context into the dedicated `catalog` PostgreSQL schema, PulseReserve guarantees schema-level autonomy within the modular monolith, preparing the system for modular database isolation without cross-schema foreign key leakage.

---

## 2. Migration Creation

The initial EF Core migration for Catalog persistence was generated using design-time tooling:

```bash
dotnet ef migrations add InitialCatalogCreate \
  --project src/Shared/PulseReserve.Infrastructure \
  --startup-project src/PulseReserve.AppHost \
  --output-dir Persistence/Catalog/Migrations \
  --context CatalogDbContext
```

### Relational Schema Blueprint
The generated migration (`20261003131943_InitialCatalogCreate.cs`) contains all 8 catalog tables and structural constraints:
1. **`catalog.venues`**:
   - Primary Key: `id` (`UUID`)
   - Columns: `name`, `created_at_utc`, `updated_at_utc`, `version` (`INTEGER NOT NULL DEFAULT 1`)
   - Complex Value Object: `address_street`, `address_city`, `address_country`, `address_postal_code` flattened directly into the table.
2. **`catalog.seating_layouts`**:
   - Primary Key: `id` (`UUID`)
   - Foreign Key: `fk_seating_layouts_venues` $\rightarrow$ `catalog.venues(id)` (`ON DELETE RESTRICT`)
   - Columns: `name`, `is_locked` (`BOOLEAN DEFAULT FALSE`), `created_at_utc`, `updated_at_utc`, `version` (`INTEGER NOT NULL DEFAULT 1`)
3. **`catalog.sections`**:
   - Primary Key: `id` (`UUID`)
   - Alternate Key: `uq_sections_layout_id` on `(seating_layout_id, id)`
   - Unique Index: `uq_sections_layout_name_lower` on `(seating_layout_id, name)`
   - Foreign Key: `fk_sections_seating_layouts` $\rightarrow$ `catalog.seating_layouts(id)` (`ON DELETE CASCADE`)
4. **`catalog.rows`**:
   - Primary Key: `id` (`UUID`)
   - Alternate Key: `uq_rows_layout_section_id` on `(seating_layout_id, section_id, id)`
   - Unique Index: `uq_rows_section_label_lower` on `(section_id, label)`
   - Composite Foreign Key: `fk_rows_sections_composite` $\rightarrow$ `catalog.sections(seating_layout_id, id)` (`ON DELETE CASCADE`)
5. **`catalog.price_tiers`**:
   - Primary Key: `id` (`UUID`)
   - Alternate Key: `uq_price_tiers_layout_id` on `(seating_layout_id, id)`
   - Check Constraint: `chk_price_tiers_name_non_empty` (`length(trim(name)) > 0`)
   - Foreign Key: `fk_price_tiers_seating_layouts` $\rightarrow$ `catalog.seating_layouts(id)` (`ON DELETE CASCADE`)
6. **`catalog.seats`**:
   - Primary Key: `id` (`UUID`)
   - Coordinates: `coordinate_x`, `coordinate_y`
   - Check Constraint: `chk_seats_coordinates` (`coordinate_x >= 0 AND coordinate_y >= 0`)
   - Unique Index: `uq_seats_row_label_lower` on `(row_id, label)`
   - Composite Foreign Key 1: `fk_seats_rows_composite` $\rightarrow$ `catalog.rows(seating_layout_id, section_id, id)` (`ON DELETE CASCADE`)
   - Composite Foreign Key 2: `fk_seats_price_tiers_composite` $\rightarrow$ `catalog.price_tiers(seating_layout_id, id)` (`ON DELETE RESTRICT`)
7. **`catalog.events`**:
   - Primary Key: `id` (`UUID`)
   - Alternate Key: `uq_events_id_layout` on `(id, seating_layout_id)`
   - Check Constraints:
     - `chk_events_chronological`: `scheduled_start_utc < scheduled_end_utc`
     - `chk_events_status_valid`: `status IN ('Draft', 'Published', 'OnSale', 'Closed', 'Cancelled')`
     - `chk_events_published_layout`: `status = 'Draft' OR seating_layout_id IS NOT NULL`
   - Foreign Keys:
     - `fk_events_venues` $\rightarrow$ `catalog.venues(id)` (`ON DELETE RESTRICT`)
     - `fk_events_seating_layouts` $\rightarrow$ `catalog.seating_layouts(id)` (`ON DELETE RESTRICT`)
8. **`catalog.event_prices`**:
   - Composite Primary Key: `pk_event_prices` on `(event_id, price_tier_id)`
   - Money Storage: `price_amount NUMERIC(18, 4) NOT NULL`, `price_currency VARCHAR(3) NOT NULL`
   - Check Constraints:
     - `chk_event_prices_amount_non_negative`: `price_amount >= 0`
     - `chk_event_prices_currency_iso`: `length(price_currency) = 3 AND price_currency = upper(price_currency)`
   - Composite Foreign Key 1: `fk_event_prices_events_composite` $\rightarrow$ `catalog.events(id, seating_layout_id)` (`ON DELETE CASCADE`)
   - Composite Foreign Key 2: `fk_event_prices_price_tiers_composite` $\rightarrow$ `catalog.price_tiers(seating_layout_id, id)` (`ON DELETE CASCADE`)

---

## 3. Migration Application Strategy

Migrations are executed via two distinct deployment pathways:
1. **Automated CI/CD Deployment:**  
   Idempotent SQL script generation:
   ```bash
   dotnet ef migrations script --idempotent --context CatalogDbContext --output catalog-migration.sql
   ```
   Applied via database pipeline before application startup.
2. **Integration Test Execution:**  
   Automatic schema migration during test initialization via `PostgreSqlDatabaseFixture`:
   ```csharp
   await context.Database.MigrateAsync();
   ```

---

## 4. Actual PostgreSQL Schema Verification

Inspection of the compiled PostgreSQL DDL verifies complete fidelity to the Phase 2A persistence design:
* **Schema Integrity:** Dedicated `catalog` schema.
* **Precision:** `price_amount` is mapped as `numeric(18, 4)` (preserving exact financial computation, strictly avoiding floating-point or truncated scale).
* **Composite Foreign Keys:** Dual composite foreign keys on `event_prices` guarantee that ticket prices can only reference price tiers belonging to the exact seating layout assigned to the event.
* **Optimistic Concurrency:** Explicit integer `version` columns configured on all aggregate roots (`catalog.venues`, `catalog.seating_layouts`, `catalog.events`).

---

## 5. Concurrency Test Strategy

The real PostgreSQL concurrency test suite resides in:
[`tests/PulseReserve.IntegrationTests/Catalog/CatalogOptimisticConcurrencyPostgreSqlTests.cs`](file:///F:/Course/github/PulseReserve/tests/PulseReserve.IntegrationTests/Catalog/CatalogOptimisticConcurrencyPostgreSqlTests.cs)

### Six Authoritative Test Scenarios
1. **Test A (SeatingLayout Stale Update):**
   - Context A and Context B load layout version 1 concurrently.
   - Context A modifies Seat A $\rightarrow$ saves $\rightarrow$ root version becomes 2.
   - Context B modifies Seat B $\rightarrow$ attempts save with stale expected version 1 $\rightarrow$ **throws `DbUpdateConcurrencyException`**.
   - Database verified to contain version 2 with only Context A's modification preserved.
2. **Test B (Event / EventPrice Stale Update):**
   - Context A and Context B load Event version 1 concurrently.
   - Context A modifies `EventPrice` $\rightarrow$ saves $\rightarrow$ root version becomes 2.
   - Context B modifies `EventPrice` $\rightarrow$ attempts save with stale expected version 1 $\rightarrow$ **throws `DbUpdateConcurrencyException`**.
3. **Test C (Direct Root Concurrency):**
   - Direct root mutation (e.g. `LockLayout()`) conflicts with concurrent rename, throwing `DbUpdateConcurrencyException`.
4. **Test D (Multiple Children Same Aggregate):**
   - Modifying 5 seats, 2 rows, and 1 price tier in a single `SaveChanges` call increments root version **exactly once** ($1 \rightarrow 2$, not $1 \rightarrow 9$).
5. **Test E (Root + Child Same Save):**
   - Mutating `LockLayout()` and a child seat concurrently in the same `SaveChanges` increments version **exactly once** ($1 \rightarrow 2$, not $1 \rightarrow 3$).
6. **Test F (Multiple Aggregates Independent):**
   - Mutating SeatingLayout A and Event B in one context advances both independently ($10 \rightarrow 11$ and $20 \rightarrow 21$).

---

## 6. Why Separate DbContexts Are Required

A single `DbContext` instance caches entity instances in its internal `IdentityMap`. If two operations use the same context:
- State changes overwrite in-memory instances before saving.
- Concurrency tokens are evaluated against local in-memory state rather than simulating two distinct concurrent actors (e.g. two separate web requests or background jobs).
Using **two distinct `DbContext` instances** (with separate connections) genuinely simulates competing database transactions attempting to write to PostgreSQL concurrently.

---

## 7. Why InMemory / SQLite Are Insufficient

1. **EF Core InMemory:**
   - Does NOT support relational transactions, foreign key constraints, composite foreign keys, or database check constraints.
   - Does not execute SQL `UPDATE ... WHERE version = @expectedVersion`.
2. **SQLite:**
   - Does not support schema qualification (`catalog.venues`).
   - Handles concurrency via file-level locking (`database is locked`), obscuring optimistic concurrency semantics.
   - Does not validate PostgreSQL-specific constraints or data types.

---

## 8. Expected Concurrency Behavior & SQL Verification

When `CatalogOptimisticConcurrencyInterceptor` executes, EF Core generates:
```sql
UPDATE catalog.seating_layouts
SET version = @newVersion, updated_at_utc = @now
WHERE id = @id AND version = @expectedVersion;
```
If 0 rows are affected (because another transaction committed `version = @newVersion` first), EF Core's relational update pipeline raises `DbUpdateConcurrencyException`.

---

## 9. Test Configuration & Environment Prerequisites

> **Updated in the stabilization phase (doc 09, Phase 0).** The original local-database setup (`PULSERESERVE_TEST_DATABASE` / `localhost:5432`) has been removed. The fixture migrates and truncates tables, so it must never point at an existing database.

The integration tests run against an **isolated, disposable PostgreSQL** started by Testcontainers:
1. `PostgreSqlDatabaseFixture` starts a fresh `postgres:17.11-alpine` container (pinned by digest `sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24`) per test class on a random host port, applies the EF Core migrations, and destroys the container when the class finishes.
2. There is **no** fallback to a local, configured, or environment-variable database. The only prerequisite is a running Docker engine.
3. Run the integration test suite:
   ```bash
   dotnet test tests/PulseReserve.IntegrationTests/PulseReserve.IntegrationTests.csproj
   ```
If Docker is unavailable, the fixture fails immediately with a `[Docker Prerequisite]` diagnostic instead of silently skipping tests.

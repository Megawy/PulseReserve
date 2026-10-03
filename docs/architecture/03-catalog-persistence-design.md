# Architecture Document 03: Catalog Persistence Design Specification

**Bounded Context:** Catalog  
**Phase:** 2A (Design Only)  
**Target Persistence Engine:** PostgreSQL 16+ / 17  
**Schema:** `catalog`  
**Status:** Under Architectural Review  

---

## 1. Purpose

This document defines the authoritative relational persistence design for the **Catalog** bounded context of PulseReserve. It translates the domain model into a robust PostgreSQL schema while establishing airtight database-level integrity, explicit aggregate-level concurrency control, and clean separation between reusable structural layout metadata and commercial, event-specific pricing.

---

## 2. Scope

### In Scope:
* Relational schema definition for the `catalog` PostgreSQL schema.
* Database-enforced hierarchical referential integrity across Venue, SeatingLayout, Section, Row, Seat, and PriceTier.
* Implementation of the approved **Event-Scoped Pricing** architectural model (`catalog.event_prices`).
* Aggregate-level optimistic concurrency control strategy for `SeatingLayout` and `Event`.
* Storage strategies for strongly typed IDs, Value Objects (`Money`, `Address`, `SeatCoordinate`, `EventPrice`), and enums.
* Specification of keys, constraints, delete behaviors, and index access patterns.
* Design-only blueprints for future EF Core mappings and migration execution.
* Explicit identification of remaining open domain decisions.

### Strictly Out of Scope:
* Any C# persistence implementation (`DbContext`, mappings, repositories).
* Generating or executing SQL migration files.
* Introducing NuGet packages or database drivers.
* Runtime reservation concerns (seat availability, holds, lease tokens, booking states).

---

## 3. Current Domain Model

The persistence design is directly anchored to the domain implementation in `PulseReserve.Modules.Catalog`:

### 3.1 Aggregates and Entities
* **`Venue` (`AggregateRoot<VenueId>`):**
  * Identity: `VenueId` (`Guid`)
  * State: `Name` (`string`), `Address` (`ValueObject`: `Street`, `City`, `Country`, `PostalCode`).
* **`SeatingLayout` (`AggregateRoot<SeatingLayoutId>`):**
  * Identity: `SeatingLayoutId` (`Guid`)
  * State: `VenueId` (`Guid`), `Name` (`string`), `IsLocked` (`bool`).
  * Owned Collections: `Sections` (`Section`), `Rows` (`Row`), `Seats` (`Seat`), `PriceTiers` (`PriceTier`).
* **`Section` (`Entity<SectionId>`):**
  * Identity: `SectionId` (`Guid`)
  * State: `Name` (`string`), `DisplayOrder` (`int`).
* **`Row` (`Entity<RowId>`):**
  * Identity: `RowId` (`Guid`)
  * State: `SectionId` (`Guid`), `Label` (`string`), `DisplayOrder` (`int`).
* **`Seat` (`Entity<SeatId>`):**
  * Identity: `SeatId` (`Guid`)
  * State: `RowId` (`Guid`), `SectionId` (`Guid`), `Label` (`string`), `Coordinate` (`SeatCoordinate`: `X`, `Y`), `PriceTierId` (`Guid`).
* **`PriceTier` (`Entity<PriceTierId>`):**
  * Identity: `PriceTierId` (`Guid`)
  * State: `Name` (`string`). Represents the structural seat tier classification (e.g. "VIP", "Regular", "Balcony"). Does **not** hold monetary pricing.
* **`Event` (`AggregateRoot<EventId>`):**
  * Identity: `EventId` (`Guid`)
  * State: `Name` (`string`), `Description` (`string`), `VenueId` (`Guid`), `SeatingLayoutId?` (`Guid?`), `ScheduledStartUtc` (`DateTime`), `ScheduledEndUtc` (`DateTime`), `Status` (`EventStatus`), `PublishedAtUtc?`, `OnSaleAtUtc?`, `CancelledAtUtc?`, `CancellationReason?`.
  * Owned Pricing: `Prices` (`IReadOnlyCollection<EventPrice>`: `PriceTierId`, `Money Price`).

---

## 4. Aggregate Boundaries & Separation of Structure vs Pricing

The architecture maintains strict boundaries between in-memory aggregates and database persistence:

```text
Domain Boundary (Aggregates)                         Database Boundary (Relational Tables)
────────────────────────────                         ───────────────────────────────────────
Venue AggregateRoot                                  catalog.venues
  - Owns venue identity & address                      - PK: id
  - Boundary: Facility metadata                        - No child tables

SeatingLayout AggregateRoot                          catalog.seating_layouts
  - Owns reusable structural seating hierarchy:        - PK: id
    Sections, Rows, Seats, PriceTiers (Categories)     - Owns child tables via CASCADE:
  - Boundary: Physical seating structure                 catalog.sections
  - Structural mutations strictly via layout root        catalog.rows
  - Zero price amounts; reusable across events           catalog.seats
                                                         catalog.price_tiers (Structural Categories)

Event AggregateRoot                                  catalog.events
  - Owns event lifecycle & schedule                    - PK: id
  - References VenueId (by ID only)                    - FK -> catalog.venues(id) [RESTRICT]
  - References SeatingLayoutId (by ID only)            - FK -> catalog.seating_layouts(id) [RESTRICT]
  - Owns commercial event pricing:                     - Owns child pricing table via CASCADE:
    Prices (EventPrice: PriceTierId, Money Price)        catalog.event_prices (event_id, price_tier_id)
  - Does NOT own or mutate SeatingLayout
```

### Invariants:
1. **No Cross-Aggregate Navigation Properties:** The `Event` aggregate does not reference or contain `SeatingLayout` or `Venue` instances. Database foreign keys represent persistence integrity, not aggregate composition.
2. **Mutations Through Root Only:** Sections, Rows, Seats, and structural PriceTiers cannot be mutated independently outside the `SeatingLayout` aggregate boundary.
3. **Event-Scoped Pricing:** Commercial ticket prices belong strictly to `Event` (`catalog.event_prices`), allowing identical structural layouts to be priced differently across events.

---

## 5. PostgreSQL Schema Definition

All tables reside in the dedicated `catalog` schema:

```text
                               ┌─────────────────────────┐
                               │     catalog.venues      │
                               │ ─────────────────────── │
                               │ PK: id (UUID)           │
                               └────────────┬────────────┘
                                            │
                     ┌──────────────────────┴──────────────────────┐
                     │ 1:N (ON DELETE RESTRICT)                    │ 1:N (ON DELETE RESTRICT)
                     ▼                                             ▼
       ┌───────────────────────────┐                 ┌───────────────────────────┐
       │      catalog.events       │                 │  catalog.seating_layouts  │
       │ ───────────────────────── │                 │ ───────────────────────── │
       │ PK: id (UUID)             │                 │ PK: id (UUID)             │
       │ FK: venue_id              │                 │ FK: venue_id              │
       │ FK: seating_layout_id ────┼────────┐        └─────────────┬─────────────┘
       └─────────────┬─────────────┘        │                      │
                     │ 1:N (CASCADE)        │                      │ 1:N (CASCADE)
                     ▼                      │        ┌─────────────┼─────────────┐
       ┌───────────────────────────┐        │        ▼             ▼             ▼
       │   catalog.event_prices    │        │  ┌───────────┐ ┌───────────┐ ┌───────────┐
       │ ───────────────────────── │        │  │ sections  │ │price_tiers│ │   rows    │
       │ PK: (event_id, tier_id)   │        │  └─────┬─────┘ └─────┬─────┘ └─────┬─────┘
       │ price_amount, currency    │        │        │ 1:N         │             │ 1:N
       └───────────────────────────┘        │        └───────┐     │     ┌───────┘
                                            │                ▼     ▼     ▼
                                            │             ┌─────────────────┐
                                            └────────────►│  catalog.seats  │
                                              (via layout)│ ─────────────── │
                                                          │ PK: id (UUID)   │
                                                          └─────────────────┘
```

### Table 1: `catalog.venues`
Represents physical facilities.
* **Columns:**
  * `id` (`UUID`, `NOT NULL`, Primary Key)
  * `name` (`VARCHAR(200)`, `NOT NULL`)
  * `address_street` (`VARCHAR(200)`, `NOT NULL`)
  * `address_city` (`VARCHAR(100)`, `NOT NULL`)
  * `address_country` (`VARCHAR(100)`, `NOT NULL`)
  * `address_postal_code` (`VARCHAR(20)`, `NOT NULL`, `DEFAULT ''`)
  * `created_at_utc` (`TIMESTAMPTZ`, `NOT NULL`, `DEFAULT CLOCK_TIMESTAMP()`)
  * `updated_at_utc` (`TIMESTAMPTZ`, `NOT NULL`, `DEFAULT CLOCK_TIMESTAMP()`)
  * `version` (`INTEGER`, `NOT NULL`, `DEFAULT 1`)
* **Constraints:** Non-empty checks on `name`, `address_street`, `address_city`, `address_country`.

### Table 2: `catalog.seating_layouts`
Aggregate root for physical seat configurations. Reusable across multiple events.
* **Columns:**
  * `id` (`UUID`, `NOT NULL`, Primary Key)
  * `venue_id` (`UUID`, `NOT NULL`)
  * `name` (`VARCHAR(200)`, `NOT NULL`)
  * `is_locked` (`BOOLEAN`, `NOT NULL`, `DEFAULT FALSE`)
  * `created_at_utc` (`TIMESTAMPTZ`, `NOT NULL`, `DEFAULT CLOCK_TIMESTAMP()`)
  * `updated_at_utc` (`TIMESTAMPTZ`, `NOT NULL`, `DEFAULT CLOCK_TIMESTAMP()`)
  * `version` (`INTEGER`, `NOT NULL`, `DEFAULT 1`)
* **Foreign Keys:**
  * `fk_seating_layouts_venues`: `(venue_id) REFERENCES catalog.venues(id) ON DELETE RESTRICT`

### Table 3: `catalog.sections`
Seating areas within a layout.
* **Columns:**
  * `id` (`UUID`, `NOT NULL`, Primary Key)
  * `seating_layout_id` (`UUID`, `NOT NULL`)
  * `name` (`VARCHAR(100)`, `NOT NULL`)
  * `display_order` (`INTEGER`, `NOT NULL`, `DEFAULT 0`)
* **Constraints:**
  * `fk_sections_seating_layouts`: `(seating_layout_id) REFERENCES catalog.seating_layouts(id) ON DELETE CASCADE`
  * `uq_sections_layout_id`: `UNIQUE (seating_layout_id, id)` (supports composite FKs from child tables)

### Table 4: `catalog.rows`
Rows belonging to a specific section and layout.
* **Columns:**
  * `id` (`UUID`, `NOT NULL`, Primary Key)
  * `seating_layout_id` (`UUID`, `NOT NULL`)
  * `section_id` (`UUID`, `NOT NULL`)
  * `label` (`VARCHAR(50)`, `NOT NULL`)
  * `display_order` (`INTEGER`, `NOT NULL`, `DEFAULT 0`)
* **Constraints:**
  * `fk_rows_sections_composite`: `FOREIGN KEY (seating_layout_id, section_id) REFERENCES catalog.sections (seating_layout_id, id) ON DELETE CASCADE`
  * `uq_rows_layout_section_id`: `UNIQUE (seating_layout_id, section_id, id)` (supports composite FKs from seats)

### Table 5: `catalog.price_tiers`
Structural seat classification categories defined within a layout (e.g. "VIP", "Regular", "Balcony"). Contains no monetary pricing.
* **Columns:**
  * `id` (`UUID`, `NOT NULL`, Primary Key)
  * `seating_layout_id` (`UUID`, `NOT NULL`)
  * `name` (`VARCHAR(100)`, `NOT NULL`)
* **Constraints:**
  * `fk_price_tiers_seating_layouts`: `FOREIGN KEY (seating_layout_id) REFERENCES catalog.seating_layouts(id) ON DELETE CASCADE`
  * `chk_price_tiers_name_non_empty`: `CHECK (length(trim(name)) > 0)`
  * `uq_price_tiers_layout_id`: `UNIQUE (seating_layout_id, id)` (supports composite FK from seats)

### Table 6: `catalog.seats`
Physical chairs with permanent identity.
* **Columns:**
  * `id` (`UUID`, `NOT NULL`, Primary Key)
  * `seating_layout_id` (`UUID`, `NOT NULL`)
  * `section_id` (`UUID`, `NOT NULL`)
  * `row_id` (`UUID`, `NOT NULL`)
  * `price_tier_id` (`UUID`, `NOT NULL`)
  * `label` (`VARCHAR(50)`, `NOT NULL`)
  * `coordinate_x` (`INTEGER`, `NOT NULL`)
  * `coordinate_y` (`INTEGER`, `NOT NULL`)
* **Constraints:**
  * `fk_seats_rows_composite`: `FOREIGN KEY (seating_layout_id, section_id, row_id) REFERENCES catalog.rows (seating_layout_id, section_id, id) ON DELETE CASCADE`
  * `fk_seats_price_tiers_composite`: `FOREIGN KEY (seating_layout_id, price_tier_id) REFERENCES catalog.price_tiers (seating_layout_id, id) ON DELETE RESTRICT`
  * `chk_seats_coordinates`: `CHECK (coordinate_x >= 0 AND coordinate_y >= 0)`

### Table 7: `catalog.events`
Scheduled commercial events.
* **Columns:**
  * `id` (`UUID`, `NOT NULL`, Primary Key)
  * `name` (`VARCHAR(200)`, `NOT NULL`)
  * `description` (`TEXT`, `NOT NULL`, `DEFAULT ''`)
  * `venue_id` (`UUID`, `NOT NULL`)
  * `seating_layout_id` (`UUID`, `NULL`)
  * `scheduled_start_utc` (`TIMESTAMPTZ`, `NOT NULL`)
  * `scheduled_end_utc` (`TIMESTAMPTZ`, `NOT NULL`)
  * `status` (`VARCHAR(30)`, `NOT NULL`, `DEFAULT 'Draft'`)
  * `published_at_utc` (`TIMESTAMPTZ`, `NULL`)
  * `on_sale_at_utc` (`TIMESTAMPTZ`, `NULL`)
  * `cancelled_at_utc` (`TIMESTAMPTZ`, `NULL`)
  * `cancellation_reason` (`VARCHAR(500)`, `NULL`)
  * `created_at_utc` (`TIMESTAMPTZ`, `NOT NULL`, `DEFAULT CLOCK_TIMESTAMP()`)
  * `updated_at_utc` (`TIMESTAMPTZ`, `NOT NULL`, `DEFAULT CLOCK_TIMESTAMP()`)
  * `version` (`INTEGER`, `NOT NULL`, `DEFAULT 1`)
* **Constraints:**
  * `fk_events_venues`: `(venue_id) REFERENCES catalog.venues(id) ON DELETE RESTRICT`
  * `fk_events_seating_layouts`: `(seating_layout_id) REFERENCES catalog.seating_layouts(id) ON DELETE RESTRICT`
  * `chk_events_chronological`: `CHECK (scheduled_start_utc < scheduled_end_utc)`
  * `chk_events_status_valid`: `CHECK (status IN ('Draft', 'Published', 'OnSale', 'Closed', 'Cancelled'))`
  * `chk_events_published_layout`: `CHECK (status = 'Draft' OR seating_layout_id IS NOT NULL)`
  * `uq_events_id_layout`: `UNIQUE (id, seating_layout_id)` (crucial: supports composite FK from `catalog.event_prices` to enforce cross-layout integrity)

### Table 8: `catalog.event_prices` (Event-Scoped Pricing with Relational Layout Integrity)
Commercial ticket pricing defined specifically for each event per structural tier classification.
* **Columns:**
  * `event_id` (`UUID`, `NOT NULL`)
  * `seating_layout_id` (`UUID`, `NOT NULL`)
  * `price_tier_id` (`UUID`, `NOT NULL`)
  * `price_amount` (`NUMERIC(18, 4)`, `NOT NULL`)
  * `price_currency` (`VARCHAR(3)`, `NOT NULL`)
* **Constraints:**
  * `pk_event_prices`: `PRIMARY KEY (event_id, price_tier_id)`
  * `fk_event_prices_events_composite`: `FOREIGN KEY (event_id, seating_layout_id) REFERENCES catalog.events (id, seating_layout_id) ON DELETE CASCADE`
  * `fk_event_prices_price_tiers_composite`: `FOREIGN KEY (seating_layout_id, price_tier_id) REFERENCES catalog.price_tiers (seating_layout_id, id) ON DELETE CASCADE`
  * `chk_event_prices_amount_non_negative`: `CHECK (price_amount >= 0)`
  * `chk_event_prices_currency_iso`: `CHECK (length(price_currency) = 3 AND price_currency = upper(price_currency))`

---

## 6. Hierarchical Integrity

To guarantee that child rows cannot point to parents from different seating layouts or sections, the schema uses **database-enforced composite uniqueness and composite foreign keys**:

```text
1. Layout -> Section:
   catalog.sections has UNIQUE (seating_layout_id, id)

2. Section -> Row:
   catalog.rows stores (seating_layout_id, section_id, id)
   FOREIGN KEY (seating_layout_id, section_id)
     REFERENCES catalog.sections (seating_layout_id, id)
   --> Prevents a row from having section_id from Layout A while row has seating_layout_id = Layout B.

3. Row -> Seat:
   catalog.rows has UNIQUE (seating_layout_id, section_id, id)
   catalog.seats stores (seating_layout_id, section_id, row_id)
   FOREIGN KEY (seating_layout_id, section_id, row_id)
     REFERENCES catalog.rows (seating_layout_id, section_id, id)
   --> Prevents a seat from referencing a row belonging to a different section or layout.

4. Layout -> PriceTier -> Seat:
   catalog.price_tiers has UNIQUE (seating_layout_id, id)
   catalog.seats stores (seating_layout_id, price_tier_id)
   FOREIGN KEY (seating_layout_id, price_tier_id)
     REFERENCES catalog.price_tiers (seating_layout_id, id)
   --> Prevents a seat from referencing a PriceTier defined in a different layout.

5. Event -> SeatingLayout -> PriceTier -> EventPrice:
   catalog.events has UNIQUE (id, seating_layout_id)
   catalog.price_tiers has UNIQUE (seating_layout_id, id)
   catalog.event_prices stores (event_id, seating_layout_id, price_tier_id)
   FOREIGN KEY (event_id, seating_layout_id)
     REFERENCES catalog.events (id, seating_layout_id) ON DELETE CASCADE
   FOREIGN KEY (seating_layout_id, price_tier_id)
     REFERENCES catalog.price_tiers (seating_layout_id, id) ON DELETE CASCADE
   --> Relational Guarantee: An EventPrice can ONLY reference a PriceTier that belongs to the exact same SeatingLayout assigned to the Event! Prevents Event A from pricing a PriceTier from Layout B.
```

This guarantees structural integrity 100% inside PostgreSQL, independent of application-level bugs or external scripts.

---

## 7. Keys and Foreign Keys

| Relationship | Constraint Name | Source Columns | Target Table & Columns | Delete Action |
| :--- | :--- | :--- | :--- | :--- |
| `layouts` $\rightarrow$ `venues` | `fk_seating_layouts_venues` | `(venue_id)` | `catalog.venues(id)` | `ON DELETE RESTRICT` |
| `events` $\rightarrow$ `venues` | `fk_events_venues` | `(venue_id)` | `catalog.venues(id)` | `ON DELETE RESTRICT` |
| `events` $\rightarrow$ `layouts` | `fk_events_seating_layouts` | `(seating_layout_id)` | `catalog.seating_layouts(id)` | `ON DELETE RESTRICT` |
| `sections` $\rightarrow$ `layouts` | `fk_sections_seating_layouts` | `(seating_layout_id)` | `catalog.seating_layouts(id)` | `ON DELETE CASCADE` |
| `rows` $\rightarrow$ `sections` | `fk_rows_sections_composite` | `(seating_layout_id, section_id)` | `catalog.sections(seating_layout_id, id)` | `ON DELETE CASCADE` |
| `price_tiers` $\rightarrow$ `layouts` | `fk_price_tiers_seating_layouts` | `(seating_layout_id)` | `catalog.seating_layouts(id)` | `ON DELETE CASCADE` |
| `seats` $\rightarrow$ `rows` | `fk_seats_rows_composite` | `(seating_layout_id, section_id, row_id)` | `catalog.rows(seating_layout_id, section_id, id)` | `ON DELETE CASCADE` |
| `seats` $\rightarrow$ `price_tiers` | `fk_seats_price_tiers_composite` | `(seating_layout_id, price_tier_id)` | `catalog.price_tiers(seating_layout_id, id)` | `ON DELETE RESTRICT` |
| `event_prices` $\rightarrow$ `events` | `fk_event_prices_events_composite` | `(event_id, seating_layout_id)` | `catalog.events(id, seating_layout_id)` | `ON DELETE CASCADE` |
| `event_prices` $\rightarrow$ `price_tiers` | `fk_event_prices_price_tiers_composite` | `(seating_layout_id, price_tier_id)` | `catalog.price_tiers(seating_layout_id, id)` | `ON DELETE CASCADE` |

---

## 8. Domain vs Database Invariants

We separate invariants strictly by who owns their enforcement:

### Enforced by Database Engine:
* **Hierarchical referential integrity:** Composite foreign keys guarantee that rows, sections, seats, and price tiers strictly co-exist in the same layout.
* **Event pricing link:** `catalog.event_prices` cascade-deletes with the event.
* **Positive coordinates & price amount:** `coordinate_x >= 0`, `coordinate_y >= 0`, `price_amount >= 0`.
* **Chronological schedule:** `scheduled_start_utc < scheduled_end_utc`.
* **Non-draft event layout requirement:** `status = 'Draft' OR seating_layout_id IS NOT NULL`.
* **Scoped uniqueness:**
  * Section name unique within layout (`case-insensitive` via functional index `(seating_layout_id, lower(name))`).
  * Row label unique within section (`case-insensitive` via functional index `(section_id, lower(label))`).
  * Seat label unique within row (`case-insensitive` via functional index `(row_id, lower(label))`).

### Enforced Exclusively by Domain Model / Application:
* **Event lifecycle transitions:** State machine rules (`Draft -> Published -> OnSale -> Closed / Cancelled`).
* **SeatingLayout lock behavior:** Rejection of additions/edits when `IsLocked == true`.
* **Pricing Immutability Invariant:**
  - `Draft` $\rightarrow$ pricing can change.
  - `Published` $\rightarrow$ pricing can change.
  - `OnSale` $\rightarrow$ **pricing cannot change** (`Event.CannotModifyOnSalePricing`).
  - `Closed` / `Cancelled` $\rightarrow$ **pricing cannot change** (`Event.TerminalStatus`).
* **Publish & OnSale Workflows (Application-Orchestrated):**
  - `Event.Publish()` and `Event.StartSale()` do **not** receive a `SeatingLayout` object, `isPricingComplete` flag, or boolean proxy.
  - **Publish Workflow:** Event is made publicly visible, but ticket sales have not begun. `SeatingLayout` is **NOT** locked:
    1. Load Event aggregate.
    2. Load SeatingLayout aggregate (using `event.SeatingLayoutId`).
    3. Validate structural readiness (`layout.ValidateStructure()`).
    4. Validate required EventPrices (verify all `layout.PriceTiers` have a configured `event.GetPrice(tier.Id)`).
    5. Call `event.Publish(utcNow)`.
  - **OnSale Workflow:** Ticket sales begin. The physical structure is locked permanently:
    1. Load Event aggregate.
    2. Load SeatingLayout aggregate (using `event.SeatingLayoutId`).
    3. Validate structural readiness (`layout.ValidateStructure()`).
    4. Validate required EventPrices.
    5. Lock layout if not already locked (`layout.LockLayout()`).
    6. Call `event.StartSale(utcNow)`.
  - `Event` aggregate remains solely responsible for its own lifecycle, its own pricing state, and its own invariants.
  - `SeatingLayout` aggregate remains solely responsible for Sections, Rows, Seats, structural PriceTiers, and structural locking.
* **Minimum structural completeness:** `SeatingLayout.ValidateStructure()` requiring at least 1 section, row, seat, and price tier before publication.

---

## 9. Aggregate-Level Optimistic Concurrency

### Independent Concurrency Boundaries
Each Aggregate Root owns an **independent, isolated concurrency boundary**:
* **SeatingLayout Concurrency Boundary:** **`SeatingLayoutId + ExpectedVersion`**
* **Event Concurrency Boundary:** **`EventId + ExpectedVersion`**

**`SeatId` alone is never a concurrency boundary.** Direct child updates that bypass the aggregate root are not supported by the persistence model.

### Concurrency Flow for `SeatingLayout`:
1. **Load Aggregate:** The application loads the `SeatingLayout` aggregate root along with its child collections (`Sections`, `Rows`, `Seats`, `PriceTiers`).
2. **Capture Expected Version:** The aggregate root's current version (e.g. `version = 3`) is tracked as `ExpectedVersion`.
3. **Execute Domain Mutation:** A domain method is invoked in memory (e.g. `layout.AddSeat(...)` or `layout.LockLayout()`).
4. **Begin Persistence Transaction:** A database transaction is opened.
5. **Persist Root & Child Modifications:** The persistence layer writes changes to the root table and child tables (`catalog.seats`, etc.).
6. **Verify and Increment Version:** The persistence layer updates the aggregate root row using an optimistic concurrency check:
   ```sql
   UPDATE catalog.seating_layouts
   SET version = version + 1,
       is_locked = @is_locked,
       updated_at_utc = @now
   WHERE id = @layout_id AND version = @expected_version;
   ```
7. **Concurrency Conflict Detection:**
   * If 0 rows are affected, another transaction has concurrently modified or locked the layout. The transaction rolls back and throws a concurrency conflict (`DbUpdateConcurrencyException`).
   * If 1 row is affected, the aggregate transaction commits successfully.

### Concurrency Flow for `Event`:
Mutating an `Event` (rescheduling, publishing, starting sale, or updating `EventPrices`) checks `catalog.events` version:
```sql
UPDATE catalog.events
SET version = version + 1,
    status = @status,
    updated_at_utc = @now
WHERE id = @event_id AND version = @expected_event_version;
```
This is an independent check on the Event aggregate and is not a substitute for the `SeatingLayout` concurrency check.

---

## 10. Lock Semantics

* **What `IsLocked` Means & When It Occurs:**  
  `SeatingLayout.IsLocked == true` is a domain-level structural invariant meaning **structural modification is permanently frozen**. It occurs when an event enters **`OnSale`** (when commercial sales begin), NOT during `Publish`. Any subsequent attempt to invoke `AddSection`, `AddRow`, `AddSeat`, or `AddPriceTier` fails with domain error `SeatingLayout.Locked`.
* **What `IsLocked` Does NOT Mean:**
  * It does not mean `DELETE` is blocked at the database engine level (deletion protection is handled via foreign key restrictions and domain lifecycle rules).
  * It does not mean exclusive single-event binding; locked layouts are fully reusable across multiple events with completely different event-specific prices.

---

## 11. Money Persistence

### Current Domain Contract
The current `Money` value object enforces only:
* `amount >= 0` (non-negative)
* `currency` = valid 3-letter ISO code

The domain currently does not restrict maximum digits or mandate a 2-decimal scale.

### Persistence Specification (Architectural Decision Settled)
* **Column Types:** In `catalog.event_prices`: `price_amount NUMERIC(18, 4) NOT NULL`, `price_currency VARCHAR(3) NOT NULL`.
* **Decision & Rationale:** `NUMERIC(18, 4)` is formally established as the persistence representation. This is an explicit database storage decision (ensuring exact decimal calculations without IEEE floating-point errors and providing room for potential sub-cent fees or currency fractions), **not** a domain business rule.
* **Constraints:** `CHECK (price_amount >= 0)` and `CHECK (length(price_currency) = 3 AND price_currency = upper(price_currency))`.

---

## 12. Timestamp Ownership

To avoid split responsibilities:
1. **`created_at_utc`:** Database default `DEFAULT CLOCK_TIMESTAMP()` provides a fallback on `INSERT`, but the application/persistence layer supplies the authoritative UTC timestamp.
2. **`updated_at_utc`:** **Owned exclusively by the application/persistence layer.**
   * On every update, the application provides the explicit UTC timestamp:
     `SET updated_at_utc = @nowUtc`
   * We do **not** rely on database defaults or database triggers to mutate timestamps on `UPDATE`.

---

## 13. Delete Semantics

The persistence layer establishes distinct, explicit deletion behaviors:

### 1. `catalog.venues`
* **Delete Behavior:** Guarded via `ON DELETE RESTRICT`.
* **Rule:** A venue cannot be deleted if any seating layouts or events reference it. An unreferenced draft venue may be hard-deleted.

### 2. `catalog.seating_layouts`
* **Delete Behavior:** Guarded via `ON DELETE RESTRICT` from `catalog.events`.
* **Rule:** If any event references a layout, PostgreSQL rejects layout deletion. An unreferenced draft layout may be deleted, cascading to its internal child entities (`sections`, `rows`, `seats`, `price_tiers`).

### 3. `catalog.events`
* **Delete Behavior:** **State-Based Lifecycle.**
* **Rule:** Events transition through `Draft` $\rightarrow$ `Published` $\rightarrow$ `OnSale` $\rightarrow$ `Closed` / `Cancelled`. Once published, hard deletion is prohibited by domain policy. Cancelled events remain permanently in PostgreSQL for auditing, financial reconciliation, and ticket holder records. Deleting a draft event cascades to `catalog.event_prices`.
* **No Generic Soft Delete:** A generic `is_deleted` column is explicitly rejected to avoid index bloat, partial uniqueness degradation, and query complexity.

---

## 14. Index Strategy

Indexes are strictly purposeful, supporting only proven high-frequency access patterns:

| Table | Index Name | Columns | Query Access Pattern |
| :--- | :--- | :--- | :--- |
| `venues` | `idx_venues_name` | `(name)` | Venue search by name. |
| `seating_layouts` | `idx_seating_layouts_venue_id` | `(venue_id)` | Finding all layouts for a venue. |
| `sections` | `uq_sections_layout_name_lower` | `(seating_layout_id, lower(name))` | Section name uniqueness + layout section lookup. |
| `rows` | `uq_rows_section_label_lower` | `(section_id, lower(label))` | Row label uniqueness + section row lookup. |
| `rows` | `idx_rows_layout_id` | `(seating_layout_id)` | Layout row projection. |
| `seats` | `idx_seats_layout_id` | `(seating_layout_id)` | **High value:** Loading all seats for an entire layout in one query. |
| `seats` | `uq_seats_row_label_lower` | `(row_id, lower(label))` | Seat label uniqueness within row. |
| `seats` | `idx_seats_price_tier_id` | `(price_tier_id)` | Price tier referential validation. |
| `price_tiers` | `idx_price_tiers_layout_id` | `(seating_layout_id)` | Loading structural tier classifications for a layout. |
| `events` | `idx_events_venue_id` | `(venue_id)` | Venue schedule lookup. |
| `events` | `idx_events_seating_layout_id` | `(seating_layout_id)` | Layout usage inspection. |
| `events` | `idx_events_status_start` | `(status, scheduled_start_utc)` | **High value:** Public discovery ("Upcoming OnSale/Published events"). |
| `event_prices` | `pk_event_prices` | `(event_id, price_tier_id)` | Primary key index for event pricing queries. |

---

## 15. The Future Reservation Boundary

To prevent cross-module coupling and catastrophic database lock contention, the Catalog persistence boundary is strictly isolated from runtime booking state.

### `catalog.seats` CONTAINS:
* `id` (`UUID`) — Permanent seat identifier.
* Hierarchy (`seating_layout_id`, `section_id`, `row_id`).
* `label` (`VARCHAR`) — e.g. "Seat 12".
* Grid coordinates (`coordinate_x`, `coordinate_y`).
* `price_tier_id` (`UUID`) — Structural classification reference.

### `catalog.seats` STRICTLY DOES NOT CONTAIN:
```text
❌ seat_status (Available, Held, Reserved, Confirmed, Sold)
❌ hold_token
❌ reservation_id
❌ user_id / customer_id
❌ expires_at / held_until
❌ version / lock timestamp for booking
```
All dynamic leases, holds, and reservation states belong exclusively to the `Reservation` bounded context and its `reservation.seat_reservations` PostgreSQL schema.

---

## 16. EF Core Mapping Strategy (Design Only)

* **Location:** `src/Modules/Catalog/PulseReserve.Modules.Catalog/Infrastructure/Persistence/` (Phase 2B).
* **Isolation:** `CatalogDbContext` remains internal to the Catalog project.
* **Encapsulation:**
  * Parameterless constructors for entities configured with `internal` or `private` access.
  * Private collections (`_sections`, `_rows`, `_seats`, `_priceTiers` in `SeatingLayout`; `_prices` in `Event`) mapped via `PropertyAccessMode.Field`.
* **Type Converters:**
  * Strongly typed IDs mapped with `ValueConverter<TId, Guid>`.
  * `EventStatus` mapped to string with `.HasConversion<string>()`.
* **Value Objects:**
  * `Address` mapped as Complex Property flattened into `venues`.
  * `SeatCoordinate` mapped as Complex Property flattened into `seats`.
  * `EventPrice` mapped as owned entity type / collection mapped to `catalog.event_prices`.

---

## 17. Migration Strategy (Design Only)

* **Schema Isolation:** Migrations for Catalog are managed independently and isolated into the `catalog` schema:
  `x.MigrationsHistoryTable("__EFMigrationsHistory", "catalog")`.
* **Deployment Policy:** Idempotent SQL scripts (`dotnet ef migrations script --idempotent`) generated during CI/CD build and applied prior to application host deployment.

---

## 18. Architectural Decisions Status Summary

All core persistence and domain boundary decisions for Catalog Phase 2A have been formally settled:

1. **Pricing Scope:** **Event-Scoped Pricing** approved. Reusable `SeatingLayout` holds structural categories (`catalog.price_tiers`); `Event` holds commercial prices (`catalog.event_prices`).
2. **Lock Timing:** `SeatingLayout` locks at **`OnSale`** (when sales open), NOT at `Publish`.
3. **Publish & OnSale Orchestration:** Cross-aggregate validations are orchestrated strictly in the Application Use Case layer. Aggregates do not pass boolean flags to one another.
4. **Money Storage Representation:** Formally settled on **`NUMERIC(18, 4)`** for exact storage. Domain contract remains `amount >= 0` and 3-letter ISO currency.
5. **PriceTier Identity:** `PriceTierId` is the sole unique identifier. `PriceTier.Name` is descriptive metadata without a uniqueness constraint.

No blocking architectural or persistence design decisions remain open for Phase 2B.

---

## 19. Deferred / Future Options

* **Database-Level Trigger for Layout Immutability (`fn_enforce_layout_unlocked`):**  
  An optional trigger on `catalog.seats` to reject direct SQL inserts if the parent layout `is_locked = true` is deferred. Current protection is achieved via Domain invariants, Aggregate boundaries, and Optimistic Concurrency (`version`). A trigger will only be evaluated if external direct-SQL writers are introduced in the future.
* **Explicit `locked_at_utc` Timestamp:**  
  Adding a dedicated `locked_at_utc` column to `catalog.seating_layouts` is deferred until a specific business auditing requirement demands it beyond the existing `updated_at_utc` timestamp.

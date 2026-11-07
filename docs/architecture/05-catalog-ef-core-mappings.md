# Architecture Document 05: Catalog EF Core Entity Mappings Specification

**Bounded Context:** Catalog  
**Phase:** 2B.2 (Catalog Entity Mapping)  
**Status:** Implemented  
**Date:** October 2026  

---

## 1. Mapping Organization

All EF Core entity configurations reside in:

```text
src/Shared/PulseReserve.Infrastructure/
└── Persistence/
    └── Catalog/
        ├── CatalogDbContext.cs
        ├── CatalogPersistenceExtensions.cs
        ├── Converters/
        │   └── CatalogIdConverters.cs
        └── Configurations/
            ├── VenueConfiguration.cs
            ├── SeatingLayoutConfiguration.cs
            ├── SectionConfiguration.cs
            ├── RowConfiguration.cs
            ├── SeatConfiguration.cs
            ├── PriceTierConfiguration.cs
            ├── EventConfiguration.cs
            └── EventPriceConfiguration.cs
```

Each mapping class has a single responsibility: explicitly mapping one entity/aggregate to its corresponding table in the `catalog` PostgreSQL schema without relying on convention magic.

---

## 2. Aggregate Ownership & Boundary Preservation

The database schema directly reflects Domain-Driven Design aggregate ownership:

### 2.1 Aggregate Roots
* **`Venue` (`catalog.venues`):** Root of facility information. Flattened `Address` complex property.
* **`SeatingLayout` (`catalog.seating_layouts`):** Root of physical structure. Owns child collections (`Sections`, `Rows`, `Seats`, `PriceTiers`) mapped through private backing fields (`PropertyAccessMode.Field`).
* **`Event` (`catalog.events`):** Root of commercial event lifecycle. Owns `Prices` (`EventPrice`) mapped through private backing field.

### 2.2 Child Entities (Not Independent Aggregates)
* `Section` (`catalog.sections`) $\rightarrow$ owned by `SeatingLayout`.
* `Row` (`catalog.rows`) $\rightarrow$ owned by `Section` within `SeatingLayout`.
* `Seat` (`catalog.seats`) $\rightarrow$ owned by `Row` within `SeatingLayout`.
* `PriceTier` (`catalog.price_tiers`) $\rightarrow$ structural seat classification owned by `SeatingLayout`.
* `EventPrice` (`catalog.event_prices`) $\rightarrow$ event-specific ticket pricing owned by `Event`.

`CatalogDbContext` exposes `DbSet` properties only for Aggregate Roots (`Venues`, `SeatingLayouts`, `Events`), preventing direct query/mutation shortcuts that bypass aggregate invariants.

---

## 3. Composite Foreign Key & Hierarchy Integrity Strategy

To guarantee that child rows cannot point to parents from different seating layouts or sections, the persistence model implements database-enforced composite keys:

1. **`catalog.rows` $\rightarrow$ `catalog.sections` (`fk_rows_sections_composite`):**
   * Foreign Key: `(seating_layout_id, section_id)` $\rightarrow$ `catalog.sections(seating_layout_id, id)`
   * Action: `ON DELETE CASCADE`
   * *Guarantee:* A row cannot reference a section from another layout.

2. **`catalog.seats` $\rightarrow$ `catalog.rows` (`fk_seats_rows_composite`):**
   * Foreign Key: `(seating_layout_id, section_id, row_id)` $\rightarrow$ `catalog.rows(seating_layout_id, section_id, id)`
   * Action: `ON DELETE CASCADE`
   * *Guarantee:* A seat cannot reference a row belonging to a different section or layout.

3. **`catalog.seats` $\rightarrow$ `catalog.price_tiers` (`fk_seats_price_tiers_composite`):**
   * Foreign Key: `(seating_layout_id, price_tier_id)` $\rightarrow$ `catalog.price_tiers(seating_layout_id, id)`
   * Action: `ON DELETE RESTRICT`
   * *Guarantee:* A seat cannot reference a price tier defined in a different layout.

4. **`catalog.event_prices` $\rightarrow$ `catalog.events` (`fk_event_prices_events_composite`):**
   * Foreign Key: `(event_id, seating_layout_id)` $\rightarrow$ `catalog.events(id, seating_layout_id)`
   * Action: `ON DELETE CASCADE`

5. **`catalog.event_prices` $\rightarrow$ `catalog.price_tiers` (`fk_event_prices_price_tiers_composite`):**
   * Foreign Key: `(seating_layout_id, price_tier_id)` $\rightarrow$ `catalog.price_tiers(seating_layout_id, id)`
   * Action: `ON DELETE CASCADE`
   * *Guarantee:* An event price can **only** reference a price tier that belongs to the exact `SeatingLayout` assigned to the `Event`.

---

## 4. Strongly Typed ID Strategy

All strongly typed IDs (`VenueId`, `SeatingLayoutId`, `SectionId`, `RowId`, `SeatId`, `PriceTierId`, `EventId`) are implemented as `readonly record struct <Name>(Guid Value)`.

In `CatalogDbContext`, they are centrally mapped to PostgreSQL UUID columns using `ConfigureConventions`:
```csharp
configurationBuilder.Properties<VenueId>().HaveConversion<VenueIdConverter>();
configurationBuilder.Properties<SeatingLayoutId>().HaveConversion<SeatingLayoutIdConverter>();
...
```
This applies uniformly to primary keys, foreign keys, and shadow properties without polluting domain types with database annotations.

---

## 5. Value Object Strategy

Value Objects are mapped according to their domain semantics:

1. **`Address` (in `Venue`):**
   * Mapped as a `ComplexProperty` flattened into `catalog.venues`:
     * `address_street` (`VARCHAR(200) NOT NULL`)
     * `address_city` (`VARCHAR(100) NOT NULL`)
     * `address_country` (`VARCHAR(100) NOT NULL`)
     * `address_postal_code` (`VARCHAR(20) NOT NULL DEFAULT ''`)
2. **`SeatCoordinate` (in `Seat`):**
   * Mapped as a `ComplexProperty` flattened into `catalog.seats`:
     * `coordinate_x` (`INTEGER NOT NULL`)
     * `coordinate_y` (`INTEGER NOT NULL`)
   * Check constraint: `chk_seats_coordinates` (`coordinate_x >= 0 AND coordinate_y >= 0`)
3. **`Money` (in `EventPrice`):**
   * Mapped as a `ComplexProperty` flattened into `catalog.event_prices`:
     * `price_amount` (`NUMERIC(18, 4) NOT NULL`)
     * `price_currency` (`VARCHAR(3) NOT NULL`)
   * Check constraints: `price_amount >= 0` and `length(price_currency) = 3 AND price_currency = upper(price_currency)`

---

## 6. Concurrency Token Preparation

Optimistic concurrency is strictly aggregate-level:
* `catalog.venues` has shadow property `version` (`IsConcurrencyToken()`).
* `catalog.seating_layouts` has shadow property `version` (`IsConcurrencyToken()`).
* `catalog.events` has shadow property `version` (`IsConcurrencyToken()`).
* Child tables (`sections`, `rows`, `seats`, `price_tiers`, `event_prices`) have **zero** concurrency tokens.

The application-level concurrency verification workflow (`ExpectedVersion == PersistedVersion`) is deferred to Phase 2B.3.

---

## 7. Why Catalog Seat Has No Reservation State

`catalog.seats` represents permanent physical chairs in a facility. It contains:
* Identity: `id` (`UUID`)
* Hierarchy: `seating_layout_id`, `section_id`, `row_id`
* Classification: `price_tier_id`
* Physical Coordinates: `coordinate_x`, `coordinate_y`
* Label: `label`

It strictly does **not** contain reservation or booking state (`reservation_id`, `user_id`, `hold_token`, `expires_at`, `status`). Ephemeral seat leases and reservations belong exclusively to the `Reservation` bounded context (`reservation.seat_reservations`), completely decoupling structural catalog updates from high-frequency flash-sale reservation traffic.

---

## 8. Why EventPrice Owns Event-Specific Price

A `SeatingLayout` represents physical geometry reusable across hundreds of events. A structural category (e.g. "VIP") in a layout cannot have a single fixed price. Instead:
* `catalog.price_tiers` defines the structural classification name only.
* `catalog.event_prices` defines the commercial ticket price specifically for a given `(event_id, price_tier_id)`.
* Relational integrity is enforced by the dual composite foreign keys linking `event_prices` to both `events` and `price_tiers` on `seating_layout_id`.

# Architecture Document 02: Catalog Domain Model & Aggregate Boundaries

**Bounded Context:** Catalog  
**Status:** Implemented (Domain & Contracts)  
**Layer:** Domain & Application Boundary  

---

## 1. Catalog Responsibility & Scope

The **Catalog** module is the authoritative owner of event scheduling, venue configurations, physical seating arrangements, and base pricing structures.

### What Catalog OWNS:
* Events (`EventId`, name, schedule, sales lifecycle status, event-specific pricing)
* Venues (`VenueId`, name, physical address)
* Seating Layouts (`SeatingLayoutId`, name, structural composition)
* Sections (`SectionId`, name, display order)
* Rows (`RowId`, label, display order)
* Physical Seats (`SeatId`, stable label, coordinates, assigned tier classification)
* Structural Price Tiers (`PriceTierId`, name — classification without monetary price)
* Event-Specific Pricing (`EventPrice`: `PriceTierId`, `Money Price` — owned by `Event`)

### What Catalog DOES NOT OWN:
* **Live Seat Availability** (Owned exclusively by `Reservation`)
* **Seat Holds & Temporary Leases** (Owned exclusively by `Reservation` & Redis)
* **Bookings / Reserved State** (Owned exclusively by `Reservation`)
* **Payment State & Invoices** (Owned exclusively by `Payments`)
* **Customer Cart / Checkout Sessions** (Owned by `Reservation` / `Payments`)

---

## 2. Aggregate Boundary Decisions & Separation of Structure vs Pricing

A critical architectural pitfall in ticketing platforms is conflating physical venue layout with commercial ticket pricing.

### The Pricing Ownership Decision: Event-Scoped Pricing
A `SeatingLayout` represents a **reusable physical configuration** (e.g. "Main Hall"). A single locked layout must be reusable across multiple events. If monetary prices were attached directly to the `SeatingLayout`, all events sharing that layout would be forced to charge identical ticket prices.

To support true layout reusability:
1. **`SeatingLayout` owns Structural Seating:** Sections, Rows, Seats, and Tier Classifications (`PriceTier`: `Id`, `Name`, e.g. "VIP", "Regular", "Balcony").
2. **`Event` owns Commercial Pricing:** An Event references a `SeatingLayoutId` and owns an internal collection of **`EventPrice`** (`PriceTierId`, `Money Price`).
3. **Outcome:** Different events sharing the same layout can price seats independently (e.g., Saturday Night VIP = $100, Tuesday Matinee VIP = $70).

```text
┌────────────────────────────────┐         ┌────────────────────────────────┐
│      Venue AggregateRoot       │         │  SeatingLayout AggregateRoot   │
│  ├── VenueId                   │         │  ├── SeatingLayoutId           │
│  ├── Name                      │ ◄─────┐ │  ├── VenueId                   │
│  └── Address (ValueObject)     │       │ │  ├── IsLocked                  │
└────────────────────────────────┘       │ │  ├── PriceTiers (Structural)   │
                                         │ │  ├── Sections (Entities)       │
                                         │ │  ├── Rows (Entities)           │
                                         │ │  └── Seats (Entities)          │
                                         │ └────────────────────────────────┘
                                         │                   ▲
┌────────────────────────────────┐       │                   │
│      Event AggregateRoot       │       │                   │
│  ├── EventId                   │       │                   │
│  ├── Name, Description         │       │                   │
│  ├── VenueId ──────────────────┼───────┘                   │
│  ├── SeatingLayoutId ──────────┼───────────────────────────┘
│  ├── ScheduledStart, End       │
│  ├── Status (Draft..Cancelled) │
│  ├── EventPrices (ValueObjects)│ -> (PriceTierId, Money Price)
│  └── DomainEvents              │
└────────────────────────────────┘
```

### 2.1 Venue (`AggregateRoot<VenueId>`)
* **Boundary:** Represents the physical facility (stadium, theater, arena).
* **Identity:** `VenueId`
* **Internal State:** `Name`, `Address` (Value Object).
* **Rationale:** A venue exists independently of any scheduled concert or seating layout.

### 2.2 SeatingLayout (`AggregateRoot<SeatingLayoutId>`)
* **Boundary:** Manages the physical structural hierarchy: `Sections -> Rows -> Seats` and associated structural `PriceTiers`.
* **Identity:** `SeatingLayoutId`
* **Entities Owned:** `Section`, `Row`, `Seat`, `PriceTier` (structural tier name only).
* **Value Objects Owned:** `SeatCoordinate`.
* **Rationale:** Enforces physical structural integrity and uniqueness constraints locally without polluting the Event lifecycle. It can be prepared, validated, and locked prior to being attached to an event.

### 2.3 Event (`AggregateRoot<EventId>`)
* **Boundary:** Owns the commercial performance schedule, sales lifecycle, and event-specific pricing.
* **Identity:** `EventId`
* **Relationships:** References `VenueId` and `SeatingLayoutId` by ID only (no object graph traversal).
* **Pricing Owned:** `EventPrice` value objects mapping `PriceTierId` to `Money`.
* **Status Lifecycle:** `Draft` -> `Published` -> `OnSale` -> `Closed` (or `Cancelled`).
* **Rationale:** Encapsulates commercial parameters (pricing and lifecycle) while leaving physical venue blueprints completely reusable.

---

## 3. Why Catalog Must Never Own Seat Availability

In a flash-sale platform:
* 10,000 customers may attempt to hold the same seat in the same second.
* Seat availability changes millisecond-by-millisecond between `Available`, `Held`, and `Confirmed`.
* If seat availability were stored in Catalog, the Catalog module would become the single bottleneck of the entire platform, creating write contention on event metadata.

By design:
* **Catalog is Structural & Static:** Catalog answers "Where is seat A-101 and what does it cost?". Its data is immutable once an event goes on sale.
* **Reservation is Dynamic & Ephemeral:** Reservation answers "Who currently holds seat A-101 for Event E?".

---

## 4. Why Seat Identity Must Be Stable

The fundamental reservation invariant is:
$$\text{EventId} + \text{SeatId} \longrightarrow \text{At Most One Active Reservation}$$

For this distributed invariant to hold:
1. `SeatId` must be a globally unique, permanent identifier (`Guid`).
2. `SeatId` must **never** be generated on-the-fly during read queries.
3. Once an event transitions to `OnSale`, the seating layout is marked `IsLocked = true`, ensuring that seat coordinates, labels, and IDs cannot be mutated or deleted while ticket sales are active.

---

## 5. Domain Invariants Implemented

| Domain Invariant | Enforcement Mechanism | Failure Error Code |
| :--- | :--- | :--- |
| **Non-negative price** | `Money.Create` value object validation | `Money.NegativeAmount` |
| **Valid ISO Currency** | `Money.Create` 3-letter currency code validation | `Money.InvalidCurrency` |
| **Non-negative coordinates** | `SeatCoordinate.Create` | `SeatCoordinate.Negative` |
| **Chronological schedule** | `Event.CreateDraft` requires `ScheduledStart < ScheduledEnd` | `Event.InvalidTimeRange` |
| **No publication without layout** | `Event.Publish` verifies non-null `SeatingLayoutId` | `Event.NoSeatingLayout` |
| **No publication in terminal state** | `Event.Publish` rejects events in `Cancelled` or `Closed` states | `Event.TerminalStatus` |
| **OnSale layout immutability** | `Event.AssignSeatingLayout` rejects changes if `OnSale` | `Event.CannotModifyOnSaleLayout` |
| **Unique Section names** | `SeatingLayout.AddSection` case-insensitive name check | `SeatingLayout.DuplicateSectionName` |
| **Unique Row labels in Section** | `SeatingLayout.AddRow` scoped label uniqueness | `SeatingLayout.DuplicateRowLabel` |
| **Unique Seat labels in Row** | `SeatingLayout.AddSeat` scoped label uniqueness | `SeatingLayout.DuplicateSeatLabel` |
| **Price tier referential integrity** | `SeatingLayout.AddSeat` verifies `PriceTierId` exists in layout | `SeatingLayout.PriceTierNotFound` |
| **Structural lock enforcement** | `SeatingLayout` rejects additions/edits when `IsLocked == true` | `SeatingLayout.Locked` |
| **Pricing immutability** | `Event.SetPrice` rejects modifications once `OnSale`, `Closed`, or `Cancelled` | `Event.CannotModifyOnSalePricing`, `Event.TerminalStatus` |

---

## 6. Cross-Aggregate Coordination

`Event` and `SeatingLayout` are autonomous, decoupled aggregates:
* `Event` references `SeatingLayout` **only by `SeatingLayoutId`**. It does not reference or contain the concrete `SeatingLayout` aggregate type.
* `SeatingLayout` has zero reference to `Event` or `EventId`.
* `SeatingLayout` owns its own structural validity (`ValidateStructure()`) and structural immutability (`IsLocked`).
* `Event` owns event lifecycle state transitions (`Draft -> Published -> OnSale -> Closed / Cancelled`).
* **Application-level orchestration** coordinates workflows requiring both aggregates:
  ```text
  Application Use Case (Event Publication Workflow)
          │
          ├── 1. Load Event aggregate
          │
          ├── 2. Load SeatingLayout aggregate (via event.SeatingLayoutId)
          │
          ├── 3. Validate structural readiness (layout.ValidateStructure())
          │
          ├── 4. Validate required EventPrices (verify all layout.PriceTiers have a matching event.GetPrice(tier.Id))
          │
          └── 5. Publish Event (event.Publish(utcNow))
                 (Note: SeatingLayout is NOT locked during Publish)

  Application Use Case (Event Start Sale Workflow)
          │
          ├── 1. Load Event aggregate
          │
          ├── 2. Load SeatingLayout aggregate (via event.SeatingLayoutId)
          │
          ├── 3. Validate structural readiness (layout.ValidateStructure())
          │
          ├── 4. Validate required EventPrices (verify all layout.PriceTiers have a matching event.GetPrice(tier.Id))
          │
          ├── 5. Lock SeatingLayout if not already locked (layout.LockLayout())
          │
          └── 6. Start Sale (event.StartSale(utcNow))
                 (Note: SeatingLayout is locked upon entering OnSale)
  ```
* Aggregates **never directly inspect or mutate one another**.
* `Event.Publish()` does **not** receive a `SeatingLayout` object, an `isPricingComplete` boolean, or any external validation flag.
* Cross-aggregate validation occurs strictly in the Application Use Case orchestrator:
  - `Event` aggregate remains responsible for its own lifecycle, own pricing state, and own invariants.
  - `SeatingLayout` aggregate remains responsible for sections, rows, seats, structural tiers, and layout locking.
* **Pricing Immutability Invariant:**
  - `Draft` $\rightarrow$ pricing can change.
  - `Published` $\rightarrow$ pricing can change.
  - `OnSale` $\rightarrow$ **pricing is permanently locked** (`Event.CannotModifyOnSalePricing`).
  - `Closed` / `Cancelled` $\rightarrow$ pricing cannot change (`Event.TerminalStatus`).

---

## 7. Layout Lock Semantics & Reusability

The `SeatingLayout.LockLayout()` operation has strictly defined domain semantics:
* **What Lock MEANS:**
  * The physical structural definition of the seating layout is now **permanently immutable**.
  * Any subsequent call to `AddSection`, `AddRow`, `AddSeat`, or `AddPriceTier` will fail with error `SeatingLayout.Locked`.
* **What Lock DOES NOT MEAN:**
  * **Not Exclusive:** Lock does *not* bind the layout to a single event. A single locked layout can be referenced concurrently by multiple tour dates:
    ```text
    SeatingLayout: "Madison Square Garden - End Stage" (IsLocked: true)
           ▲
           ├── Event 1: "Concert Tour - Night 1" (SeatingLayoutId: MSG)
           ├── Event 2: "Concert Tour - Night 2" (SeatingLayoutId: MSG)
           └── Event 3: "Concert Tour - Night 3" (SeatingLayoutId: MSG)
    ```
  * **Not Unavailable:** The layout remains fully readable by all services and projection pipelines.
  * **No Ownership Fields:** `SeatingLayout` does not contain `EventId`, `OwnerEventId`, or `ExclusiveEventId`.

---

## 8. Implementation Status Matrix

* **Implemented (Phase 1 + Correction Pass):**
  * `SharedKernel` primitives (`Entity<TId>`, `AggregateRoot<TId>`, `ValueObject`, `IDomainEvent`, `Result`, `Result<TValue>`, `Error`, `IDateTimeProvider`).
  * `Catalog` Aggregates (`Event`, `Venue`, `SeatingLayout`).
  * `Catalog` Entities (`Section`, `Row`, `Seat`, `PriceTier`).
  * `Catalog` Value Objects (`Money`, `Address`, `SeatCoordinate`).
  * `Catalog.Contracts` public IDs (`EventId`, `VenueId`, `SeatingLayoutId`, `SectionId`, `RowId`, `SeatId`, `PriceTierId`), DTOs (`EventCatalogDto`, `SeatMetadataDto`), and Integration Events.
  * 28 Unit tests covering domain invariants, aggregate isolation, layout lock, and multi-event layout reuse.
  * 23 Architecture tests strictly enforcing module and aggregate boundaries (including mutual independence between `Event` and `SeatingLayout`).
* **Planned (Phase 2):**
  * Catalog persistence schema (`catalog.*` in PostgreSQL via EF Core / migrations).
  * Reservation bounded context domain model and Redis lease manager.
* **Deferred (Phase 3+):**
  * Complex seating polygon geometries and SVG stage rendering.
  * Dynamic pricing engines / surge pricing models.

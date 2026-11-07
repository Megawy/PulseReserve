# Architecture Document 06: Catalog Aggregate-Level Optimistic Concurrency

**Bounded Context:** Catalog  
**Phase:** 2B.3 (Aggregate-Level Optimistic Concurrency)  
**Status:** Approved & Implemented  

---

## 1. Why Root-Level Concurrency Exists

In Domain-Driven Design, an Aggregate Root defines the transactional consistency and invariant boundary for the entire graph of entities it owns.
In the Catalog bounded context:
* **`SeatingLayout`** is the Aggregate Root for `Section`, `Row`, `Seat`, and `PriceTier`.
* **`Event`** is the Aggregate Root for `EventPrice`.
* **`Venue`** is an Aggregate Root (single table representation).

The authoritative concurrency boundary is defined strictly as:
* **`SeatingLayoutId + ExpectedVersion`**
* **`EventId + ExpectedVersion`**
* **`VenueId + ExpectedVersion`**

Any concurrent mutation to an aggregate's child entities (e.g. adding seats, modifying seat coordinates, adjusting event prices) or to the root itself (e.g. `LockLayout()`, `Publish()`, `StartSale()`) modifies the state of the aggregate as a whole. Optimistic concurrency control must operate on the aggregate boundary, ensuring competing transactions cannot commit conflicting changes against stale aggregate state.

---

## 2. Why Child-Level Concurrency Is Insufficient

Assigning individual concurrency tokens (`xmin` or `version`) to child entities (`Seat`, `Row`, `Section`, `PriceTier`, `EventPrice`) fragments the aggregate boundary and fails critical invariants:
1. **Structural Lock Bypass:** If Administrator A locks a layout (`layout.LockLayout()`), only the `catalog.seating_layouts` root row is updated. If Administrator B concurrently edits `Seat 1`, Seat 1's child token is unmodified. Administrator B's seat modification commits into what was supposed to be a frozen layout.
2. **Blind Concurrent Additions:** If Administrator A adds a seat in Row 1 and Administrator B simultaneously adds a seat in Row 1, child-level concurrency checks only the newly inserted rows, detecting no conflict even if total capacity or layout constraints are exceeded.
3. **Price Inconsistency:** If two administrators modify different `EventPrice` entries concurrently, neither detects the other's update, allowing contradictory pricing to be established without aggregate-level coordination.

Child entities possess no autonomous lifecycle outside their aggregate root. Therefore, concurrency must be enforced at the aggregate root level.

---

## 3. Why the Interceptor Is Infrastructure-Owned

The `CatalogOptimisticConcurrencyInterceptor` is implemented in `PulseReserve.Infrastructure.Persistence.Catalog.Interceptors`:
* **Domain Purity:** Domain entities (`SeatingLayout`, `Event`, `Seat`, etc.) remain completely free of persistence concerns, ORM attributes, and EF Core dependencies.
* **Separation of Concerns:** The domain model focuses on business invariants (`IsLocked`, `ValidateStructure`, `CanModifyPricing`), while the infrastructure persistence layer translates aggregate mutations into relational transactions and concurrency tokens.
* **Automatic Change Tracking:** The interceptor hooks into EF Core's `SavingChanges` / `SavingChangesAsync` pipeline, inspecting the `ChangeTracker` graph before SQL generation without requiring manual version bumps or boilerplate in application use cases.

---

## 4. Why Explicit Integer Version Is Used

PulseReserve uses an explicit `int Version` column (`version INTEGER NOT NULL DEFAULT 1`) on aggregate roots:
* **Database Portability & Control:** Integer versioning operates consistently across relational engines, unit tests, and migration scripts.
* **Deterministic Predicate Generation:** EF Core compares against the version originally loaded into memory:
  ```sql
  UPDATE catalog.seating_layouts
  SET version = @newVersion, updated_at_utc = @now
  WHERE id = @aggregateId AND version = @expectedVersion;
  ```
* **Clear Semantic Progression:** Version monotonically increments ($1 \rightarrow 2 \rightarrow 3$), cleanly representing aggregate commit generations.

---

## 5. Why PostgreSQL `xmin` Is Not Used

PostgreSQL's system `xmin` column represents the transaction ID of the physical row:
* **Row-Level Isolation:** In PostgreSQL, updating a row in `catalog.seats` updates the `xmin` of that specific seat row only. The parent row in `catalog.seating_layouts` is not touched, leaving its `xmin` stale.
* **Provider Coupling:** `xmin` binds the application strictly to PostgreSQL system internals and behaves differently under PostgreSQL vacuuming, wraparound, and connection pooler configurations.
* **Lack of Child Propagation:** Without an explicit update to the parent table, `xmin` cannot detect concurrent child modifications.

---

## 6. Why Database Triggers Are Not Used

Database triggers (`AFTER INSERT/UPDATE/DELETE` on child tables to update parent versions) were rejected:
* **EF Core Concurrency Conflicts:** A database trigger updating the parent row mid-transaction conflicts with EF Core's own concurrency check, causing false-positive `DbUpdateConcurrencyException` errors or statement ordering anomalies.
* **Hidden Business Logic:** Triggers conceal persistence workflows inside database procedural scripts, complicating migrations, testing, and debugging.
* **Architecture Compliance:** ADR-002 and Architecture Doc 03 Section 12 establish that PulseReserve does not rely on database triggers for state or timestamp management.

---

## 7. How Concurrent Aggregate Mutations Are Detected

The interceptor coordinates aggregate concurrency through a strict 5-stage lifecycle:

```text
1. Load Aggregate Root (e.g. SeatingLayout version = 10)
   OriginalValues["Version"] = 10, CurrentValues["Version"] = 10
                       ↓
2. Domain Mutation (e.g. layout.AddSeat(...) or seat.UpdateLabel(...))
   Child marked Added/Modified in ChangeTracker; Root remains Unchanged
                       ↓
3. Interceptor Pre-Save Inspection (SavingChanges / SavingChangesAsync)
   - Detects mutation on child (Section, Row, Seat, PriceTier, EventPrice)
   - Identifies owning Aggregate Root via foreign key shadow properties
   - Validates that the owning Root is tracked in the ChangeTracker
   - Advances Root Version: CurrentValues["Version"] = 10 + 1 = 11
   - Marks Root as EntityState.Modified
   - Preserves OriginalValues["Version"] = 10
                       ↓
4. SQL Generation & Optimistic Concurrency Predicate
   UPDATE catalog.seating_layouts
   SET version = 11, updated_at_utc = @now
   WHERE id = @layout_id AND version = 10;
                       ↓
5. Concurrency Conflict Evaluation
   - If 1 row affected: Commit transaction. New version is 11.
   - If 0 rows affected (competing transaction committed first):
     EF Core throws DbUpdateConcurrencyException. Transaction rolls back.
```

### Multiple Child Changes Deduplication
If multiple children (e.g. 5 seats and 2 rows) are modified within the same aggregate transaction, the interceptor collects owning roots into a `HashSet<EntityEntry>`, ensuring the aggregate root version advances **exactly once** ($10 \rightarrow 11$, never $10 \rightarrow 17$).

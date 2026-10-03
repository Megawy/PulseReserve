# ADR-003: Aggregate Boundaries in Catalog

* **Status:** Accepted
* **Date:** 2026-10-03
* **Context:**
  Large entertainment facilities contain up to 50,000+ seats per venue. If `Event` directly contained the entire seating layout and all seats within a single aggregate, loading an event would cause catastrophic memory allocations (tens of thousands of entity allocations per request), extreme database lock contention, and an inability to reuse venue configurations across tour dates.
* **Decision:**
  Establish **three distinct Aggregate Roots** within the Catalog bounded context:
  1. `Venue` (`AggregateRoot<VenueId>`): Represents the physical facility and address.
  2. `SeatingLayout` (`AggregateRoot<SeatingLayoutId>`): Owns the physical seating structure (`Sections`, `Rows`, `Seats`, and structural `PriceTiers`).
  3. `Event` (`AggregateRoot<EventId>`): Owns the commercial performance schedule, sales lifecycle status, and commercial pricing.
  Cross-aggregate relationships exist strictly by identifier (`VenueId`, `SeatingLayoutId`). Aggregates never navigate, contain, or directly mutate one another.
* **Rationale:**
  * Keeps the `Event` aggregate lightweight (~200 bytes), allowing lifecycle transitions (`Publish`, `StartSale`, `Close`, `Cancel`) to touch only one database row.
  * Allows physical venue configurations to be prepared, structurally validated, and locked independently of event scheduling.
  * Eliminates aggregate bloat and memory pressure.
* **Alternatives Considered:**
  1. *Monolithic Event Aggregate:* Rejected due to severe memory overhead and write contention on the 50,000-seat graph.
  2. *Seat as Independent Aggregate Root:* Rejected because individual seats have no independent invariant lifecycle outside their section and layout during layout authoring.
* **Consequences:**
  * Positive: High performance, zero graph traversal overhead, clean separation of concerns.
  * Negative: Workflows spanning multiple aggregates (e.g. validating layout readiness before publishing an event) must be coordinated by application use cases rather than in-memory navigation.

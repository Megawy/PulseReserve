# ADR-004: Event-Scoped Pricing

* **Status:** Accepted
* **Date:** 2026-10-03
* **Context:**
  In the initial domain exploration, `PriceTier` inside `SeatingLayout` held both the tier name and the monetary price (`Money Price`). However, a physical `SeatingLayout` represents a reusable architectural blueprint (e.g. "Main Arena"). A single layout is reused across multiple tour dates, matinees, and festivals. Attaching commercial monetary prices directly to the layout would force every event sharing that layout to charge identical ticket prices.
* **Decision:**
  Separate **Structural Seating Classification** from **Commercial Event Pricing**:
  * `SeatingLayout` owns structural `PriceTier` entities (`PriceTierId`, `Name`) without any monetary values. These classify seats into categories (e.g. "VIP", "Regular", "Balcony").
  * `Event` owns an internal collection of `EventPrice` value objects (`PriceTierId`, `Money Price`), defining the event-specific commercial price for each structural category.
  * In PostgreSQL persistence, structural categories live in `catalog.price_tiers`, while event pricing lives in `catalog.event_prices` with composite foreign key integrity ensuring an event can only price tiers belonging to its linked layout.
* **Rationale:**
  * Maximizes reuse of complex physical seating layouts across multiple events.
  * Enables event organizers to price identical seats differently per event (e.g., Saturday night headline concert VIP = \$100 vs Tuesday acoustic matinee VIP = \$60).
  * Enforces pricing immutability at the domain level once an event enters `OnSale`.
* **Alternatives Considered:**
  1. *Layout-Scoped Pricing:* Rejected because it forced full duplication of 50,000-seat layout configurations whenever an event needed different ticket prices.
  2. *Seat-Level Pricing on Event:* Rejected as overly granular; tickets in stadium events are priced by category/tier, not arbitrary seat-by-seat prices.
* **Consequences:**
  * Positive: High operational flexibility, zero layout duplication, clean commercial pricing lifecycle.
  * Negative: Requires an additional join table (`catalog.event_prices`) when projecting seat metadata with prices for ticket buyer catalog views.

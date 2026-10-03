# ADR-006: SeatingLayout Lock at OnSale

* **Status:** Accepted
* **Date:** 2026-10-03
* **Context:**
  When an event transitions through its lifecycle (`Draft` $\rightarrow$ `Published` $\rightarrow$ `OnSale`), the timing of when physical seat structures become permanently immutable (`SeatingLayout.IsLocked = true`) determines operational flexibility and concurrency safety.
  If the layout is locked early upon `Publish`, organizers cannot correct layout typos or minor seat coordinates prior to tickets going live. Conversely, if the layout is not locked when sales begin (`OnSale`), structural changes made while customers are leasing seats would cause catastrophic booking state corruption.
* **Decision:**
  Trigger `SeatingLayout.LockLayout()` at **`OnSale`** (when commercial sales begin), **not** during `Publish`:
  * **Publish Workflow:** Event becomes publicly visible and discoverable. `SeatingLayout` structural readiness and `EventPrices` are validated by the Application layer, but `LockLayout()` is **not** called. Layout remains open for administrative corrections.
  * **Start Sale (OnSale) Workflow:** Commercial ticket sales begin. The application orchestrator verifies structural readiness, locks the `SeatingLayout` permanently (`IsLocked = true`), and calls `Event.StartSale()`.
  * **Pricing Immutability:** Once an event is `OnSale`, commercial pricing is also permanently frozen (`Event.CannotModifyOnSalePricing`).
* **Rationale:**
  * Aligns immutability with actual financial risk: the moment customers can place holds or payments on specific seats, those seats must be immutable.
  * Preserves operational flexibility during the pre-sale announcement window (`Published`).
  * Enforces the separation of concerns: `Event.Publish()` and `Event.StartSale()` do not receive `SeatingLayout` objects or boolean flags; orchestration is performed cleanly in the application layer.
* **Alternatives Considered:**
  1. *Lock upon Layout Creation:* Rejected because layouts must be incrementally constructed and revised across multiple drafts.
  2. *Lock upon Event Publish:* Rejected because it prematurely freezes the physical layout before sales even open, preventing pre-sale corrections.
* **Consequences:**
  * Positive: Optimal balance between administrative flexibility and concurrency safety.
  * Negative: Requires the Application layer to orchestrate layout locking prior to starting sales.

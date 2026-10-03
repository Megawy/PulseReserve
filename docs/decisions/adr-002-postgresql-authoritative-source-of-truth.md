# ADR-002: PostgreSQL as Authoritative Source of Truth

* **Status:** Accepted
* **Date:** 2026-10-03
* **Context:**
  Flash-sale ticketing requires handling massive bursts of concurrent reservation attempts for high-demand seats. While in-memory stores (e.g. Redis) provide sub-millisecond lease coordination, in-memory state is vulnerable to process crashes, node failovers, and split-brain scenarios. A clear authority must be established to definitively guarantee zero double-booking under all failure modes.
* **Decision:**
  Designate **PostgreSQL** as the sole authoritative source of truth for all durable state across all modules (including seat availability, holds, confirmed reservations, and commercial catalog data). In-memory layers (Redis) act strictly as high-speed admission filters and ephemeral lease acceleration layers.
* **Rationale:**
  * ACID compliance and relational integrity guarantee durable consistency.
  * PostgreSQL independently enforces the invariant "at most one active reservation per seat per event" via partial unique index constraints:
    ```sql
    CREATE UNIQUE INDEX uq_active_seat_reservation 
    ON reservation.seat_reservations (event_id, seat_id) 
    WHERE status IN ('Held', 'Confirmed');
    ```
  * Even under total Redis loss, cache flush, or distributed lease race conditions, PostgreSQL deterministically rejects duplicate reservations.
* **Alternatives Considered:**
  1. *Redis as Authoritative Source:* Rejected because Redis persistence (RDB/AOF) introduces asynchronous replication lag and potential data loss during cluster failovers.
  2. *Event Sourcing with Distributed Log Authority:* Rejected as primary storage due to operational complexity; event streams are reserved for asynchronous downstream projection.
* **Consequences:**
  * Positive: Ironclad guarantee against double-booking; durable auditability; database-enforced relational integrity.
  * Negative: PostgreSQL connection pools and write paths must be protected by Redis admission filters and lean aggregate designs to prevent database saturation under flash traffic.

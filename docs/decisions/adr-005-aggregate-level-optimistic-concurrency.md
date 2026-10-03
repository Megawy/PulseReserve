# ADR-005: Aggregate-Level Optimistic Concurrency

* **Status:** Accepted
* **Date:** 2026-10-03
* **Context:**
  In a multi-user administrative environment, concurrent modifications to `SeatingLayout` or `Event` can introduce stale-write anomalies. Specifically, if Administrator A locks a layout while Administrator B concurrently adds seats to it, Administrator B's write must not silently bypass the lock invariant. Furthermore, child entity IDs (e.g. `SeatId`, `RowId`) cannot act as independent concurrency boundaries without risking aggregate corruption.
* **Decision:**
  Enforce **Aggregate-Level Optimistic Concurrency Control** via an explicit `version INTEGER NOT NULL DEFAULT 1` token on Aggregate Roots:
  * `SeatingLayout` concurrency boundary: **`SeatingLayoutId + ExpectedVersion`**.
  * `Event` concurrency boundary: **`EventId + ExpectedVersion`**.
  Child entity IDs are **not** independent concurrency tokens. Any mutation to child entities (adding/modifying sections, rows, seats, or event prices) is executed within a database transaction that verifies and increments the aggregate root's version token (`WHERE id = @id AND version = @expectedVersion`). Direct child updates that bypass the aggregate root are strictly prohibited.
* **Rationale:**
  * Preserves aggregate boundaries and transactional consistency.
  * Deterministically detects race conditions: if a layout is locked concurrently, any competing modification touches 0 rows during root version increment, triggering a `DbUpdateConcurrencyException` and rolling back the transaction.
  * Avoids heavyweight database-level pessimistic locking (`SELECT FOR UPDATE`) across read operations.
* **Alternatives Considered:**
  1. *Pessimistic Table/Row Locking:* Rejected because long-running transactions or administrative read locks would block public catalog projections.
  2. *Child-Level Concurrency Tokens:* Rejected because child entities do not have autonomous business lifecycles outside the aggregate root.
* **Consequences:**
  * Positive: Strict aggregate consistency; complete race condition protection; clean exception handling in application handlers.
  * Negative: Competing concurrent updates to the same aggregate will cause one transaction to abort and require retry.

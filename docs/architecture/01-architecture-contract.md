# Architecture Contract 01: Core Architecture & Concurrency Standards

**Project:** PulseReserve  
**Architecture:** Modular Monolith (.NET 9 / ASP.NET Core 9)  
**Status:** Approved & Enforced  

---

## 1. Primary Invariants & Guarantees

### 1.1 Invariant: One Seat -> At Most One Active Reservation
* **Rule:** A single physical seat for a specific event can be in an active reservation state at most once across the entire system lifetime.
* **Active States:**
  * `Held` (temporary lease during checkout)
  * `Confirmed` (permanent booking post-payment)
* **Historical States:**
  * `Expired` (hold TTL expired without payment)
  * `Cancelled` (released by user or payment failure)
* **Database Enforcement:**
  PostgreSQL independently enforces this invariant via a partial unique index:
  ```sql
  CREATE UNIQUE INDEX uq_active_seat_reservation 
  ON reservation.seat_reservations (event_id, seat_id) 
  WHERE status IN ('Held', 'Confirmed');
  ```
  Even if Redis is unavailable, bypassed, or flushed, PostgreSQL guarantees zero double-booking under max concurrency.

### 1.2 Redis Role & Lease Semantics
* **Role:** High-speed admission filter and seat lease acceleration layer. Absorbs traffic spikes to prevent database connection pool saturation.
* **Lease Key:** `res:hold:{eventId}:{seatId}`
* **Lease Token:** Cryptographically random UUID (e.g. `550e8400-e29b-41d4-a716-446655440000`).
* **TTL:** Exactly 600 seconds (10 minutes).
* **Renewal:** Strictly forbidden (prevents seat-hoarding bots).
* **Release:** Token-guarded atomic Lua script:
  ```lua
  if redis.call("get", KEYS[1]) == ARGV[1] then
      return redis.call("del", KEYS[1])
  else
      return 0
  end
  ```
* **Authority:** Redis is **never** the source of truth for reservation ownership. PostgreSQL is the sole durable authority.

---

## 2. Reservation Critical Path & Asynchronous Decoupling

The synchronous hold path is strictly isolated from asynchronous event publishing and projection:

```text
[ Client HTTP ] ──► [ Redis Lease Check/Set ] ──► [ PostgreSQL TX: Insert Hold + Outbox ] ──► [ Return 201 Created ]
                                                              │
                                                        (Asynchronous)
                                                              ▼
                                                    [ Outbox Publisher ]
                                                              │
                                                              ▼
                                                        [ Apache Kafka ]
                                                              │
                                                              ▼
                                                    [ SignalR Projection ]
                                                              │
                                                              ▼
                                                    [ Angular UI Update ]
```

* **Synchronous Success Decision:** Only depends on Redis lease acquisition + PostgreSQL transactional write (Hold + Outbox row).
* **Asynchronous Infrastructure:** Kafka publishing, SignalR WebSocket pushes, Payment handling, and Notifications are completely decoupled from the HTTP response.

---

## 3. Payment & Expiration Lifecycle

1. **Explicit Checkout Trigger:**
   * A seat hold does **not** trigger a payment transaction.
   * Payment begins exclusively when the customer issues an explicit checkout command (`POST /api/payments/checkout`).
2. **State Machine Separation:**
   * **Reservation States:** `Available` -> `Held` -> `Confirmed` / `Cancelled` / `Expired`.
   * **Payment States:** `Initiated` -> `Pending` -> `Succeeded` / `Failed` / `Refunded`.
3. **Expiration Authority:**
   * PostgreSQL timestamps (`expires_at_utc`) are authoritative.
   * Background sweepers query expired rows using `FOR UPDATE SKIP LOCKED`.
   * If a payment arrives after `expires_at_utc`, PostgreSQL rejects the reservation confirmation and a refund is issued.

---

## 4. Module Boundaries & Project Structure

PulseReserve uses a **2-project Vertical Slice architecture** per module:

```text
PulseReserve.Modules.{Module}            -> Internal implementation (Domain, Slices, Data)
PulseReserve.Modules.{Module}.Contracts  -> Public contracts (Events, DTOs, Interfaces)
```

### Dependency Rules:
1. `AppHost` references all module implementation projects, `SharedKernel`, and `Infrastructure`.
2. A module implementation project may reference its own `.Contracts` and `SharedKernel`.
3. A module may reference another module's `.Contracts` project **only**.
4. **No module may reference another module's internal implementation project.**
5. `SharedKernel` has zero infrastructure dependencies.
6. `Infrastructure` references `SharedKernel` only.
7. Contracts remain lightweight with zero database or messaging package references.

---

## 5. Architectural Guardrail Rules (NetArchTest)

Automated tests in `PulseReserve.ArchitectureTests` enforce the following rules on every build:

* **Rule 1:** Module implementations cannot reference internal namespaces of other modules.
* **Rule 2:** Module contracts cannot reference internal module implementations or infrastructure.
* **Rule 3:** SharedKernel cannot reference infrastructure, AppHost, or any module.
* **Rule 4:** SharedKernel and Contracts must not reference database or messaging packages (`Microsoft.EntityFrameworkCore`, `Npgsql`, `StackExchange.Redis`, `Confluent.Kafka`, `MassTransit`).
* **Rule 5:** AppHost is the sole composition root referencing module implementations.

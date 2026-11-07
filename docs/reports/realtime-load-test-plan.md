# Real-time and contention load-test plan

**Status: a plan and two scripts. Nothing here has been run.** No number in this repository is a measured capacity. The owner has not
set performance targets, so this plan defines what to measure and how to read it, not what is "good enough".

Scripts (k6, free and open source; AGPL-3.0 tool, nothing from it ships in the product): `tests/load/hold-contention.k6.js`,
`tests/load/live-latency.k6.js`. Both are unverified templates (no k6, API or database was available when they were written); expect to
fix details on the first run (SignalR framing, cookie handling in `ws.connect`, CSRF flow).

## 1. Environment rules

* A **dedicated** stack with a disposable PostgreSQL (for example the Compose stack on a separate host or with its own volume). Never
  production, never the developer's native PostgreSQL, never `PULSERESERVE_TEST_DATABASE`.
* No payment provider is involved. Hold and live-update scenarios do not touch Stripe or Paymob; payment scenarios (not planned here)
  would use the simulator.
* Raise, for the run only: `Identity:RateLimit:PermitsPerMinute` (every virtual user registers from one address) and
  `Live:ConnectsPerMinute`. Record the values used. Leave every other limit at its default, because the defaults are what is being judged.
* Run the load generator on a different machine from the API, or the generator's CPU is part of the result.
* Record: API and database versions, instance sizes, PostgreSQL settings that differ from defaults, and whether the API ran
  `Release` or `Debug`.

## 2. Scenarios

| # | Scenario | Script | Question it answers | Pass condition (invariant, not performance) |
|---|---|---|---|---|
| 1 | **Hold contention.** N customers race for one seat. | `hold-contention.k6.js` | Does the database still allow exactly one winner under contention? | exactly one `201`, all others `409 Reservation.SeatsUnavailable`, no 5xx. A second `201` is a Severity-1 defect. |
| 2 | **Hold contention, many seats.** N customers each want overlapping sets of up to 8 seats. | (extend scenario 1) | No deadlock, no partial hold, no oversell. | After the run, `count(seats held or sold) = sum of seats in live reservations`; no reservation holds a subset of what it asked for. |
| 3 | **Announcement latency.** M browsers watch one event; each holds its own seat. | `live-latency.k6.js` | How long from the hold response to the `availability` message (p50/p95/p99), and does every announcement arrive? | `live_missed_messages == 0`. The latency distribution is recorded, not judged. |
| 4 | **Connection count.** Open connections in steps (100, 500, 1 000, …) and idle for 10+ minutes, spanning at least one max-age rotation (10 min). | (extend scenario 3) | Memory/CPU per connection; does rotation re-establish every connection and produce a `resync`, without a thundering herd? | all connections reconnect; `listenerConnected` stays true; `signals.dropped` stays 0. |
| 5 | **Burst.** A single large write burst (for example thousands of reservation changes in a few seconds, from a script, not from users). | new | Does the dispatcher coalesce, and does overflow produce a `resync` rather than silence? | If the 10 000-signal queue overflows: `resyncs{reason="queue-overflow"}` increments and clients re-read; nothing is lost silently. |
| 6 | **Listener outage.** Drop the `pulsereserve-live` database session while traffic runs (`pg_terminate_backend`). | manual | Reconnect, backoff and resync under load. | `listener.reconnects` +1, one `resync` per client afterwards, updates resume. |
| 7 | **Hold-expiry backlog.** Create tens of thousands of expired `Held` rows, then start the worker. | manual (SQL) | Does the batching drain without long locks? | backlog drains in batches of `BatchSize`; concurrent `POST /api/reservations` latency is not visibly affected (record it). |

## 3. What to record for every run

HTTP: request rate, status-code counts, p50/p95/p99 latency per endpoint. Hub: connections, `signals`, `messages`, `resyncs`,
`signals.dropped` (`GET /api/admin/live/status` and the `PulseReserve.Live` meter). Database: CPU, active connections (note the extra
`pulsereserve-live` session), lock waits, slow statements for the hold and expiry statements, `pg_stat_activity` state of the listener.
API process: CPU, memory, GC pauses, thread-pool queue length.

## 4. Reading the results

* **Invariants first** (scenarios 1, 2, 3 pass conditions). A violated invariant overrides every performance number.
* A latency or capacity figure becomes a statement in the documentation only with the environment description next to it.
* The owner should set targets (concurrent viewers per event, holds per second, acceptable announcement latency) before results are
  judged as acceptable or not. Until then the honest summary is "measured X on Y".
* Known design limits to look for rather than assume: the dispatcher is a single sequential loop per instance (slow database reads delay
  everyone on that instance); the LISTEN connection is one per instance; one process serves API, workers and hub.

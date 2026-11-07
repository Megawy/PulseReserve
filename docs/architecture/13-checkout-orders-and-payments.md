# 13 – Checkout, orders and the payment simulator

Status: implemented. Orders and payment attempts live in the **Payments module** (own `payments` schema, ADR-008);
seat state stays in the **Reservation module**. They meet only through a port adapted in the composition root.

## What already existed (and is reused unchanged)
| Concept | Representation |
|---|---|
| Active hold | `reservations.status = 'Held'` and `expires_at_utc > now()`; its seats are `event_seats.status = 'Held'` with `reservation_id` and `hold_expires_at_utc` |
| Expired / released hold | `Expired` / `Released`; expiry is lazy: a Held row past `expires_at_utc` is *reported* as Expired and its seats are reclaimable by anyone |
| Seat claimed by another reservation | `event_seats.reservation_id` points elsewhere (row locks + `FOR UPDATE SKIP LOCKED`, ADR-009) |
| Authoritative prices | `event_seats.price_*`, an immutable snapshot taken from Catalog when the event went OnSale, copied to `reservation_items` |
| Ownership | `reservations.customer_id` = authenticated user id; foreign reservations are 404 |
| Clock | PostgreSQL (`now()` / `clock_timestamp()`), never the application clock |

## Reservation lifecycle extension
```
Held --begin checkout--> Checkout --payment confirmed, seats sold--> Confirmed   (seats: Sold, permanent)
Held | Checkout --customer release/cancel--> Released        Held | Checkout --deadline passes--> Expired
```
`Checkout` keeps the **original** expiry: the hold is never extended (architecture contract: fixed, non-renewable).
A reservation in Checkout still counts as the customer's one active reservation per event (the unique index covers
`Held` and `Checkout`). Seat statuses: `Available`, `Held`, `Sold`. All three transitions are single PostgreSQL
transactions that take the reservation's row lock first (`FOR UPDATE`) – the same lock release, expiry closing and
every other checkout step take – and decide expiry with `clock_timestamp()`:

* **BeginCheckout** – owner only; `Held` and live -> `Checkout`; `Checkout` and live -> resume (idempotent); expired ->
  closed as Expired, seats freed, *no order*; Released/Expired/Confirmed -> not active.
* **ConfirmSale** – `Checkout` and live: lock this reservation's seats, mark them `Sold` only if **every** one is still
  exclusively ours, then `Confirmed`. Expired first -> closed, nothing sold. Anything else -> nothing sold.
* **ReleaseCheckout** – `Checkout` -> `Released` and the seats return to the pool (order cancelled).

## Order and payment state machines
Order (`payments.orders.status`):
```
PendingPayment -> Paid                                   payment succeeded AND seats sold
PendingPayment -> Cancelled                              customer cancels
PendingPayment -> Expired                                deadline (= reservation expiry) passed; reported lazily by the DB clock
PendingPayment | Cancelled | Expired -> RefundRequired   provider reports success but the seats can no longer be sold
Paid, RefundRequired are final (a trigger refuses leaving them)
```
Payment attempt: `Initiated -> Succeeded | Failed | Cancelled`; a `Cancelled` attempt may still become `Succeeded`
(late provider success). **A failed attempt does not change the order**: it stays `PendingPayment`, the seats stay in
Checkout until the original deadline, and the customer may start another attempt or cancel.

## Checkout transaction strategy
Creation is a two-step saga across two modules, each step idempotent and atomic:
1. Reservation module: `Held -> Checkout` under the row lock (database clock decides expiry).
2. Payments module: insert order + lines in one transaction. PostgreSQL decides uniqueness:
   `uq_orders_reservation` (one order per reservation, any key) and `uq_orders_customer_idempotency_key`.

A crash between 1 and 2 leaves a `Checkout` reservation without an order. Repeating the request resumes at step 2;
if nobody does, the hold expires at its original time and the seats return on their own (no sweeper needed).
No transaction is held while waiting for a payment provider: provider calls happen between short transactions.

Limits: no distributed transaction; a crash between the steps blocks that customer's new holds for that event until
the original expiry (at most the hold duration). Expiry is lazy (no background sweeper), as for holds.

## Price snapshot
Totals are computed by the server, in .NET `decimal`, from the reservation's snapshot prices (`numeric(18,4)` in the
database; no floating point). `payments.order_items` stores seat, quantity (always 1), unit price, line total and
currency; `orders` stores currency and total. Triggers make lines immutable and forbid changing any snapshot column of
an order (or deleting either); only `status`/`updated_at` may change. Client-supplied totals/prices do not exist in
any request DTO. Later catalog or inventory price changes cannot rewrite an order.

## Idempotency
* `POST /api/orders` + `Idempotency-Key`: same key and same reservation -> original order (200, `Idempotent-Replayed`);
  same key, different reservation -> 409 `Order.IdempotencyKeyReused`; new key for a reservation that already has a
  payable order -> that order; concurrent duplicates -> one order (unique indexes), others answer as replays.
* `POST /api/orders/{id}/payments` + key (unique per order): same key -> same attempt; a different key while an attempt
  is in flight -> 409 `Payment.AttemptInProgress` (partial unique index: one `Initiated` attempt per order).
* Completion is idempotent per attempt: the same verdict again re-drives the (idempotent) sale confirmation, a
  different verdict for a completed attempt is 409 `Payment.AlreadyCompleted`. The one exception is a verified *success* for an
  attempt already recorded as `Failed`: the verdict is kept and a payment exception is recorded (409
  `Payment.LateSuccessNeedsReview`; see document 18).
* The browser reuses the same key after an ambiguous failure (network, timeout, 5xx) and generates a new one after a
  definite answer. Nothing in memory is needed for correctness.

## Hold expiry during payment
The payment verdict never decides seat ownership; `ConfirmSale` does, atomically, with the database clock.
If the deadline passed before confirmation the seats are **not** sold, even if nobody else took them (one simple,
deterministic rule). The order becomes `RefundRequired` and the attempt is recorded as `Succeeded` (the provider took
the money), so a later refund process can find it. A late success therefore can never confirm seats already sold to
someone else, and never touches the next buyer's reservation. Verified with a real 3 second hold in the integration
tests, including a late callback racing the next buyer's own payment.

## Recovery
All state is in PostgreSQL. After a restart: orders, attempts, tokens and the checkout state are intact (tested with a
second host on the same database). If the process dies after the verdict was recorded but before the sale was
confirmed, the next identical callback (a real provider re-sends webhooks) finishes the job; tested. There is no
outbox/worker: provider webhooks and the customer's own retries are the retry mechanism, and expiry is lazy.
Deferred deliberately: a reconciliation job that sweeps `Succeeded` attempts whose order is still `PendingPayment`.

## Payment simulator boundaries
* `IPaymentProvider` is the abstraction; `SimulatedPaymentProvider` is one adapter. Architecture tests forbid the order
  domain from referencing any concrete provider. A real provider = another adapter plus a signed-webhook route that
  calls the same `PaymentService.CompleteAsync`.
* **Off by default.** `Payments:Simulator:Enabled=true` is honoured only in Development/Testing: any other environment
  fails startup. Without it no provider is available and payment initiation answers 503 `Payment.ProviderUnavailable`.
  The completion route `POST /api/dev/payment-simulator/complete` is not even mapped unless enabled.
* The simulator takes no card data and moves no money. The API marks attempts `isSimulated`, the UI labels the flow, and a
  simulated success is displayed as "Paid (simulated)".
* A verdict must name the exact order and attempt and carry the attempt's `simulatorToken` (256 random bits, shown only
  to the order's owner while the attempt is in flight); the caller must also be the signed-in owner of the order.
  Anything else is `404 Payment.AttemptNotFound`. A customer cannot mark an arbitrary order paid.

> Later work extends this document: eligibility (`GET /api/orders/{id}/payment-options` now answers `eligible` and a `reason`, see 22),
> multi-provider payments (16), late success (18), history (20), anomalies (21). The "Remaining limitations" below describe the state when
> this document was written; refunds, reconciliation, tickets and notifications exist since (14).

## API surface (all customer-only: 401 without a session, foreign orders are 404, CSRF token on every POST)
`POST /api/orders`, `GET /api/orders/{id}`, `GET /api/orders/by-reservation/{reservationId}`,
`POST /api/orders/{id}/cancel`, `GET /api/orders/{id}/payment-options`, `POST /api/orders/{id}/payments`, and (development/test only)
`POST /api/dev/payment-simulator/complete`.

## Remaining limitations
Refunds and a reconciliation job do not exist yet (`RefundRequired` is only a marker). Tickets/notifications after
`Paid` are not implemented. There is one currency per reservation (existing rule). Lazy expiry means an expired order
is reported as `Expired` by reads and closed in the database on the next write that touches it.

# 14 – Reconciliation, refunds, outbox, tickets and notifications

Status: implemented in the Payments module (`payments` schema). Builds on doc 13 (checkout and orders).

## 1. Audit: every failure window of the payment lifecycle
| # | Window | What a crash leaves behind | Recovery |
|---|---|---|---|
| W1 | attempt row inserted, provider reference not yet stored | `Initiated` attempt, no reference | the same idempotency key resumes it; otherwise reconciliation asks the provider (`NotFound` + no reference = it never reached the provider: attempt cancelled) |
| W2 | provider says success, we crash before recording it | attempt still `Initiated` | the provider re-sends the verdict, or reconciliation **asks the provider** for a stale attempt (`ResolveStaleAttempt`) |
| W3 | verdict recorded (`Succeeded`), crash before the sale is confirmed | `Succeeded` attempt, order `PendingPayment` | `FinalizeSuccessfulPayment` task re-runs the idempotent sale confirmation |
| W4 | sale confirmed (seats `Sold`), crash before the order says `Paid` | reservation `Confirmed`, order not finalized | same task; `ConfirmSale` answers "already confirmed" -> `Paid` |
| W5 | order `Paid` but no follow-up work | cannot happen: `Paid` and its two outbox events are one transaction; discovery also repairs rows that lack them | |
| W6 | order `RefundRequired` but no refund record | cannot happen: same transaction (`FinalizeAfterSaleAsync`); discovery also repairs | |
| W7 | order past its deadline, still `PendingPayment` | lazy `Expired` on read only | `ExpireOrder` task persists it and closes the reservation |
| W8 | refund sent, answer lost | provider may or may not have refunded | refund state `Unknown`, resolved by **querying** (section 3) |

Rule kept from doc 13: a late callback or a reconciliation pass never sells seats of an expired or reassigned hold:
`ConfirmSale` decides under the reservation row lock with the database clock, otherwise the order becomes `RefundRequired`.

## 2. Durable reconciliation
Work is discovered by idempotent scanners and stored in `payments.reconciliation_tasks` (unique `(kind, subject_id)`).
Kinds: `FinalizeSuccessfulPayment` (subject = attempt), `ResolveStaleAttempt` (attempt), `ExpireOrder` (order).

* **Claiming**: `FOR UPDATE SKIP LOCKED`, bounded batch (`BatchSize`), a **lease** (`locked_until`). Concurrent workers or hosts never take the
  same task; a crashed worker's tasks are claimable again after the lease. Index `(state, next_attempt_at_utc)`.
* **States**: `Pending -> Processing -> Done`; failure -> `Pending` with exponential backoff
  (`BackoffBase * 2^(attempt-1)`, capped by `MaxBackoff`); after `MaxAttempts` (default 8) -> `NeedsReview` (terminal until an administrator retries).
  A task whose worker keeps dying (lease expires `MaxAttempts` times) also becomes `NeedsReview`.
* **Poison isolation**: every task runs in its own try/catch; the exception type and a truncated message are stored (`last_error`), never tokens, references or personal data.
* **No guessing**: a provider timeout or any ambiguous answer is "unknown": the task retries later. Success is applied only when the provider says `Succeeded`
  (through the same `CompleteAsync` as a webhook). A retried local request never marks an order paid.
* **Stale pending attempts**: if the provider still says `Pending` and the order is no longer payable, the attempt is *abandoned* (marked `Cancelled`) after `AbandonAttemptAfter`
  (30 min default). A late verdict is still accepted and ends as `RefundRequired`.
* **Restart**: everything is rows; the hosted `PaymentsWorker` (config `Payments:Workers:Enabled`, default true, every 15 s) simply resumes.

Retry policy defaults: batch 20, lease 2 min, completion grace 30 s, stale after 5 min, backoff 30 s -> 1 h, 8 attempts. All validated config (`Payments:Workflow:*`).

## 3. Refunds
`payments.refunds`, one per order (`uq_refunds_order`), created in the same transaction that makes the order `RefundRequired`
(reason `PaymentAfterSaleFailed`) or by an administrator (`AdminRequested`). Amount and currency are the order's; triggers enforce that a refund
matches a `RefundRequired` order with a `Succeeded` attempt, that identity/amount are immutable, that a `Succeeded` refund is final, and that refunds are never deleted.

```
Requested --(intent persisted)--> Unknown --accepted+completed--> Succeeded   (order RefundRequired -> Refunded, same transaction)
                                   |  \--accepted, finishing later--> Pending --provider confirms--> Succeeded
                                   |  \--definitive refusal, permanent--> Failed        (terminal for the worker)
                                   |  \--definitive refusal, retryable--> Requested (new submission number, backoff)
                                   \--ambiguous (timeout etc.)--> stays Unknown, retried by QUERYING
any open state --attempts exhausted--> NeedsReview
```
Safeguards against double refunds:
* The intent is persisted as `Unknown` **before** the provider is called. A crash or timeout therefore never leaves "not sent" ambiguity.
* `Unknown`/`Pending` are resolved only by `GetRefundStatus(idempotencyKey)`. Only an explicit "no such refund" answer allows submitting, again with the **same** key
  (`refund-{id}-{submission}`), so a provider that did receive it deduplicates. The submission number rises only after a definitive refusal.
* Claims use the lease, so concurrent workers cannot submit the same refund; tests assert `RefundCalls == 1` and money moved once.
* A refund is `Succeeded` only when the provider confirms. The simulator's refunds are labelled `is_simulated` and move no money.
* Seat/inventory policy: a refund after a failed sale means the seats were **not** sold (hold expired or reassigned): nothing to release or void; the seats belong to whoever holds them now.
  A **Paid** order never changes state; refunding a completed purchase (void tickets, inventory decision) is a separate future flow and is deliberately not exposed.

## 4. Outbox
`payments.outbox_events` (unique `(type, aggregate_id)`): `IssueTickets` and `NotifyOrderPaid`, inserted in the same transaction that sets the order `Paid`.
Delivery is **at least once** (claim + lease + backoff + `NeedsReview`, like tasks); handlers are idempotent:
* tickets: unique `(order_id, seat_id)`, issued in one transaction only when the order is `Paid` (and a trigger refuses tickets for any other order);
* notifications: unique delivery record `(order_id, kind)` checked before sending (a crash between sending and recording can send twice: documented, never affects tickets).
Ticket issuance and notification are independent events: a failing notification neither blocks tickets nor touches the order.

## 5. Tickets and notifications
One ticket per purchased seat: `public_id` = 256 random bits (base64url), unique; rows are append-only (trigger). Customers read their own via `GET /api/orders/{id}/tickets` (404 for others, no email/customer id in the body).
No check-in/QR validation yet (later milestone). `INotificationSender` is a port; the host provides `DevelopmentNotificationSender` (logs order id and count, delivers nothing; refused outside Development/Testing)
or the production default that is unavailable (events retry, then `NeedsReview`; purchases unaffected).

## 6. Administrator operations (Admin policy + CSRF; mutations audited)
`GET /api/admin/payments/overview | reconciliation-tasks | refunds | outbox-events | audit-log` (filters `state`, `limit`),
`POST .../reconciliation-tasks/{id}/retry`, `.../outbox-events/{id}/retry` (only `NeedsReview` -> `Pending`),
`.../refunds/{id}/retry` (`Failed` -> `Requested` with a new submission; `NeedsReview`/`Unknown`/`Pending` -> `Unknown`, i.e. re-query first),
`.../orders/{id}/refund` (only for `RefundRequired`/`Refunded`; idempotent), `.../process-now` (one worker pass).
Every state change writes `admin_audit_log` (actor, action, subject, detail) in the same transaction; the table is append-only. The simulator completion route stays unmapped in production.

## 7. Remaining gaps before a real provider
Signed webhook endpoint calling `CompleteAsync`; real `GetPaymentStatus`/refund/lookup-by-attempt adapters with provider-side idempotency; a real notification adapter (email) that resolves the address from Identity;
refund of completed purchases; check-in/QR; alerting on `NeedsReview` counts (the overview endpoint exposes them); a UI for operators; metrics.

> Update: Stripe and Paymob adapters, signed webhooks and provider selection now exist (verified only against fake HTTP, not live or sandbox providers); see `16-multi-provider-payments.md`. The first gap above (webhook endpoint and real adapters) is therefore partly closed; the remaining items stand.

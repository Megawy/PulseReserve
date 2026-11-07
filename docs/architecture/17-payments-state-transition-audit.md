# 17. Payments, refunds and operations: state-transition audit

Scope: orders, reservations (checkout side), payment attempts, provider verdicts, refunds (including `ManuallyRecorded`),
reconciliation tasks, outbox events and tickets, as the code stands on the `upgrade/dotnet-10` working tree. Everything below
comes from reading the source and migrations. **Nothing here was run against PostgreSQL** (Docker is unavailable) and **no
provider was contacted**, so every guarantee that depends on a trigger, constraint, row lock or isolation level is marked
*source only* until the checklist at the end has been executed.

Evidence levels used in this document:

* **U** - covered by a unit or in-memory test that was executed.
* **C** - covered by an integration test that compiles but has **not** been executed.
* **S** - source inspection only.

## 1. Transition inventory

### Orders (`payments.orders`, `OrderStore`, `WorkflowStore`)

| From | To | Code path | Evidence required | DB protection | Retry / duplicate / late |
| --- | --- | --- | --- | --- | --- |
| (new) | PendingPayment | `TryCreateAsync` | customer session + live reservation | unique reservation and key; snapshot columns immutable (trigger) | duplicate key -> same order (C) |
| PendingPayment | Cancelled / Expired | `CancelAsync` (customer, row lock) | owner session | CHECK on status values only | repeat is a no-op; `Paid`/`RefundRequired` -> 409 |
| PendingPayment | Expired | `ExpireOrderAsync` (worker, row lock, DB clock) | deadline passed | CHECK only | idempotent |
| PendingPayment / Cancelled / Expired | Paid | `FinalizeAfterSaleAsync(sold)` after a recorded provider success and `ConfirmSale` | verified provider success, amount and currency matched (webhook) or provider lookup (reconciliation) | trigger: `Paid` is final | `WHERE status IN (...)` makes repeats no-ops |
| PendingPayment / Cancelled / Expired | RefundRequired | same call, `sold = false` (hold expired, released or reassigned) | same | trigger: `RefundRequired` may only become `Refunded` | same |
| RefundRequired | Refunded | `MarkRefundSucceededAsync` (provider confirmed) **or** `RecordManualRefundAsync` (administrator statement) | provider confirmation, or administrator + `NeedsReview` refund of a provider without API refunds | trigger: `Refunded` is final | `WHERE status = 'RefundRequired'` |
| Paid | anything | - | - | trigger rejects any change (S) | no code path exists |

`Cancelled` and `Expired` are not made final by the database; only the code (conditional `WHERE`) keeps them from moving anywhere
except `Paid` / `RefundRequired` on a verified late success. (S)

### Reservations (`reservation.reservations`, `ReservationStore.Checkout`)

`Held -> Checkout` (begin checkout), `Checkout -> Confirmed` (`ConfirmSaleAsync`: row lock, DB `clock_timestamp()`, all seats still
`Held` by this reservation), `Held|Checkout -> Expired` (`CloseExpiredAsync`), `Checkout -> Released|Expired` (`ReleaseCheckoutAsync`).
Each runs under `SELECT ... FOR UPDATE` on the reservation and uses a conditional `UPDATE ... WHERE status = ...`.
`ReleaseCheckoutAsync` only acts on `Checkout`, so a `Confirmed` reservation is never released by order expiry (S).
The database only has a CHECK on the status values; there is no transition trigger, so finality of `Confirmed` is code-only. (S)

### Payment attempts (`payments.payment_attempts`)

| From | To | Code path | Notes |
| --- | --- | --- | --- |
| (new) | Initiated | `BeginPaymentAsync` (order row lock) | one `Initiated` per order and unique key (unique indexes, C) |
| Initiated | Succeeded / Failed | `RecordAttemptOutcomeAsync` (order lock, then attempt lock) | verdict must carry the attempt's stored provider reference (constant-time compare); `checkout_url` cleared |
| Cancelled | Succeeded | same | a late provider success is still recorded; the order then ends `RefundRequired` if the seats are gone |
| Initiated | Cancelled | `CancelAsync`, `ExpireOrderAsync` (only without a provider reference), `CancelAttemptAsync`, `AbandonAttemptAsync`, `CancelUnreferencedAttemptAsync` | a referenced attempt is only abandoned for an order that is no longer payable and after the abandon age |
| Succeeded | anything | rejected as `ConflictingOutcome` / `AlreadyRecordedSame` | |
| Failed | Succeeded | not applied: the Failed verdict is kept and a payment exception is recorded (document 18); formerly rejected as `ConflictingOutcome` | see finding M2 |

The database has no transition trigger on attempts: `Succeeded` is final only because no code path updates it. (S)

### Provider verdicts

* **Stripe** (`StripePaymentProvider.Parse`): signature over the raw body with a 5 minute tolerance; only `checkout.session.completed` /
  `async_payment_succeeded` with `payment_status = paid` is a success; `checkout.session.expired` is a failure; everything else is
  ignored. (U for parsing, signature, replay window; C for the pipeline)
* **Paymob**: HMAC-SHA512 over the documented ordered fields; success only when captured (not pending, error, voided, refunded or a child
  transaction); **a non-success transaction is ignored, never recorded as a failure**, so a customer retrying a card on the same
  intention cannot be locked into `Failed`. (U)
* `PaymentWebhookService` additionally requires: known attempt of **this** provider, matching order, matching stored reference, success
  amount and currency equal to the order's. A mismatch changes nothing. (U)

### Refunds (`payments.refunds`)

| From | To | Code path | Evidence / actor | DB protection |
| --- | --- | --- | --- | --- |
| (new) | Requested | `RefundSql.InsertForOrder` (system on a failed sale, or admin request) | order is `RefundRequired` with a `Succeeded` attempt; amount and currency copied from the order | insert trigger re-checks order, attempt, amount, currency; `uq_refunds_order` |
| Requested | Unknown | `MarkRefundUnknownAsync` (before any provider call) | worker | - |
| Requested / Unknown / Pending | Pending | `MarkRefundPendingAsync` | provider accepted, not complete | - |
| Requested / Unknown / Pending | **Succeeded** | `MarkRefundSucceededAsync` | **provider confirmation** (response or refund lookup) | `WHERE state NOT IN ('Succeeded','ManuallyRecorded')`; trigger: `Succeeded` is final |
| Requested / Unknown / Pending | Failed / Requested / NeedsReview | `MarkRefundRejectedAsync` | provider refusal | - |
| Requested / Unknown / Pending | NeedsReview | `MarkRefundNeedsReviewAsync` (provider without API refunds), `DeferRefundAsync`, claim-time attempt limit | worker | - |
| Failed / NeedsReview / Unknown / Pending | Requested / Unknown | `RetryRefundAsync` (admin, row lock, audited) | administrator | `Succeeded` and `ManuallyRecorded` are rejected as `InvalidState` |
| **NeedsReview** | **ManuallyRecorded** | `RecordManualRefundAsync` (admin, row lock, audited) | **administrator statement**, provider registered with `SupportsRefunds = false`, amount and currency equal to the stored refund | CHECK `chk_refunds_manual_fields`; trigger: only from `NeedsReview`, then final |

`ManuallyRecorded` answers the specific questions asked of this audit:

* **Overwritten?** No. The trigger rejects any change of state, provider refund id or any `manual_*` column once the state is
  `ManuallyRecorded` (S); the store's `UPDATE` is conditional on `state = 'NeedsReview'`; a repeat with different text is a 409 and writes
  nothing. (U for the service; C for the SQL and trigger)
* **Re-entered from an invalid state?** No. The trigger rejects entry from anything but `NeedsReview` (S), the store checks the state under
  the row lock, and the CHECK constraint requires the four `manual_*` columns exactly when the state is `ManuallyRecorded`. (C)
* **Processed by a worker?** No. Claiming only selects `Requested`, `Unknown` and `Pending`; every worker update names its source states;
  `MarkRefundSucceededAsync` also excludes `ManuallyRecorded`. (S, C)
* **Confused with a provider confirmation?** No. Different state, different columns (`manual_*` vs `provider_refund_id`, which stays
  `NULL`), different label, different audit action and detail ("NOT provider-confirmed"). `isRefundComplete` in the console is true
  only for `Succeeded`. (U)
* **Second refund attempt?** No: the provider is never called for a manual-only provider, and Stripe/simulator refunds cannot be recorded
  manually. (U)

### Reconciliation tasks and outbox events

`Pending -> Processing` (claim with `FOR UPDATE SKIP LOCKED` and a lease), `Processing -> Done`, `Processing -> Pending` (retry with
backoff), `Processing -> NeedsReview` (attempt limit, or a lease that keeps expiring), `NeedsReview -> Pending` (admin retry, audited,
row lock). Unique `(kind, subject_id)` / `(type, aggregate_id)` make discovery and creation idempotent. (S, C)

### Ticket issuance

`IssueTicketsAsync` takes `FOR SHARE` on a `Paid` order, inserts with `ON CONFLICT (order_id, seat_id) DO NOTHING`; the insert trigger
rejects a ticket for a non-`Paid` order; tickets are append-only (trigger). A paid order cannot become refunded, so a ticket can never
exist for a `Refunded` order. Notification delivery is a separate event with a unique `(order_id, kind)` row. (S, C)

## 2. Findings

Severity reflects impact if the issue were left; "fixed" means changed in this working tree and covered by an executed test.

### High

**H1 - the manual-refund dialog was announced as "Refund this order?" (fixed).**
`ConfirmDialog` used the fixed id `confirm-heading` for `aria-labelledby`/`<h2>`. The refunds page hosts three dialogs, so all of them
resolved to the first heading in the document. A screen-reader user opening "Record a manual refund?" (or the retry dialog) heard the
name of a different, unrelated action on a financial confirmation. Fix: unique heading id per component instance.
Files: `shared/ui/confirm-dialog/confirm-dialog.ts`. Regression test: `manual-refund.spec.ts` ("names every dialog by its own heading"),
which failed before the change (`expected 1 to be 3`) and passes after. (U)

### Medium

**M1 - a repeated "request refund" could describe a manually recorded refund as awaiting provider confirmation (fixed).**
The request call is idempotent and returns any existing refund. For a `ManuallyRecorded` one the toast said "not complete until the
provider confirms it", which is false for that state. Fix: `refundRequestMessage` in `ops-rules.ts` (used by `refunds.ts`); test in
`manual-refund.spec.ts`. (U)

**M2 - a verified success for an attempt already recorded as `Failed` is only logged (addressed afterwards, see [18-late-payment-success-exceptions.md](18-late-payment-success-exceptions.md)).**
The description below is the state of the code when this audit was written; document 18 describes the durable exception workflow that replaced it.

`PaymentWebhookService` answers `Acknowledged / conflicts_with_recorded_outcome` and writes an Error log line; no durable record or
console item exists, so money that moved would have to be found in the logs. The reachable window is narrow: Paymob never records a
failure (non-captured transactions are ignored) and Stripe records a failure only for `checkout.session.expired` or a lookup that
reports the session closed. Fixing it needs a new reconciliation-task kind and console wording, which is a feature, so it is left as a
documented risk. (S)

**M3 - reverse-proxy and transport settings are not configured by the application (partly addressed afterwards, see [23-proxy-rate-limiting-and-simulator-isolation.md](23-proxy-rate-limiting-and-simulator-isolation.md)).**
The description below is the state of the code when this audit was written. Since then the application processes forwarded headers from listed
proxies only (mandatory `Security:ForwardedHeaders:Mode` outside Development/Testing), orders authentication before the rate limiter, and limits
financial writes per signed-in user. TLS termination, HSTS, host restriction, webhook-path throttling and log masking of `?hmac=` remain deployment
responsibilities.
There is no `UseForwardedHeaders`, `UseHttpsRedirection` or `UseHsts`, `AllowedHosts` is `*`, and the only rate limiter (sign-in) is keyed
by `RemoteIpAddress`. Behind a proxy every client shares the proxy's address, so the sign-in limit would apply to everyone at once.
Webhook and admin routes are not rate limited. The deployment must terminate TLS, forward client addresses with a trusted-proxy list,
restrict hosts and (if wanted) rate limit `/api/webhooks`. The signature, not the network, is the webhook's protection. The Paymob
signature query string is removed from `HttpRequest.QueryString` before the hosting layer logs it (`WebhookRequestRedaction`), but a
proxy or CDN access log in front of the application will still record `?hmac=...`; that must be configured there. (S, U for redaction)

**M4 - database-level guarantees are unproven.** Locking, uniqueness, triggers, the new CHECK and trigger, and the migration have never
run against PostgreSQL in this work. See the checklist.

### Low

* **L1 - detached XML documentation (fixed).** The `ManualRefundApiRequest` record had been inserted between a class summary and its class
  (`PaymentOperationsEndpoints.cs`).
* **L2 - the `state` column is `varchar(16)` and `ManuallyRecorded` is exactly 16 characters.** It fits, but there is no slack. A new test
  (`PaymentsModelConsistencyTests`, 6 cases, executed) checks every state constant of refunds, orders, attempts, tasks and outbox events
  against its column length and CHECK constraint using the EF model, so the next longer state fails the build instead of at runtime.
  It reads the model only; it does not prove PostgreSQL enforces anything.
* **L3 - `GET /api/admin/payments/refunds` returns the whole refund record,** including the derived idempotency key and the provider's
  refund id. They are not secrets and the endpoint is Admin-only, but the console does not use them. (S)
* **L4 - rejected manual-completion attempts leave no audit row or log line** (deliberate; tests assert that a rejection changes nothing).
  An administrator probing ineligible refunds is therefore invisible. (S, U)
* **L5 - a lock wait longer than the 5 s `lock_timeout` surfaces as a 500,** not a 409. It needs a stalled transaction on the same refund.
  (S)
* **L6 - the database does not make `Cancelled` / `Expired` orders, attempts or reservations final, and the refund insert trigger does not pin
  the initial state or the provider.** Only the application writes these tables; a person with SQL access could still set them. (S)
* **L7 - a customer sees an order as "Refunded" once an administrator has recorded the manual refund.** That is the administrator's statement,
  not the provider's; the customer UI does not distinguish the two. (S)
* **L8 - the webhook body limit is counted in characters after decoding,** not bytes; the `Content-Length` check and Kestrel's own limit
  bound it in practice. (S)
* **L9 - a repeat of an identical manual record by a different administrator is answered 200 with the first administrator's attribution.**
  Intended (idempotent), but the second administrator's click is not recorded. (S)

## 2b. System-driven transitions are now recorded (added afterwards)

The inventory above lists what the code *can* do. Every one of those transitions is now also written, by a database trigger and in the same
transaction, to the append-only `payments.financial_transitions` table with its source, fact basis, provider event id, actor and correlation id;
conflicting provider events that were refused are kept once per event. See [20-financial-transition-history.md](20-financial-transition-history.md).
The "Failed -> Succeeded is only logged" finding (M2) is superseded by document 18, and a mismatching event by document 21.

## 3. Verification status

See the report that accompanies this document for the exact commands and results. Summary of what could and could not be run:

* Executed: .NET build, unit tests, architecture tests, Angular tests, lint, Prettier check, Angular build, `dotnet ef` model-diff and offline
  migration scripts.
* Compiled, **not executed**: all PostgreSQL integration tests, including the eight `ManualRefund_*` tests.
* Blocked by Docker: everything in the checklist below.
* Blocked by provider credentials: any real Stripe or Paymob call (not attempted).
* Not done: browser-based visual review (light/dark, responsive); the Mailora reference frontend was not inspected.

## 4. PostgreSQL integration checklist (when Docker is available)

Run `dotnet test tests/PulseReserve.IntegrationTests` and read failures before anything else, then confirm these specifically.

**Manual refund (`PaymentWorkflowHttpTests`)**

1. `ManualRefund_IsRecordedAtomically_AsManuallyRecorded_NotProviderConfirmed_AndAudited` - one transaction updates refund, order and audit.
2. `ManualRefund_AmountOrCurrencyMismatch_OrMissingConfirmation_ChangesNothing`.
3. `ManualRefund_IsRefusedForAProviderThatRefundsThroughTheApi_AndForRefundsThatAreNotWaitingForReview`.
4. `ManualRefund_RepeatedIdentically_IsIdempotent_ButADifferentRecordIsAConflict_AndNothingOverwrites`.
5. `ManualRefund_ConcurrentSubmissions_RecordExactlyOnce` (eight parallel submissions, two different texts: one audit row).
6. `ManualRefund_TheWorkerNeverTouchesIt_AndTheDatabaseKeepsItFinal` (trigger rejects reopen, rewrite, provider id).
7. `ManualRefund_ConstraintsRejectHalfRecordsAndIllegalTransitions` (`chk_refunds_manual_fields`, entry only from `NeedsReview`).
8. `ManualRefund_NeedsAnAdministratorAndACsrfToken`.

**Still missing - add these when a database is available (none exists today)**

* *Worker versus manual completion:* hold a `NeedsReview` Paymob refund, start `RunAsync` and `POST manual-completion` together (start gate as in
  `RaceAsync`), repeat on fresh orders; invariant: the refund ends `ManuallyRecorded` or `NeedsReview`-then-`ManuallyRecorded`, never
  `Succeeded`, the provider is called zero times, exactly one audit row.
* *Retry versus manual:* `POST retry` and `POST manual-completion` on the same `NeedsReview` refund concurrently; invariant: either
  `Unknown` (retry won, manual 409) or `ManuallyRecorded` (manual won, retry 409), never both audit rows.
* *Existing-row migration:* create the schema at `20261021204239_AttemptCheckoutUrl`, insert refunds in every state, apply
  `20261022112035_ManualRefundRecord`, confirm all rows pass `chk_refunds_manual_fields` and `chk_refunds_state`; then apply `Down` with no
  manual rows (succeeds, function and constraint restored) and with one manual row (**must fail** and leave the row intact).
* *Trigger and constraint matrix:* direct SQL for every illegal move (`Requested -> ManuallyRecorded`, `Succeeded -> ManuallyRecorded`,
  `ManuallyRecorded -> NeedsReview`, edit of each `manual_*` column, `manual_note` on a non-manual row).
* *Order/refund agreement:* after a manual record the order is `Refunded`, `payments.tickets` for it is empty, and a late duplicate success
  webhook for the same attempt changes nothing.
* *Lock timeout:* hold `FOR UPDATE` on a refund in one session and submit a manual record; observe the response (expected today: 500, see L5).
* Pre-existing gaps recorded in the multi-provider document (concurrent identical webhooks across hosts, success racing expiry with real
  timing, concurrent `InitiateAsync`) stay on the list.

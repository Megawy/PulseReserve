# 18. Late payment success: the exception workflow

This closes finding M2 of [17-payments-state-transition-audit.md](17-payments-state-transition-audit.md): a cryptographically
verified provider success that arrives for a payment attempt this system had already recorded as `Failed` used to be answered
`200 conflicts_with_recorded_outcome` and written to the log, with no durable record and nothing for an operator to act on.

Status of the evidence, as everywhere in these documents: unit and in-memory tests were executed; the PostgreSQL integration
tests and the migration were **not** run (Docker is unavailable); no provider was contacted. Nothing here is production-ready.

## 1. Root cause, confirmed from the code

`OrderStore.RecordAttemptOutcomeAsync` maps `(Failed, success)` to no transition and, because it is not "the same verdict", to
`ConflictingOutcome`; `PaymentService.CompleteAsync` turned that into `Payment.AlreadyCompleted`, and `PaymentWebhookService` acknowledged
it (`conflicts_with_recorded_outcome`) after an Error log line. The transaction wrote nothing, so a payment the provider reports as
taken left no row anywhere.

What can leave an attempt `Failed` (every call to `RecordAttemptOutcomeAsync` with `Succeeded = false`):

| Producer | Provider | Evidence |
| --- | --- | --- |
| verified `checkout.session.expired` webhook | Stripe | `StripePaymentProvider.Parse` maps it to `PaymentFailed` |
| reconciliation lookup reporting `expired` | Stripe | `GetPaymentStatusAsync` -> `Failed` -> `PaymentService.CompleteAsync` (only for attempts still `Initiated`) |
| simulator "fail" | Simulator (development only) | `SimulatorCompletePayment` |
| **a Paymob callback** | **none** | every non-captured transaction (declined, pending, error, voided, refunded, child) is `Ignored`, never a failure; unit test `APaymobCallback_CanNeverCreateAnException_BecauseItNeverRecordsAFailedAttempt` |

So the case is structurally unreachable for Paymob and, for Stripe, contradicts Stripe's own rule that an expired Checkout Session
cannot be paid; in practice it is a provider anomaly, an out-of-order or replayed delivery, or a local mis-recording. The cost of being
wrong is unrecorded money, which is why it is recorded rather than argued away. A Stripe expiry and a Paymob callback do not mean the
same thing and are not treated as if they did: the new code keys on the **local attempt state**, not on a provider.

## 2. Domain model

A new table `payments.payment_exceptions`, one row per `(attempt, kind)`, with one kind today: `LateSuccessAfterFailedAttempt`.

* **Created atomically with the verdict check.** `RecordAttemptOutcomeAsync` already holds the order lock and the attempt lock. For
  `(Failed, success)` it inserts the exception in the same transaction (`ON CONFLICT (attempt_id, kind) DO NOTHING`) and returns
  `LateSuccessRecorded` or, for any repeat, `LateSuccessAlreadyRecorded`. The webhook answers 200 only after that commit; a database error
  surfaces as an error so the provider redelivers.
* **Provider-verified facts vs. local facts.** Before the call the webhook service has already verified the signature, the attempt, the
  provider, the order and the **amount and currency against the order**. The row stores the amount and currency copied from the order
  (not from the event), the provider, whether it is simulated, the first verified event id and the detection time. Separately it stores
  a snapshot of what this system had recorded: the attempt's original status, failure code and verdict time, and the order's status at
  detection.
* **The Failed verdict is evidence and never changes.** The attempt row is untouched, a trigger refuses to change the status of a Failed
  attempt that has an exception, and the exception's facts are immutable.
* **Nothing is fulfilled.** No finalisation runs: the order is not set to `Paid` or `RefundRequired`, the reservation is not asked to sell,
  no outbox event and no ticket exist, and no refund record is created or claimed. (A ticket cannot exist for a non-`Paid` order anyway:
  insert trigger.) The existing late-success path for a **Cancelled** attempt is unchanged, as are all provider-confirmed refunds.
* **No second charge.** While the order has **any** payment exception (open or resolved), `BeginPaymentAsync` answers `HeldForReview`
  (`409 Payment.OrderHeldForReview`), under the same order lock as the insert, for every provider. Nothing retries the payment and
  nothing falls back to another provider. A same-key replay is still answered with the existing attempt.
  An attempt that is **already in flight** for the order (the customer started again after the failure) is withdrawn in the same
  transaction when the exception is created (`Cancelled`, failure code `order_held_for_review`, hosted link cleared), so the
  payment page the customer may still have open is no longer offered. This is the existing "cancelled attempt" semantics: a verified
  success for that attempt is still recorded and finalised by the normal rules (the order can then be `Paid`), and the exception
  remains open, showing the order as "paid by a different payment, so this one is an additional charge". The seats-and-money
  consequence of a customer who pays the provider page anyway is therefore visible rather than lost, but it is not prevented: the
  provider's own session cannot be closed by this application.
* **One supported next step.** The money must be returned in the provider's dashboard and an administrator records that
  (`POST /api/admin/payments/exceptions/{id}/resolve`). The record is `Resolved (administrator statement)`: nothing is sent to a provider,
  the order, attempt and refund records are not touched, and the response, audit text and console all say the provider did not confirm it.
  Resolving does **not** reopen the order for payment: an order that ever had a payment exception is never paid again (a withdrawn
  attempt may still be payable at the provider, and two applied payments for one order is what must never happen). The customer
  starts a new order.

### Why not a reconciliation task, a rewrite, or the refund table

* *Reconciliation task (`ReconciliationKind`).* A task is worker-driven work with retries; this item needs no worker action, only a
  person. A task has nowhere to store the provider-verified facts or an operator's resolution, and `Done` would not say who resolved it
  or how. Adding a kind would also widen a CHECK constraint on a table whose lifecycle does not fit. The existing task, retry and
  `NeedsReview` machinery is therefore unchanged and still covered by its own tests; **there is deliberately no worker pass for
  exceptions**, so there is no retry or escalation to test. Detection is atomic with the webhook and relies on the provider's redelivery
  if the database is unavailable.
* *Rewriting `Failed -> Succeeded` and reusing the normal path.* That would erase the original verdict and could mark the order `Paid`
  from a payment this system had judged failed.
* *The refund table.* A refund requires a `Succeeded` attempt and a `RefundRequired` order (insert trigger) and there is one refund per
  order. A duplicate charge on an order that is already `Paid` (or already has a refund) cannot be represented there. Relaxing that is a
  larger, riskier change; the consequence is that these refunds are made in the provider's dashboard even for Stripe. There is no
  automated refund queued for the attempt, so a person refunding it cannot cause a second one.

## 3. What an administrator sees

`GET /api/admin/payments/exceptions?state=Open|Resolved&limit=` and the console page **Payment exceptions** (nav "Exceptions", overview tile
"Payment exceptions"). Per row, two clearly separated groups:

* **This system recorded:** the attempt's original status (`Failed`), the reason code and time on record, "kept exactly as recorded".
* **The provider reported:** a success of the amount and currency, "signature, amount and currency matched the order" (or "Simulated
  success ... no real money moved"), and when it was seen.
* **Order:** its status as it is now, an explanation (still awaiting payment and no longer payable / closed and not fulfilled / paid by a
  different payment so this one is an additional charge / has its own refund record for a different payment), "Fulfilment: not applied",
  and the order's own refund state when it has one.
* **Status / next step:** `Needs an administrator` with "Refund required. This app does not refund this payment." and the action "Record
  refund", offered **only** when the server's `nextAction` is `RefundExternallyThenRecord` (derived from the state; the client never
  chooses it). After resolution: "Resolved (administrator statement)", who, when, reference, and "the provider did not confirm it".

Never exposed: webhook payloads or signatures, provider references, checkout links or client secrets, the provider event id, customer
identifiers. A unit test asserts the response type has none of those members.

The dialog repeats what the action does and does not do, shows the amount read-only (an echo that the server verifies against the stored
exception), takes an optional reference and note, requires an explicit confirmation tick, blocks double submission, keeps the dialog open
with a truthful message on a 409 (nothing new recorded, list refreshed, retry refused), says "nothing was recorded; you can try again" on
other failures, and closes with a toast on 401. After success focus returns to the page heading.

Authorization: the Admin policy and the antiforgery filter on the whole group (401 anonymous, 403 customer, 403 without a token). Rejected
resolutions (not confirmed, mismatch) change and audit nothing. Success writes the audit action `ResolvePaymentException`
(actor, exception id, attempt, order, provider, "NOT provider-verified"; the free-text note is not copied into the log).
Detection itself has no acting administrator, so it is recorded by the exception row (detected time, original verdict snapshot), not by an
audit entry.

## 4. Migration `20261022140828_LateSuccessPaymentExceptions`

Offline SQL was generated and read (`dotnet ef migrations script`); it has **not** been applied to any database.

* Table `payments.payment_exceptions` (21 columns), PK `pk_payment_exceptions`, FKs to `orders` and `payment_attempts` (`RESTRICT`).
* CHECKs: kind, state (`Open`/`Resolved`), resolution value, amount > 0, currency format, and `chk_payment_exceptions_resolution_fields`
  (the four resolution columns are present exactly when the state is `Resolved`).
* Indexes: unique `(attempt_id, kind)` (idempotency), `(state, detected_at_utc)`, `order_id`.
* Triggers (hand-written plpgsql, reviewed but unexecuted): insert guard (must start `Open` and name a `Failed` attempt of its own
  order), update/delete guard (facts immutable, `Resolved` final, no delete), and `trg_payment_attempts_keep_failed_verdict` on
  `payment_attempts` (a Failed attempt that has an exception cannot change status; deliberately narrow so no other attempt is affected).
* Existing rows: the migration only adds a table and triggers; no existing row is read or changed. The new attempt trigger fires on
  `UPDATE` of attempts but only acts when an exception exists.
* `Down` drops the trigger, the table and the functions. **It destroys every recorded exception and resolution**; export them first.
* The application version that inserts into the table must not run before the migration is applied; an old application version is
  unaffected by the new table.

## 5. Tests

Executed (no database): `PaymentWebhookServiceTests` (late success after expiry recorded once with nothing else changed; repeats and
different event ids produce one exception; bad signature, wrong amount, wrong currency, wrong order produce none; cancelled and
expired orders are recorded but never finalised or sold even when the seats would be sellable; new payments blocked for every
provider; Paymob cannot create one; normal success and failure-after-success unchanged), `PaymentExceptionOperationsTests` (service
rules, 401/403/CSRF, 400/404/409 mapping, body ignored for provider/order, list filter, no secrets in the response),
`PaymentsModelConsistencyTests` (state lengths and CHECK lists, resolution all-or-nothing, unique index), and the Angular specs in
`exceptions.spec.ts`.

Compiled, **not executed** (need PostgreSQL): `ProviderPaymentsHttpTests.LateSuccess_*`, `LateSuccess_WithdrawsAnAttemptAlreadyInFlight_*`, `ResolvingALateSuccessException_*`,
`PaymentExceptionRoutes_NeedAnAdministratorAndAnAntiforgeryToken`, `PaymentExceptions_AreProtectedByTheDatabase`, and the extended
success-versus-expiry race (`ASuccessRacingASessionExpired_...` now asserts an exception exists exactly when the failure won).

## 6. Remaining risks

* The money is returned by hand, for Stripe as well; there is no automated or provider-confirmed refund for an exception, and no way
  to fulfil the order from the payment even when its seats are still held. A customer who really paid must rebuy after a refund.
* An order with an exception can never be paid again, even after resolution; the customer is told to start a new order (a generic conflict message, no detail).
* The reservation is not released when the exception is created; it simply runs out. A `Cancelled`/`Expired` order stays that way.
* A verified failure arriving after a recorded success is still only acknowledged (the success stands), as before.
* Exceptions do not expire and there is no notification; the overview tile is the only prompt.
* A resolution is a statement. Nothing reconciles it against the provider's settlement report.
* If the database is down when the webhook arrives the provider must redeliver; Stripe retries for days, other providers may not.

## 7. PostgreSQL checklist (Docker required)

1. Apply `20261022140828_LateSuccessPaymentExceptions` to a database at the previous migration that already contains attempts in every
   status, orders in every status and refunds; confirm nothing existing changed; apply `Down` and confirm only the new objects vanish.
2. Run the tests listed above and read failures first. In particular confirm: one row after eight concurrent deliveries
   (`uq_payment_exceptions_attempt_kind`), the attempt trigger message, the facts-immutable and `Resolved`-final messages, the insert
   guard for a non-Failed attempt, and the resolution CHECK.
3. Add (missing today): a race between `POST resolve` and a new late-success delivery; a race between the customer's payment start and the
   late-success webhook for the same order (the order lock serialises them: either the new attempt exists first and is withdrawn by the
   exception, or the exception exists first and the start is refused; neither order may leave an `Initiated` attempt beside an open
   exception, which `LateSuccess_WithdrawsAnAttemptAlreadyInFlight_...` checks only for the sequential case); a real redelivery after a forced database failure mid-request.
4. Confirm the discovery scanner never creates a task for a Failed attempt with an exception (asserted once in
   `LateSuccess_AfterARecordedFailure_...`).

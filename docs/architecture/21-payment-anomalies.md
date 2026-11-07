# 21. Payment anomalies: valid signature, wrong contents

Evidence labels are those of document 19 (U executed unit test, C integration test compiled but **not executed**, S source review).
**Nothing here has run against PostgreSQL or a real provider.**

## 1. The gap this closes

Document 19 (invariant 5) recorded a gap: a webhook whose signature verified but whose amount or currency did not match the order was
rejected with `400` and written to a log line only. For Paymob, which has no payment lookup, there was no other trace, so money that
moved for the wrong amount could only be found by someone reading logs. Events that named an unknown attempt, another provider's attempt
or another order, or carried the wrong provider reference, had the same fate.

A **payment anomaly** is now a durable, deduplicated, administrator-visible record of such an event.

## 2. What is and is not an anomaly

Only a **success** event whose **signature was verified** can become one (failure events carry no money; an invalid signature proves nothing
about who sent it and is never stored, U: `AnInvalidSignature_CreatesNoAnomaly_NoMatterWhatTheBodySays`, `AFailureEvent_NeverCreatesAnAnomaly_...`).

| Kind | Meaning | Associated with an attempt/order? |
| --- | --- | --- |
| `AmountOrCurrencyMismatch` | known attempt and order, but the amount or currency differs from the order's | yes |
| `ReferenceMismatch` | known attempt and order, but the provider reference differs from the stored one (or none was stored) | yes |
| `UnknownPaymentReference` | names an attempt this system does not know (another environment sharing the account, a deleted test, a forgery that passed a leaked secret) | **no** |
| `InconsistentAssociation` | names an attempt of another provider, or an order that does not own the attempt | **no** |

An anomaly is **evidence, not a payment.** It never marks an order paid, never issues a ticket, never confirms a sale, never creates or
completes a refund, never changes an attempt's verdict (including a `Failed` one: a mismatching event after a failure neither rewrites it nor
becomes a trusted late success, U: `AMismatch_AfterAFailedVerdict_...`) and never touches a paid order (U: `AMismatch_AfterASuccessfulPayment_...`).
The answer is **`200` once the evidence is stored** (this changed: it used to be `400`, which made the provider retry an event that was already
on record; see section 4b). The evidence is stored first and the answer is sent only afterwards.

## 3. Untrusted received data, trusted expected data

The table keeps the two apart, in separate columns, so neither can be mistaken for the other:

| Untrusted - taken from the event as received | Trusted - read from this system's own records |
| --- | --- |
| `received_amount_minor`, `received_currency` (only if it is three letters, upper-cased) | `expected_amount_minor`, `expected_currency` (from the order, converted with the currency's own exponent) |
| `claimed_attempt_id`, `claimed_order_id` (plain values, **no foreign key**) | `attempt_id`, `order_id` (foreign keys, set only when the event matched a real attempt of that provider and order) |
| `provider_event_id` (the provider's id, used for deduplication) | `order_status_at_detection`, `attempt_status_at_detection` |

A database trigger refuses an association that is not real (`attempt_id` must be an attempt of that `order_id` and `provider`), and a CHECK
(`chk_payment_anomalies_association`) requires `UnknownPaymentReference` / `InconsistentAssociation` to have no attempt and no order, so an
attacker-influenced event cannot appear attached to a genuine order. Currency columns are CHECKed against `^[A-Z]{3}$`.

**Minimal and sanitized.** No payload, signature, header, provider reference, customer name or email, card data or URL is stored, logged or
returned (U: `TheAnomalyRecord_AndTheLogs_CarryNoSignatureSecretPayloadOrReference`, `AnAnomaly_KeepsTrustedAndUntrustedValuesApart_...`).

## 4. Deduplication, failure behaviour, redelivery

`dedupe_key` is `"<provider>:<providerEventId>"`, falling back to `"<provider>:<claimedAttemptId>:<amount>:<currency>:<reason>"` when the
event has no id. A **unique index on `(kind, dedupe_key)`** and `ON CONFLICT DO NOTHING` make any number of redeliveries one row (U with an
in-memory recorder: `ARedeliveredMismatchEvent_IsOneAnomaly_ADifferentEventIsAnother`; C for the index). A redelivery is answered `200` and
costs one indexed insert attempt. When the insert reports "nothing inserted" the store **confirms inside the same transaction that the record
exists** (`SELECT EXISTS ...`) and throws if it does not, so a duplicate is never the reason an event is acknowledged without a record.

The event id is part of the identity, so a **different** event (a second payment) is a second anomaly, even for the same attempt.

If the evidence cannot be stored, the exception propagates, the webhook is **not** acknowledged (5xx) and the provider redelivers, so the
record is created on a later delivery (U: `IfTheEvidenceCannotBeStored_TheWebhookIsNotAcknowledged_...`; C: `AMismatchWebhook_IsNotAcknowledgedWhenTheEvidenceCannotBeStored_...`).

## 4b. Webhook acknowledgement matrix (the HTTP endpoint, not a service method)

The rule behind every row: **2xx only when the event needs no redelivery** (applied, a harmless duplicate, or its evidence is durably recorded);
**4xx when it is not authentic or not readable** (nothing is stored, nothing is trusted); **5xx when it could not be recorded or judged**, so the
provider delivers it again. A body carries a `status` and a `reason` code only. All rows are exercised through the real endpoint with real
signatures and adapters, against in-memory stores (U: `WebhookAcknowledgementTests`, 21 cases); the SQL side is the compiled-only C tests.

| # | Situation | Outcome | HTTP | Stored (durably) | Provider redelivers? |
| --- | --- | --- | --- | --- | --- |
| 1 | Provider not enabled here | `ProviderUnavailable` | 503 | nothing | yes (configuration may be temporary) |
| 2 | Unknown provider segment / body > 256 KiB | route / size check | 404 / 413 | nothing | no / provider-specific |
| 3 | Invalid signature, stale timestamp, forged | `Rejected invalid_signature` | **400** | **nothing** - an unauthenticated event is never evidence | yes - the right signal for a mis-set secret |
| 4 | Signed but structurally invalid body | `Rejected malformed_event` | **400** | nothing | yes |
| 5 | Authentic but of another merchant context (live event on a test deployment, connected-account event, another Paymob integration) | `Rejected wrong_context` | **400** | nothing | yes - deliberately: the endpoint is registered in the wrong place and the event may be **real money**; a failed delivery stays visible and resendable in the provider's dashboard |
| 6 | Event type we do not use / not conclusive | `Acknowledged ignored_event` | 200 | nothing | no |
| 7 | Verified success, data matches | `Applied` (normal workflow) | 200 | order/attempt transition + history | no (duplicates are idempotent) |
| 8 | Verified success for an attempt recorded `Failed` | `Acknowledged late_success_recorded_for_review` | 200 | payment exception (same transaction as the check) | no |
| 9 | Verified verdict that contradicts a recorded opposite one | `Acknowledged conflicts_with_recorded_outcome` | 200 | a `Rejected` history row once per provider event; nothing overwritten | no |
| 10 | Verified **success** that disagrees with our records (amount, currency, reference, association) | `AnomalyRecorded <reason>` | **200** | **anomaly row, committed first** | **no** - recorded |
| 11 | Row 10 delivered again | `AnomalyRecorded <reason>` | 200 | no new row; the existing one is confirmed in the transaction | no |
| 12 | Verified success naming an attempt we do not know / another provider's / another order | `AnomalyRecorded unknown_attempt` etc. | 200 | anomaly **without any attempt/order association** | no |
| 13 | Verified **failure/expiry** that matches nothing | `Acknowledged unmatched_event_ignored` | 200 | nothing (no money moved) | no - a 4xx here would be retried for days for an event that can never be processed |
| 14 | **Storage failure** while recording (or any database failure before durable persistence) | exception -> `Server.Error` problem | **5xx** | nothing | **yes** - and the retry is recorded |
| 15 | Verified success but our order data is unusable (order missing, amount not expressible in minor units) | `Failed order_not_found / order_amount_invalid` | **500** | nothing | yes - the event is kept until the data is repaired |

How the providers retry (this is why the matrix looks the way it does):

* **Stripe** (documented): any non-2xx, including 4xx, is a failed delivery and is retried for up to three days with exponential backoff in live
  mode (three attempts over a few hours in a sandbox); a 2xx ends it. A delivery can be resent from the dashboard for up to 15 days after the
  event (30 days with the CLI). Stripe asks for a quick 2xx before slow work; the evidence insert is one short transaction.
* **Paymob**: the documentation consulted states **no** retry policy, response requirement or resend window. Nothing here assumes one: the only
  answer that is safe whatever Paymob does is "2xx after the record exists, otherwise not 2xx" (**unverified, P**).

Every outcome has an explicit HTTP mapping and the default arm **fails closed**: an outcome nobody mapped throws (a 5xx, never an acknowledgement and
never a silent 4xx), and a test pins the list of outcomes so adding one forces a decision (U: `EveryWebhookOutcome_HasAnExplicitHttpMapping_...`).

Invariant: *a verified anomaly is never applied as a payment, but once it is durably recorded it is acknowledged and not retried; a database
failure is never acknowledged.* The `Failed` and `Rejected` rows deliberately stay non-2xx because acknowledging them would hide an event the
operator may still need from the provider.

Consequences worth knowing: a provider account shared by several environments sends each environment the others' events; successes become
`UnknownPaymentReference` anomalies (resolve them as "Not our payment") and failures are ignored. Use a separate provider account (or at least
separate webhook endpoints and secrets) per environment.

## 5. The operator workflow

* **Where:** Admin -> Payments -> **Anomalies** (`/admin/payments/anomalies`), plus an overview tile "Payment anomalies" (open count) that links to the open list.
  API: `GET /api/admin/payments/anomalies?state=Open|Resolved&limit=` (admin only; a state other than those two is `400`).
* **What the page shows:** the event's claim (untrusted) next to what this system's records say; the fixed sentence "No order was paid, no
  ticket issued, no refund made or created"; the state "Needs an administrator"; for a resolved one, the recorder, the time, the reference and
  "The provider did not confirm it".
* **The one action:** *Record outcome*. The administrator must (1) pick one of three statements, (2) optionally add a reference (<= 200
  characters) and a note (<= 300), (3) tick that they compared the event with the provider's dashboard. Nothing is sent without all of it, and
  the button is disabled while a request is in flight.

| Statement (`resolution`) | Meaning |
| --- | --- |
| `NotOurPayment` | confirmed with the provider that it does not belong to this system/environment |
| `RefundedExternally` | the money was returned outside this system |
| `NoActionNeeded` | investigated; nothing further is needed (e.g. the provider reversed it) |

`POST /api/admin/payments/anomalies/{id}/resolve` `{ investigationConfirmed, resolution, externalReference, note }` (administrator, antiforgery
token, per-user write limit). **The statement changes nothing else**: order, attempt, refund, seats and tickets are untouched, and the provider is
not called. It stores the actor, the time and the text, writes an `admin_audit_log` row (`ResolvePaymentAnomaly`) and a history row labelled
`AdministratorStatement`. States: `Open -> Resolved`, then final (trigger). An identical repeat is `200` and returns the first recording; a
different statement for a resolved anomaly is `409`; a missing confirmation or unknown value is `400` and writes nothing; an unknown id is `404`.
The order id, provider and amounts in the body are ignored - the anomaly is read from the stored row (U: `OrderProviderAndAmountsInTheBody_AreIgnored_...`).

### What the operator must check, per provider

| Provider | Find the payment by | Confirm before recording |
| --- | --- | --- |
| Stripe | the event id (`evt_...`, shown in the row) in Developers -> Events, then the Checkout Session / PaymentIntent it references; the attempt id appears as the session's `client_reference_id` | amount and currency actually captured; whether it is already refunded or disputed; whether the session belongs to this environment |
| Paymob | the transaction id shown as the event id, in the dashboard's Transactions; the merchant order id carries the attempt id | captured (not pending/voided), amount and currency, environment (test vs live account) |
| Simulator | not applicable - the simulator never signs events | - |

If the payment cannot be found, **record nothing** and leave the anomaly open. There is no "dismiss" action, on purpose.

## 6. Why not just accept it, or turn it into a late-success exception?

A late-success exception (document 18) is a verified fact about *our* attempt and *our* amount, which is what makes it safe to treat as
"money was taken for this order". An anomaly is the opposite case: the contents disagree with our records, so treating it as a
payment (or refunding it automatically) would act on a value we do not trust. The only safe automatic action is to keep it and tell a person.

## 7. Migration `20261022152016_PaymentAnomalies`

Adds `payments.payment_anomalies` (23 columns, 7 CHECKs, 2 foreign keys, 4 indexes incl. the unique dedupe index), an update guard (facts immutable,
`Resolved` final, no delete), an insert guard (starts `Open`, real association) and the two history triggers. It reads and changes no existing row.
**Rollback refuses while any anomaly exists** (explicit `RAISE EXCEPTION`); export the rows first if it is truly intended (C:
`ThePaymentAnomalyRollback_RefusesWhileAnAnomalyExists_AndSucceedsWithout`).

## 8. Tests

| Area | Tests | Status |
| --- | --- | --- |
| Webhook scenarios (right contents applied with no anomaly; wrong amount/currency; wrong reference; unknown attempt; forged association; Paymob identity; redelivery; failed event; after failed/paid; storage failure; no secrets) | `PaymentWebhookServiceTests` (anomaly section) | U |
| The HTTP endpoint end to end: every row of the acknowledgement matrix in section 4b (status, body, what is stored, retry then record) | `WebhookAcknowledgementTests` (21 cases) | U |
| Operations: next step derived from state, confirmation required, fixed statements only, length limits, conflict/replay, endpoint security and statuses, admin-only list/history | `PaymentAnomalyOperationsTests` (12) | U |
| Route sweep includes the new routes (anonymous 401, customer 403, missing token 403) | `FinancialEndpointSecurityTests` | U |
| Table constraints and triggers, dedupe index, mismatch kept once, unknown attempt kept unlinked, forged association refused, resolution as statement with audit | `ProviderPaymentsHttpTests.Audit.cs` (`AMismatchingEvent_...`, `AnEventForAnUnknownAttempt_...`, `AMismatchWebhook_...`, `AnAnomaly_CanOnlyBeResolved...`, `PaymentAnomalies_AreProtectedByTheDatabase`) | C |
| Console page: untrusted vs trusted, no association, resolved view, confirmation gate, double-submit, 401/403/404/409/429/500, ids/labels, empty/error | `anomalies.spec.ts` (19) | U |

## 9. Known limits

* Failure events (e.g. Stripe `checkout.session.expired` for an unknown session) are not recorded: no money moved, and storing every one would
  let an unauthenticated-but-signed flood grow the table.
* An event with a valid signature but **no** event id and a changing amount can create more than one row (the fallback key includes the amount).
* There is no push notification. The signals are the durable count and the oldest age on the overview, structured log events and counters, and a
  throttled backlog reminder - see [26](26-operational-signals-and-alerting.md). Nothing alerts by itself.
* A `2xx` answer tells the provider the event was received, not that the payment was applied; the body says `recorded_for_review` so a person
  reading a delivery log can tell them apart. Nothing in an anomaly's life is hidden from a provider dashboard that shows the event as delivered.
* The anomaly's existence is itself unverified against PostgreSQL (all `C` rows above).

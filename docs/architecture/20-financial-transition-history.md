# 20. Financial transition history

Scope: a durable, append-only record of **every state change** of an order, a payment attempt, a refund, a payment exception and a
payment anomaly, plus every provider event that was **refused** because it conflicted with a recorded outcome. It complements the
administrator audit log (`admin_audit_log`, document 15), which records what an administrator *did*; the history records what *moved*,
whoever or whatever caused it.

Evidence labels used here are the ones defined in document 19 (U executed unit test, C integration test compiled but **not executed**,
S source review, SQL-syntax offline parse). **Nothing in this document has run against PostgreSQL.** Every statement about trigger
behaviour, atomicity or deduplication in the database is therefore *designed and reviewed*, not observed.

## 1. Design: a trigger, fed by the application

The history is written by one generic PL/pgSQL trigger function, `payments.record_financial_transition()`, attached to
`payments.orders`, `payments.payment_attempts`, `payments.refunds`, `payments.payment_exceptions` and `payments.payment_anomalies`
(an `AFTER INSERT` trigger and an `AFTER UPDATE OF <status column> ... WHEN (OLD.x IS DISTINCT FROM NEW.x)` trigger each).

Why a trigger and not logging calls in the stores:

* **Coverage does not depend on memory.** Thirty-odd code paths change these columns (webhook verdicts, workers, administrators, customers,
  clean-up, the simulator). A new code path, or a hand-written `UPDATE`, is recorded without anyone remembering to log it.
* **Atomic with the change.** The row is inserted by the same statement's trigger in the same transaction. A rolled-back change leaves no
  history; a committed change always has its row. If the history insert itself failed, the financial change would fail with it.
* **A no-op writes nothing.** The update trigger fires only when the status/state value really changes, and the stores' conditional
  `UPDATE ... WHERE status = ...` do not touch the row when a duplicate event arrives. A redelivered webhook therefore adds no row.

What the trigger cannot know on its own (who caused the change, on what basis, which provider event) is supplied by the application
through **transaction-local settings**:

```
set_config('pulsereserve.source' | '.fact_basis' | '.provider_event_id' | '.actor_user_id' | '.correlation_id' | '.reason', value, true)
```

The `true` makes each setting local to the current transaction: it disappears at commit or rollback and can never leak to another
request on a pooled connection. `OrderStore.BeginAsync` and `WorkflowStore.BeginAsync` apply the ambient
`FinancialTransitionContext` (an `AsyncLocal`, `Domain/FinancialTransitionContext.cs`) at the start of **every** transaction. Scopes are
opened where the cause is known: the webhook service (`ProviderWebhook`), the reconciliation lookup, each worker pass, each
administrator operation, the customer's payment start, the simulator. `Refine` narrows the basis/reason for one operation inside a scope.
A change made with **no** scope is still recorded, with source `Unspecified`, so a missing scope is visible rather than silent.

The context carries only identifiers and stable codes. It cannot carry a payload, a signature, a secret, a provider reference or
customer detail (U: `TheContext_CarriesOnlyIdentifiersAndCodes_NeverASecretOrPayload`).

## 2. What a row says

Table `payments.financial_transitions` (migration `20261022151129_FinancialTransitionHistory`):

| Column | Meaning |
| --- | --- |
| `id`, `occurred_at_utc` | row id; database clock (`now()`, the transaction's start time) |
| `entity_type`, `entity_id`, `order_id` | `Order`, `PaymentAttempt`, `Refund`, `PaymentException`, `PaymentAnomaly`; the entity's id; the order it belongs to (NULL for an anomaly that is not associated with one) |
| `from_state` | NULL when the entity was created by this row |
| `to_state`, `event_code` | the new state; `<entity>.<from or new>_to_<to>` (lower case), or `paymentattempt.rejected_conflicting_verdict` |
| `kind` | `Transition` (a change happened) or `Rejected` (an event was refused and **nothing changed**; `from_state` is the unchanged state and `to_state` the state the event asked for) |
| `source` | who or what: `ProviderWebhook`, `ReconciliationLookup`, `Worker`, `Administrator`, `Customer`, `Simulator`, `System`, `Unspecified` |
| `fact_basis` | what the change rests on, **never blended**: `ProviderVerified` (a provider's signature-verified or looked-up answer), `ApplicationDecision` (a rule of this application), `AdministratorAction` (an administrator commanded a retry or a request), `AdministratorStatement` (an administrator said something happened outside this system; **no provider confirmed it**), `Unspecified` |
| `provider`, `provider_event_id` | provider name and the provider's own event id, when the change came from one |
| `actor_user_id` | the user id of the customer or administrator; NULL for providers and workers |
| `correlation_id` | the enclosing request/trace id (inherited, else `Activity.TraceId`, else a new GUID) so one webhook's rows can be grouped |
| `reason` | stable reason code, e.g. `provider_verdict`, `late_success_recorded`, `attempt_withdrawn_for_review`, `sale_confirmation`, `payment_deadline_passed`, `refund_intent_recorded`, `needs_operator`, `administrator_retry`, `manual_refund_statement`, `exception_resolution_statement`, `anomaly_resolution_statement`, `initiation_uncertain`, `payment_anomaly_recorded`, `stale_attempt_cleanup` (full list: `TransitionReason`) |

Indexes: `idx_financial_transitions_entity (entity_type, entity_id, occurred_at_utc)`, `idx_financial_transitions_order (order_id, occurred_at_utc)`,
and the partial unique `uq_financial_transitions_rejected_event (entity_type, entity_id, event_code, provider_event_id) WHERE kind = 'Rejected'
AND provider_event_id IS NOT NULL`.

There are deliberately **no foreign keys**: the history is evidence and must outlive anything it describes.

### Fact basis in practice

| Transition | Basis | Why |
| --- | --- | --- |
| attempt `Initiated -> Succeeded` from a webhook | `ProviderVerified` | signature, amount and currency were checked |
| order `PendingPayment -> Paid` by the sale confirmation that follows | `ApplicationDecision` (`sale_confirmation`) | the order changes because this application's rules allow it, not because a provider said so |
| refund intent recorded (`Unknown`) | `ApplicationDecision` | recorded *before* the provider is asked |
| refund `Pending -> Succeeded` after a provider answer | `ProviderVerified` | |
| ambiguous provider answer, escalation to `NeedsReview` | `ApplicationDecision` | never labelled as a provider fact (U) |
| administrator retry / refund request | `AdministratorAction` | |
| manual refund, exception resolution, anomaly resolution | `AdministratorStatement` | not provider-verified (U: `TransitionLabellingTests`) |
| a rejected administrator request | nothing | it never reaches the store, so it leaves no history (U) |

## 3. Duplicates, rejected events and failures

* **Duplicate webhook for an applied outcome:** the store's conditional update changes nothing, the trigger does not fire, no row. (C)
* **Conflicting provider event** (e.g. a `Failed` verdict for an attempt already `Succeeded`): the state is kept and one `Rejected` row is
  written with `ON CONFLICT (...) WHERE kind = 'Rejected' DO NOTHING`, so redelivery of the same provider event id adds nothing. (C)
* **Rolled-back change:** no row. A recovery done later by a worker has its own row, labelled `Worker` / `ApplicationDecision`. (C)
* **History insert fails** (disk, a failing trigger, a permission problem): the whole financial transaction fails. A webhook is then not
  acknowledged (5xx) and the provider redelivers; a worker pass leaves the item for its next pass. Nothing is applied without its record,
  and nothing is recorded without being applied. This is the intended trade: *availability of a payment confirmation is sacrificed to
  the existence of its evidence*. (C: `WebhookRedelivery_AfterAForcedDatabaseFailure_...`, with a deliberately failing trigger)
* **Anomaly evidence fails to store:** the webhook is not acknowledged, and the redelivery records it (C).

## 4. Append-only, and what that does not cover

`trg_financial_transitions_append_only` (the same `forbid_row_change()` guard the audit log and tickets use) rejects any `UPDATE` or `DELETE`.
It does **not** stop `TRUNCATE`, `DROP TABLE`, a superuser, the table owner disabling triggers, or restoring an older backup. Production
roles used by the application should not own the table or be superusers; that is a deployment matter this repository cannot enforce.
The table has **no retention job**. Growth is bounded by financial activity (a handful of rows per order), but a retention/archival period is a
business and legal decision that has not been made and that code must not invent: the decisions required are listed in
[25](25-evidence-retention-and-history-policy.md), the database-role assumptions in [24](24-database-roles-and-evidence-protection.md).

History starts when the migration is applied. **Existing rows are not backfilled** (inventing prior transitions would fabricate causes and fact
bases that were never recorded; see document 25, section 3): an order that was paid before the migration has no history, and the History page says
so, and flags any entity whose earliest recorded change is already a move from an existing state ("Some history starts mid-life").

## 5. Reading it

* API (administrator only, antiforgery not needed for GET): `GET /api/admin/payments/history?orderId=&entityType=&entityId=&limit=` -
  newest first, limit 1-200 (default 50), `entityType` must be one of the five values (else 400). No payload, signature, reference,
  email or name is part of a row.
* Console: **Admin -> Payments -> History** (`/admin/payments/history?order=<id>`), read-only, labels the basis in words
  ("Provider-verified fact", "Administrator statement (not provider-verified)").
* Operators with database access can group a request's rows: `SELECT ... FROM payments.financial_transitions WHERE correlation_id = '<id>' ORDER BY occurred_at_utc, id`.

## 6. Migration notes

* `20261022151129_FinancialTransitionHistory` adds one table, three indexes, one function, eight triggers and the append-only trigger.
  It reads and changes no existing row. The new triggers add one small insert per status change; they take no lock beyond the row being
  changed. `CREATE TRIGGER` takes a `SHARE ROW EXCLUSIVE` lock on each of the four tables for the duration of the statement, which is
  instantaneous on these tables but still blocks concurrent writes for that moment: apply during a quiet period.
* **Rollback refuses while any history row exists** (explicit `RAISE EXCEPTION`, `23000`); an empty table rolls back cleanly. If a
  rollback is truly intended, export the rows first and drop the table by hand. (C: `TheTransitionHistoryRollback_RefusesOnceAnyHistoryExists_...`)

## 7. Tests

| Area | Tests | Status |
| --- | --- | --- |
| Context scopes, no leakage, identifiers only | `TransitionContextTests` (6) | U |
| Basis/source/actor of each operation, rejected requests leave nothing | `TransitionLabellingTests` (7) | U |
| Model consistency (state columns fit, CHECKs, table/index names) | `PaymentsModelConsistencyTests` | U |
| Whole life of a webhook payment, redelivery adds nothing, rollback leaves nothing, append-only, no secrets, conflicting event kept once | `ProviderPaymentsHttpTests.Audit.cs` (`TransitionHistory_*`, 5) | C |
| Migration applies to representative rows; rollback guard with/without rows; full chain forward and back | `PaymentsMigrationPostgreSqlTests` | C |
| Console page: validation, loading/error/empty states, labels, refused events | `history.spec.ts` (10) | U |

## 8. Known limits

* No row for reads, for reconciliation tasks, outbox events, tickets or reservations (their own tables are their record).
* `occurred_at_utc` is the transaction start time, so two changes in one transaction share it; ordering inside one transaction is by `id`
  (random) and is therefore not guaranteed. The `event_code` and `from_state -> to_state` chain still identify the order of states.
* `provider_event_id` is truncated to 200 characters, `correlation_id` to 64, `reason` to 100 before being sent to the database.
* Not verified against PostgreSQL: trigger firing conditions, `set_config(..., true)` behaviour under connection pooling, the partial unique
  index's `ON CONFLICT` inference, lock behaviour of `CREATE TRIGGER` on a populated database.

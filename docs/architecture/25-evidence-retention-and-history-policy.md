# 25. Evidence, history and retention: what is implemented, what is not, what must be decided

This document separates the kinds of record the payment domain produces, states exactly what protects each of them in the code today, and lists the
decisions that **cannot be made in code**. It deliberately contains **no retention period**: nothing in the repository or its documentation
states a legal, tax, contractual or card-scheme obligation, and inventing one would be worse than leaving the question open.
Evidence labels are those of document 19; nothing here was exercised against PostgreSQL.

## 1. The five kinds of record

| Kind | Where | Written by | Authoritative for | Deletable by the application? |
| --- | --- | --- | --- | --- |
| **Financial transition history** | `payments.financial_transitions` | a database trigger, in the same transaction as the change | what moved, from which state to which, who or what caused it, on what basis | **No.** The application never issues `DELETE`/`TRUNCATE` (U), the table accepts no `UPDATE`/`DELETE` (trigger, C) |
| **Investigation evidence** | `payments.payment_anomalies`, `payments.payment_exceptions` | the webhook pipeline (created), an administrator (resolved) | that a signature-verified event was not applied, and what an administrator said afterwards | **No.** Same protections; an `Open` item is also counted and shown until someone records an outcome |
| **Administrator audit trail** | `payments.admin_audit_log` | the administrator operations | who did what, with text that says "NOT provider-verified" where that is true | **No.** Append-only trigger; insert-only privilege |
| **Provider event identifiers** | `provider_event_id` columns in the history and the anomalies | copied from the verified event | the key that links our record to the provider's own event log and dashboard | as part of the rows above |
| **Application logs** | the log platform | `ILogger` | operations and alerting only | n/a - **not evidence of record** |

Logs are deliberately not part of the evidence: they are sampled, rotated and lost for reasons unrelated to finance, and they carry only
identifiers and codes by design. A financial question must be answerable from the database tables (and the provider's dashboard), never from
logs. Conversely the database tables never contain a payload, signature, secret, provider reference, card detail or customer name/email.

## 2. What is implemented

* **No application delete path.** A static test fails if application code contains `DELETE FROM payments.*`, `TRUNCATE`, `DROP`, `ALTER TABLE`,
  trigger switching or privilege changes, and fails if the stores ever `UPDATE` the insert-only tables (U: `FinancialDatabaseBoundaryTests`).
* **No automatic deletion.** There is no retention job, no purge, no archival. This is intentional until section 5 is decided.
* **Unresolved items cannot disappear.** An `Open` anomaly or exception is never deleted, is counted from the database in the overview, is
  listed first and oldest first, and is repeated in the log until resolved (document 26). A *resolved* item stays visible with its recorded
  statement.
* **Destructive rollback is guarded** (document 24, section 7): a migration rollback refuses while any of this evidence exists.
* **Recorded-at-the-time versus reconstructed.** Every row in `financial_transitions` is written by the trigger when the change commits: there is
  no import path, no backfill and no repair job, so the table can only contain what was observed live (S; the integration tests that would prove
  the trigger fires are compiled, not executed). Two things tell an operator how far to trust a history:
  * a change made without any application scope is recorded with source `Unspecified` (shown as "Not specified"): it was done outside the
    application's labelled paths, for example by hand in SQL;
  * the History page flags an entity whose **earliest recorded change is already a move from an existing state** ("Some history starts mid-life"):
    its earlier changes were never recorded, and the page says that they are not reconstructed (U: `history.spec.ts`). It does not guess when
    that entity was created, and it declines to judge when the page may hide older rows (200 or more).

## 3. Why there is no backfill

Orders, attempts and refunds that existed before the history migration have a *current* state and some timestamps (`created_at_utc`,
`completed_at_utc`, `updated_at_utc`), not a sequence of transitions. Turning those columns into history rows would **invent** prior states,
causes and fact bases ("this attempt succeeded because the provider verified it") that were never recorded. The history therefore starts when the
migration is applied. If a business need appears to reconstruct earlier years, it must come from an independent source (the provider's own
exports and the administrator audit log), be stored **in a separate, clearly named table** and never in `financial_transitions`.

## 4. What the provider keeps (and for how long)

The provider's records are the independent half of every investigation. Stripe's documentation states that it retries a failed webhook
delivery for up to three days in live mode (a sandbox: three attempts over a few hours) and that a delivery can be resent manually from the
dashboard for up to 15 days after the event was created and with the CLI for up to 30 days. Paymob's documentation consulted for this work does
not state a retry policy or a resend window (**unverified**). Practical consequence: an event that needed a configuration fix (for example a wrong
webhook secret or a wrong-environment endpoint) should be fixed and resent **within those windows**, because afterwards the only copy of the fact
is whatever the provider's dashboard still shows.

## 5. Decisions that are not for code to make

Each needs an owner and a written answer; until then the safe default (keep everything, delete nothing) stands.

| # | Decision | Owner | Inputs the owner needs |
| --- | --- | --- | --- |
| D1 | How long `financial_transitions`, `payment_anomalies`, `payment_exceptions` and `admin_audit_log` rows must be kept, and what happens at the end (delete, anonymise, archive) | finance / legal | the jurisdictions of the seller and of its customers; tax and accounting record-keeping rules that apply; obligations in the acquirer/provider contracts; the dispute (chargeback) windows of Stripe and Paymob; the time the business wants to be able to explain a payment |
| D2 | Whether `actor_user_id` (customers and administrators) and `resolved_by` are personal data for the purposes of erasure requests, and whether the answer is "anonymise the id, keep the row" | data-protection owner / legal | the privacy notice, the applicable data-protection law, D1 |
| D3 | The mechanism for any permitted deletion or anonymisation: it must be a deliberate, **audited maintenance procedure run by the table-owner role**, never an application feature, because the append-only triggers (correctly) forbid the application to do it | engineering + DBA | D1, D2, document 24 |
| D4 | Archival: exporting old rows to storage that the database operator cannot modify, and how long the export is kept | engineering + finance | D1; backup policy |
| D5 | Retention and access for application logs | operations | the log platform in use; the fact that logs hold ids and codes only |
| D6 | Confirm that no backfill of history is wanted (section 3) | finance | whether any pre-migration orders need a reconstructed narrative, from which independent source |
| D7 | Who may resolve an anomaly or exception, and the evidence they must attach | operations / finance | the runbook in document 19, section 9 |

## 6. What an operator must never do to "tidy up"

* Delete or edit anomaly, exception, history or audit rows to make a counter go down. The counter measures work left to do; the only way to reduce it
  is to record an outcome after investigating (document 19, 9.3).
* Treat a log line as the record of a payment decision.
* Backfill `financial_transitions` by hand-written SQL: the rows would look as if the application had recorded them live.
* Run a database rollback against a populated database to "reset" it: the guards will refuse, and bypassing them destroys evidence.

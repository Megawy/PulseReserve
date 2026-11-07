# 19. Financial domain: invariants, operations runbook and production checklist

This document consolidates documents 13, 14, 16, 17 and 18 (and the later 20-27: transition history, payment anomalies and the webhook
acknowledgement matrix, eligibility and ambiguous initiation, proxy/rate limiting/simulator isolation, database roles, evidence retention,
operational signals, PostgreSQL integration verification) into one place and states, invariant by invariant, **which layer enforces
it and what evidence exists**. It describes behaviour that is implemented; anything that is not implemented is listed as a limitation.

Evidence legend (used throughout; never blended):

| Tag | Meaning |
| --- | --- |
| **U** | Unit or in-memory test that was **executed** (fakes for stores and providers; no database, no real provider). |
| **C** | Integration test that needs a disposable PostgreSQL through Docker. **Update 2026-10-23: every C-tagged test below has since been executed against PostgreSQL and passes (266 / 266, four consecutive full runs; see document 27 section 0, which also records the one product defect and the test fixes that run produced).** Read "C" in this document as "executed on 2026-10-23" unless a row says otherwise; the limitations in sections 11-12 (what no test covers) are unchanged. |
| **S** | Source and migration review only. |
| **SQL-syntax** | The SQL text was parsed offline by libpg_query (statement and PL/pgSQL syntax only). Not executed. |
| **B** | Visually reviewed in a browser. **Nothing in this work was.** |
| **P** | Verified against a real provider sandbox. **Nothing was.** |

Nothing in this document is "proven against PostgreSQL" or "verified with Stripe/Paymob".

## 1. State machines (summary; details in 17 and 18)

* **Order:** `PendingPayment -> Paid | RefundRequired | Cancelled | Expired`, late success can move `Cancelled`/`Expired -> Paid | RefundRequired`,
  `RefundRequired -> Refunded`. `Paid` and `Refunded` are final and `RefundRequired` may only become `Refunded` (trigger).
* **Payment attempt:** `Initiated -> Succeeded | Failed | Cancelled`, `Cancelled -> Succeeded` (late success). `Failed` is never rewritten: a
  verified success for a `Failed` attempt becomes a **payment exception** instead.
* **Payment exception:** `Open -> Resolved` (administrator statement). Facts immutable, `Resolved` final, no delete (trigger).
* **Payment anomaly:** `Open -> Resolved` (administrator statement). Facts immutable, `Resolved` final, no delete (trigger). It is evidence only
  and changes no order, attempt or refund (document 21).
* **Every one of the above** (orders, attempts, refunds, exceptions, anomalies) writes an append-only row to `payments.financial_transitions` in the
  same transaction (document 20).
* **Refund:** `Requested -> Unknown -> Pending | Succeeded | Failed | NeedsReview`, `Failed|NeedsReview|Unknown|Pending -> Requested|Unknown`
  by administrator retry, `NeedsReview -> ManuallyRecorded` (administrator statement, providers without API refunds only).
  `Succeeded` and `ManuallyRecorded` are final (trigger).
* **Reconciliation task / outbox event:** `Pending -> Processing -> Done | Pending (backoff) | NeedsReview`, `NeedsReview -> Pending` by administrator.

## 2. Provider capabilities (as implemented, not as hoped)

| Capability | Stripe | Paymob | Simulator |
| --- | --- | --- | --- |
| Hosted checkout | Checkout Session | Intention + Unified Checkout redirect | none (development only) |
| Idempotent initiation | yes: `Idempotency-Key` per attempt | **no**: the provider is called **at most once per attempt**; an ambiguous answer closes the attempt (`initiation_uncertain`) and the customer starts a new one; a payment that still arrives for the closed attempt is a payment anomaly (document 22). Duplicates at the provider are *not* claimed impossible | n/a |
| Webhook authenticity | `Stripe-Signature` over the raw body, 5 minute timestamp tolerance, constant-time compare | HMAC-SHA512 over the 20 documented fields (`hmac` query parameter) | n/a |
| Success event | `checkout.session.completed` / `async_payment_succeeded` with `payment_status = paid` | captured transaction only (not pending, error, voided, refunded, child) | outcome endpoint |
| Failure event | `checkout.session.expired` | **none**: every non-captured callback is ignored | "fail" outcome |
| Payment status lookup | yes (`SupportsPaymentLookup`), amount, currency and reference verified | **no** | n/a |
| API refunds | yes, idempotent per submission, found again by metadata (not by key, keys expire) | **no**: refunds are made in the dashboard and recorded by an administrator | simulated |
| Evidence | U for adapters against fake HTTP; **P: none** | same | U |

Consequences: a Paymob payment can only be completed by a verified callback or by an operator; an ambiguous Paymob state stays unresolved.
A manual record is only allowed where `SupportsRefunds` is false, so it cannot be used to bypass an automatic refund.

## 3. Invariant matrix

"Enforcing layer" lists every layer that contributes. "Evidence" is limited to what actually ran or was reviewed.

| # | Invariant | Enforcing layer(s) | Evidence | Remaining uncertainty |
| --- | --- | --- | --- | --- |
| 1 | One order never receives two independently applied successful payments | DB: one `Initiated` attempt per order (partial unique index), `Paid` final (trigger), conditional finalisation `UPDATE`. Service: order row lock in payment start and verdict recording; a verified success for a `Failed` attempt is an exception (never applied); an order with any exception can never start another payment; an attempt already in flight is withdrawn when the exception is created | U (webhook, harness and in-memory store rules), C (`ConcurrentIdenticalSuccessWebhooks`, `PaymentStart_RacingALateSuccess`, `LateSuccess_WithdrawsAnAttemptAlreadyInFlight`), S | A payment page the customer already has open at the provider cannot be closed by this application; if it is paid the exception shows the order as paid by a different payment (an additional charge) and an administrator refunds the other one. Unproven against PostgreSQL |
| 2 | No duplicate refund transfer by retry or concurrency | DB: one refund per order (unique index), terminal states final (trigger). Service: intent persisted as `Unknown` before the provider call, lookup before any resubmission, same provider idempotency key across ambiguous retries, new key only after a definitive refusal, `FOR UPDATE SKIP LOCKED` claim with lease, administrator retry only from listed states | U (workflow tests with scripted providers, Stripe adapter classification of ambiguous answers), C (`ConcurrentWorkers_RefundExactlyOnce`, `RetryAndManualCompletion_Contending...`, `ConcurrentAdministratorRefundRequests...`) | Stripe sandbox behaviour (P) unverified. Stripe keys expire after 24 h; mitigated because every refund is found again by metadata before a resubmission |
| 3 | No ticket unless the order is validly paid | DB: ticket insert trigger requires a `Paid` order, tickets append-only, unique `(order, seat)`. Service: `IssueTickets` reads the order `FOR SHARE` and requires `Paid` | C (`NoTickets_ForUnpaidCancelledOrRefundPendingOrders`, exception tests assert zero tickets), S | none beyond the unrun database |
| 4 | No inventory oversold | Reservation module: row lock plus database-clock expiry decision on sale confirmation, seat ownership and status constraints | C (existing reservation/checkout suites), S | outside the payment code; not re-audited here |
| 5 | A verified financial fact is not silently discarded because it conflicts with local state | Verified success for a `Failed` attempt: durable exception in the same transaction as the verdict check, acknowledged (200) only after commit, a database failure surfaces as 5xx so the provider redelivers. A signature-verified **success** whose amount/currency/reference/association disagrees with the order is kept as a **payment anomaly** (untrusted received values apart from trusted expected values, deduplicated per provider event, minimal and sanitized) before the event is acknowledged (`200 recorded_for_review`, so the provider does not keep retrying an event that is on record); if it cannot be stored the webhook is **not** acknowledged (5xx) and the provider redelivers - the full matrix is document 21, section 4b | U (webhook and anomaly tests), C (`WebhookRedelivery_AfterAForcedDatabaseFailure`, `LateSuccess_*`, `AMismatchingEvent_*`, `AMismatchWebhook_IsNotAcknowledged...`) | Failure events and invalid signatures are not recorded (no money / unauthenticated); a wrong-merchant-context event stays a 400 on purpose. The anomaly needs an administrator; there is no push notification, but the durable count and age, structured log events and a throttled backlog reminder exist (document 26). Paymob's retry behaviour is undocumented (P). Unproven against PostgreSQL |
| 6 | A conflicting payment is not treated as fulfilment | Exceptions never finalise: no `Paid`, no outbox, no ticket, no reservation sale, no refund record | U, C | the customer cannot be fulfilled from such a payment at all (limitation, see 18) |
| 7 | An ambiguous outcome is neither success nor failure | Adapters throw on ambiguous answers; payment attempts stay `Initiated`; refunds stay `Unknown` and are looked up; Paymob "unknown" stays pending until a verified callback or an operator | U | provider behaviour (P) |
| 8 | Administrator statements stay distinguishable from provider-confirmed results | Separate states (`ManuallyRecorded`, exception `Resolved`), separate columns, CHECK constraints, triggers, audit text "NOT provider-verified", labels in API and console | U (`ManualRefund*`, `PaymentExceptionOperations`, Angular specs), C (trigger/constraint tests), SQL-syntax | visual review (B) not done |
| 9 | An exception prevents unsafe new payment attempts | DB read under the order lock in `BeginPaymentAsync` (any exception, open or resolved); in-flight attempt withdrawn. `payment-options` answers `eligible = false`, reason `OrderHeldForReview`, with **no providers**, and the start is refused `409` consistently (document 22) | U (`PaymentEligibilityTests`, in-memory rule, Angular specs), C | a payment page the customer already has open at the provider cannot be closed by this application (see 1) |
| 10 | Duplicate events and retries are idempotent | Unique indexes (`(attempt, kind)`, `(type, aggregate)`, `(kind, subject)`, `(order, seat)`), `ON CONFLICT DO NOTHING`, conditional updates, row locks | U, C | unproven against PostgreSQL |
| 11 | Customers cannot read or change other customers' financial records | Authorization policies, ownership predicate (`customer_id`) in every customer query, 404 for foreign ids, antiforgery on writes | U (route sweep over every discovered route: 401 anonymous, 403 customer on admin routes, 403 without token), C (ownership of orders, tickets and payment options) | admin read endpoints return no customer identifiers |
| 12 | Workers cannot overwrite terminal or manually resolved states | Every worker `UPDATE` names its source states; triggers reject changes to `Succeeded`, `ManuallyRecorded`, resolved exceptions, `Paid`, `Refunded` | C (`TheWorkerNeverTouchesIt...`, `AWorkerSkipsARefundThatIsLocked...`), S | unproven against PostgreSQL |
| 13 | Every consequential state change is traceable | Append-only `payments.financial_transitions`, written by a trigger in the **same transaction** as the change, for orders, attempts, refunds, exceptions and anomalies: entity, previous and new state, source, **fact basis** (provider-verified / application decision / administrator action / administrator statement), provider, provider event id, actor, correlation id, stable reason; refused conflicting events once per provider event. Administrator actions also keep `admin_audit_log`. A duplicate webhook adds no row (the update does not fire the trigger); a rolled-back change leaves none; a history failure fails the change (webhook not acknowledged) | U (context, labelling), C (`TransitionHistory_*`), SQL-syntax, S | History starts at migration time and is never backfilled (the History page flags entities whose history starts mid-life); the trigger does not stop the table owner, a superuser or `TRUNCATE` (document 24); no retention period is defined and none may be invented in code (document 25). Unproven against PostgreSQL |
| 14 | Amounts and currencies come from trusted server records | Order total from the reservation snapshot; refund and exception amount/currency copied from the order inside SQL; webhook amount compared in integer minor units; client bodies carry only echoes that are verified | U | none |
| 15 | Production cannot use the simulator | `PaymentsOptions` **fails closed**: the simulator is allowed in `Development` with the flag alone, in `Testing` **only with the explicit opt-in** `Payments:Simulator:AllowInTestingEnvironment=true`, and in no other environment (any other, unset or misspelt name fails startup); an unparsable value fails startup instead of being read as on/off; it **cannot be enabled beside a real provider in any environment**; a misconfigured real provider is never replaced by it; the endpoint is not mapped otherwise (document 23, section 4) | U (`ProviderConfigurationTests`, executed Docker-free integration-project configuration tests) | `Testing` still relaxes cookie, key-persistence and forwarded-header defaults (unchanged: the integration suite needs it), so a production host must still never run under that name; disabling the simulator proves nothing about a real provider (P) |
| 16 | A valid signature does not make an event's contents trusted | Webhook service checks attempt, provider, order, reference, amount and currency against OUR records before anything is applied; mismatches become anomalies (never payments) | U | the contents of a *correct* event are still only as good as the provider's account configuration (P) |
| 17 | The client address used for limits is not chosen by the client | `Security:ForwardedHeaders:Mode` is mandatory outside Development/Testing; `Proxy` trusts only listed proxies/networks, nearest hop only by default, catch-all refused; forwarded headers are processed before the limiter; the financial limit is per signed-in user | U (`ForwardedHeadersTests`, `FinancialEndpointSecurityTests`) | never exercised behind a real proxy; webhook rate limiting is deliberately left to the proxy |

## 4. Database responsibilities

| Object | Enforces |
| --- | --- |
| `uq_payment_attempts_one_initiated_per_order`, `uq_payment_attempts_provider_reference`, idempotency-key uniqueness | one in-flight attempt, no shared provider reference, replay by key |
| triggers on `orders` | immutable price snapshot, `Paid`/`Refunded` final, `RefundRequired` only to `Refunded`, no delete |
| `uq_refunds_order`, refund insert/update triggers | one refund per order matching the order's payment, amount and currency; identity immutable; `Succeeded` and `ManuallyRecorded` final |
| `chk_refunds_manual_fields`, `chk_refunds_state` | a manual record is all-or-nothing and only in its state |
| `payment_exceptions` constraints and triggers | kind/state/resolution values, resolution all-or-nothing, one per `(attempt, kind)`, must name a `Failed` attempt of its own order, facts immutable, `Resolved` final, no delete |
| `trg_payment_attempts_keep_failed_verdict` | the `Failed` verdict of an attempt that has an exception cannot change |
| `payment_anomalies` constraints and triggers | kind/state/resolution values, resolution all-or-nothing, currency format, association only for the two correlated kinds and only to a real attempt of that order and provider, facts immutable, `Resolved` final, no delete, starts `Open`, one per `(kind, dedupe_key)` |
| `financial_transitions` trigger function, eight record triggers (plus two for anomalies), append-only trigger, `uq_financial_transitions_rejected_event` | one row per real status change, in the same transaction; no update or delete; one `Rejected` row per provider event |
| `tickets` triggers/indexes | only for `Paid` orders, append-only, unique per seat |
| `admin_audit_log` trigger | append-only |

All of this is **SQL-syntax checked and S-reviewed; none of it has run**.

## 5. Concurrency: what is and is not established

| Scenario | Test | What it controls | Status |
| --- | --- | --- | --- |
| Many identical success webhooks | `ConcurrentIdenticalSuccessWebhooks_...` | started together; **interleaving not controlled** | C |
| Many identical late-success deliveries | `LateSuccess_RepeatedAndRacing_CreatesExactlyOneException` | started together; interleaving not controlled; asserts one row | C |
| Payment start racing a late success | `PaymentStart_RacingALateSuccess_...` | both callers provably waiting on the order row (via `pg_stat_activity`) before release; **wake order not controlled**, so it asserts the invariants for either order and branch-specific rows | C |
| Resolution in flight while the provider redelivers | `ResolutionInFlight_AndARepeatedLateSuccess_...` | resolution provably queued behind a lock on the exception row while a redelivery is processed | C |
| Redelivery after a forced database failure | `WebhookRedelivery_AfterAForcedDatabaseFailure_...` | deterministic: a failing trigger is installed, the request must not be acknowledged, the trigger is removed, the redelivery is recorded | C |
| Success versus expiry | `SuccessAndExpiredEvent_BothContendingForTheOrder_...`, `ASuccessRacingASessionExpired_...` | contention observed in the first; the second only repeats rounds | C |
| Retry versus manual refund | `RetryAndManualCompletion_Contending...` | both waiting on the refund row; either outcome asserted | C |
| Worker versus locked refund | `AWorkerSkipsARefundThatIsLockedByAnAdministrator_...` | deterministic: `SKIP LOCKED` observed, then sequential steps | C |
| Concurrent administrator refund requests | `ConcurrentAdministratorRefundRequests_...` | both waiting on the order row | C |
| Concurrent resolutions / manual completions | `Resolving...Concurrently...`, `ManualRefund_ConcurrentSubmissions_...` | started together; interleaving not controlled | C |
| Redelivered mismatch events / history rows | `AMismatchingEvent_IsKeptAsEvidence_...`, `TransitionHistory_IsNotDuplicated_ByRedeliveredEvents`, `TransitionHistory_KeepsAConflictingEventOnRecord_OncePerProviderEvent` | sequential redeliveries and a conflicting event; the unique-index arbitration under true concurrency is **not** exercised | C |

No test in this list has been executed.

## 6. Reconciliation and retry behaviour

Discovery is idempotent (`ON CONFLICT DO NOTHING` on `(kind, subject)`); work is claimed with `FOR UPDATE SKIP LOCKED` and a lease
(default 2 minutes); retries back off from 30 s doubling to 1 h; after 8 attempts (or repeated lease expiry) an item becomes `NeedsReview` and
waits for an administrator. A task, refund or event that is `NeedsReview` is never retried by a worker. There is deliberately **no worker for
payment exceptions**: they need a person, and creation is atomic with the verdict.

## 7. Required configuration

| Setting | Purpose | Note |
| --- | --- | --- |
| `ConnectionStrings:Catalog` | database | the legacy `PULSERESERVE_TEST_DATABASE` variable is only a fallback; do not set it on a deployed host |
| `DataProtection:KeysPath` | persisted cookie keys | required outside Development/Testing |
| `Identity:AdminUserIds` | the only way to be an administrator | empty means nobody |
| `Payments:Workers:Enabled` / `IntervalSeconds`, `Payments:Workflow:*` | background work | validated at startup |
| `Payments:Simulator:Enabled`, `Notifications:DevAdapter:Enabled` | development only | the simulator is `Development`-only, plus `Testing` with `Payments:Simulator:AllowInTestingEnvironment=true`; startup fails elsewhere (document 23, section 4, lists the variables per scenario) |
| `Security:FinancialWritesPerMinute` | per signed-in user limit on state-changing financial requests (default 60) | webhooks and reads are not limited by the application |
| `Security:ForwardedHeaders:Mode` (+ `KnownProxies`, `KnownNetworks`, `ForwardLimit`) | `Direct` or `Proxy` | **required outside Development/Testing: the host will not start without it** (document 23) |
| Stripe / Paymob keys, webhook secrets, base URLs, currencies | see document 16 | secrets only through configuration/secret stores; never logged |

## 8. Security and log redaction

* Webhooks are anonymous by nature: signature on the raw body first, 256 KiB cap, answers carry reason codes only, no cookies or antiforgery.
* The Paymob signature travels in the query string. The application removes it before the hosting layer logs the request
  (`WebhookRequestRedaction`). **Not under application control:** reverse-proxy, CDN and load-balancer access logs will still contain
  `?hmac=...` unless configured to drop or mask the query of `/api/webhooks/*`.
* Request bodies and signature headers are never logged. Logs carry ids and outcomes only (U: `WebhookHandling_LogsIdsAndOutcomesOnly...`).
* Outside Development an unexpected exception answers `500 Server.Error` with no detail (U), and the failure is logged by the middleware.
* Per-user rate limit on financial writes (U). Forwarded headers are now processed by the application in `Proxy` mode, from **listed proxies only**
  (document 23); the sign-in limiter is per real client address behind a trusted proxy (U). TLS termination, HSTS, host restriction and a
  webhook-path rate limit or allow-list still belong to the proxy, as does masking `?hmac=` in every log in front of the application.
* Endpoint filters (the antiforgery check) run after parameter binding, so an authenticated caller without a token and with an unparsable body
  gets 400 instead of 403; nothing runs either way (U).

## 9. Operations runbook

### 9.1 A late-success exception ("Payment exceptions" page, overview tile)

What it means: the provider reported (and this application verified) a successful payment for an attempt that this system had already recorded
as `Failed`. The `Failed` verdict is kept, the order is **not** paid, no tickets exist, no refund exists, and the order can never be paid again.

1. Open **Payment exceptions**, filter **Needs an administrator**. Note provider, amount, order id, attempt id, the local verdict and the order's status.
2. In the provider dashboard find the payment by the **attempt id** (Stripe: the Checkout Session's `client_reference_id`/metadata; Paymob: the
   merchant order id). Confirm that it is **captured**, the amount and currency match the row, and it is not already refunded or disputed.
   If it is not found or not captured, **do not record anything**: leave the exception open and investigate (there is no "dismiss" action).
3. Decide with the customer: refund (the supported path). If the order is shown as *paid by a different payment* the charge is an additional
   charge and must be refunded in full.
4. Make the refund **in the provider dashboard**. Copy its reference.
5. Verify the refund: it appears in the dashboard with the full amount and a succeeded or pending state; for card refunds note that pending
   is not the same as settled.
6. Only then use **Record refund**: confirm the statement, enter the reference, submit. This stores your statement; nothing is sent to the
   provider and the provider has not confirmed it to this application. The exception becomes *Resolved (administrator statement)* and the audit
   log gets `ResolvePaymentException`.
7. The customer starts a new order. Reconcile the statement against the provider's settlement report; the application never does.

### 9.2 A refund waiting for review at a provider without API refunds (Paymob)

Same discipline: refund in the dashboard, verify the refund, then **Record manual refund** (amount shown read-only, reference optional, explicit
confirmation). The refund becomes *Refunded (manually recorded)*, the order becomes `Refunded`, the audit log gets `RecordManualRefund`.
Do not use Retry for such a refund (the console hides it): it would only return to review.

### 9.3 A payment anomaly ("Anomalies" page, overview tile)

What it means: a provider event that carried a **valid signature** disagreed with this system's records (wrong amount or currency, a reference
that does not match the attempt, an attempt this system does not know, or an inconsistent attempt/order). **Nothing was applied**: the order is not
paid, no ticket exists, no refund was made or created, and any recorded verdict is unchanged. See document 21.

1. Open **Anomalies**, filter **Needs an administrator**. Read the left column as *what the event claimed (untrusted)* and the middle one as *what
   our records say*.
2. Find the payment in the provider dashboard by the **event id** shown in the row (Stripe: Developers -> Events; Paymob: Transactions). Confirm
   whether it was captured, for which amount and currency, in which account/environment, and whether it was already refunded or disputed.
3. If it is a real charge of the customer's money: refund it in the dashboard, verify the refund, then record **Money returned outside this
   system**. If it does not belong to this system or environment: **Not our payment**. If it was reversed or needs nothing: **No action needed**.
4. If the payment **cannot be found or understood, record nothing** and leave the anomaly open. There is no dismiss action.
5. Record the outcome: choose the statement, add a reference, tick that you compared the event with the dashboard. This stores your statement
   (administrator, time, text); the provider is not called, nothing about the order changes, and the provider has not confirmed it.
6. The event was acknowledged (`200`) once its evidence was stored, so the provider normally stops sending it; if it sends it again, no new row appears
   (deduplicated by event id) and the count does not grow. Operational signals for a growing backlog are in document 26.

### 9.4 Reconstructing what happened to an order ("History" page)

Open **History**, enter the order id (or follow the link from an exception/anomaly). Each row says what moved, from which state to which, **who
or what** moved it (provider webhook, status lookup, worker, administrator, customer, simulator), on **what basis** (provider-verified fact,
application decision, administrator action, administrator statement) and the provider event id and actor where they exist. A *Refused* row is a
provider event that conflicted with the recorded outcome and changed nothing. Group the rows of one request by correlation id with SQL
(document 20, section 5). History does not exist for changes made before the migration was applied.

### 9.5 Other items needing a person

`Needs review` tasks and events (re-queue after checking the cause); refunds the provider refused (retry creates a new submission, safe because the
provider declined); open exceptions and refunds are counted on the overview.

## 10. Migration and rollback risks

| Migration | Forward | Rollback |
| --- | --- | --- |
| `...InitialPayments`, `...ReconciliationRefundsOutboxTickets` | create the payment schema, triggers and indexes | destroy the payment schema's tables (not reviewed in this pass beyond SQL syntax) |
| `...AttemptCheckoutUrl` | adds a nullable column | drops it (stored hosted-page links are lost; harmless) |
| `20261022112035_ManualRefundRecord` | four nullable columns, widened state check, `chk_refunds_manual_fields`, replaced update trigger. Existing rows stay valid | **refuses** while any `ManuallyRecorded` refund exists (explicit check, surrounding transaction changes nothing) |
| `20261022140828_LateSuccessPaymentExceptions` | new table, constraints, indexes, three triggers (one on `payment_attempts`). Reads and changes no existing row | **refuses** while any exception exists (explicit check); drop the table by hand after exporting if the rollback is really intended |
| `20261022151129_FinancialTransitionHistory` | new table, three indexes, one function, eight record triggers (orders, attempts, refunds, exceptions: insert + status change) and an append-only trigger. Reads and changes no existing row; **no backfill** | **refuses** while any history row exists (explicit check, `23000`) |
| `20261022152016_PaymentAnomalies` | new table (23 columns, 7 CHECKs, 2 foreign keys, 4 indexes), update/insert guards, two history triggers. Reads and changes no existing row | **refuses** while any anomaly exists (explicit check, `23000`) |

All six rollback guards (every migration whose `Down` drops a table now refuses while records exist - document 24, section 7), the whole forward script and every SQL literal of the two financial stores were syntax-parsed offline (the counts are in
the final verification report). **None was applied to a database**, so lock duration on a populated table, trigger behaviour and rollback behaviour
are unverified. The `payment_attempts` keep-failed-verdict trigger fires on every attempt update but acts only when an exception exists; the
history triggers fire only when the status/state value changes.

### Deployment sequence and rollback precautions

1. **Before** deploying the new application set `Security:ForwardedHeaders:Mode` (and proxies/networks) on every host: the new application refuses
   to start without it outside Development/Testing (document 23). Review `Security:FinancialWritesPerMinute`.
2. **Apply the four migrations before the application** (`dotnet ef database update` or an idempotent script, in the order above). They are additive
   and backwards compatible: instances still running the previous version keep working (they write history rows with source `Unspecified`, and never
   touch the anomaly table), so a rolling deployment is possible.
3. Lock behaviour (unmeasured): `ManualRefundRecord` replaces `chk_refunds_state` (`DROP` + `ADD CONSTRAINT`, which validates every existing refund
   row under a table lock; fine for a small table, check `refunds` size first); `CREATE TRIGGER` on `orders`, `payment_attempts`, `refunds` and
   `payment_exceptions` briefly takes a `SHARE ROW EXCLUSIVE` lock on each. Apply in a quiet period and set a statement/lock timeout.
4. **Application rollback** is safe: leave the schema in place; the older application ignores the new tables.
5. **Database rollback** is deliberately obstructed once any manual refund, exception, history row or anomaly exists: each `Down` raises before
   changing anything. Export the table (or take a backup), drop the table by hand, then run the rollback again. Nothing in the migrations deletes
   financial records.
6. Take a backup before the first run on a populated database and rehearse steps 2-5 on a restored copy: **none of this has been executed**.

## 11. Production-readiness checklist (all open unless stated)

1. Run the whole integration suite on a disposable PostgreSQL (Docker); read failures before anything else. Prerequisites, isolation safeguards and exact
   commands: document 27.
2. Apply the migrations to a disposable copy of a populated database; run both rollback guards with and without rows.
3. Run the compiled race and trigger tests listed in section 5 and the missing ones in section 12.
4. Provider sandboxes: one full Stripe and one full Paymob payment with webhook delivery; a Stripe refund; confirm Paymob callback field names and the HMAC
   against a real callback; confirm that no real event can arrive for an expired Stripe session as a success.
5. Reverse proxy / CDN: mask `?hmac=` in access logs, set the application's `Security:ForwardedHeaders:*` to the real topology (document 23) and make the
   proxy overwrite client-supplied `X-Forwarded-*`, terminate TLS, HSTS, restrict hosts, rate-limit or allow-list `/api/webhooks/*`.
6. Monitoring (document 26): alert on log events 7103 (evidence not stored, persisting) and 7105 (order data unusable), review 7110 (unresolved
   backlog reminder), watch `oldestOpenPaymentAnomalyAgeSeconds` and `NeedsReview` counts, and 5xx on `/api/webhooks/*` (a 5xx there means the evidence
   or the verdict could not be stored and the provider will redeliver). None of this has been wired into a real monitoring system.
7. Confirm `ASPNETCORE_ENVIRONMENT` is not `Testing`/`Development` in production and that **no** `Payments:Simulator:*` setting is present.
8. Browser review of the financial admin pages in both themes and at phone, tablet and desktop widths (not done in this work).
9. Create the least-privilege application role and run the verification queries (document 24, sections 4 and 6), and get the retention decisions
   D1-D7 answered by their owners (document 25). Until then the safe default stands: keep everything, delete nothing. (The per-transition history and
   the customer-facing explanation of a blocked order are implemented.)
10. Rehearse section 9.1 end to end with a sandbox payment.

## 12. PostgreSQL scenarios that no test covers at all (not even compiled)

The compiled PostgreSQL tests are listed, with their concerns, in document 27 (206 methods in the integration project, 183 of them needing Docker).
Existing-row migration compatibility, every rollback guard, the anomaly constraints, the history trigger's main paths and the lock-barrier races
are among them. What follows has **no** test:

* Worker versus manual completion with a refund in `Unknown` while the worker holds a lease that then expires; lease expiry while an administrator
  resolves an item; `NeedsReview` escalation after repeated lease expiry.
* A success webhook racing the customer's *cancel* of the order.
* Two exceptions on one order (two failed attempts) and the order view for both.
* The `payment_attempts` keep-failed-verdict trigger under concurrent updates.
* History: `set_config(..., true)` isolation under connection pooling (two concurrent requests with different contexts never see each other's values);
  a change made by hand SQL with no application scope is recorded as `Unspecified`.
* True parallel arbitration of `uq_payment_anomalies_kind_dedupe` and `uq_financial_transitions_rejected_event` by many simultaneous deliveries of
  one event (the compiled redelivery tests are sequential; the start-together tests do not control the interleaving).
* The order of statements in `OrderStore.BeginPaymentAsync` when an exception is inserted between its check and its insert (the order row lock
  should serialise them; prove it).
* Everything under the recommended least-privilege role (document 24): the suite runs as the container's superuser.
* The duration of `CREATE TRIGGER` and constraint replacement on a populated copy of a real database.
* Not prevented by design and therefore untested: `TRUNCATE`, owner or superuser bypass of the append-only triggers.

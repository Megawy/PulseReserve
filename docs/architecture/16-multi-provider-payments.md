# 16. Multi-provider payments: Stripe and Paymob

PulseReserve can take payments through **Stripe** (hosted Checkout) and **Paymob** (Unified Checkout redirect) next to the
development/test simulator. Both feed the *existing* payment lifecycle (doc 14): orders, attempts, verified completion,
sale confirmation, `RefundRequired`, refunds, reconciliation, outbox, tickets and the admin console. There is no parallel
lifecycle and no new state.

> **Later documents.** Late success after a failure: 18. Durable per-transition history: 20. Payment anomalies (valid signature, wrong contents):
> 21. Order eligibility and Paymob's at-most-once initiation: 22. Forwarded headers, rate limits, simulator isolation and `?hmac=` log
> redaction: 23. Invariants, runbook, migrations and deployment sequence: 19. Where this document disagrees with them, they win.

> **Verification status.** Both adapters are verified **against fake HTTP handlers and documented fixtures only**.
> No Stripe or Paymob account, key or sandbox was used, no real charge or refund was made, and nothing here has been
> exercised against a live or sandbox provider. Do not describe either integration as live until the checklist in
> "Before going live" has been done with real sandbox credentials.

## How it fits the existing architecture

| Concern | Where |
|---|---|
| Provider abstraction | `IPaymentProvider` (extended with `SupportsCurrency`, `SupportsPaymentLookup`, `SupportsRefunds`, query-style lookups; all with defaults so the simulator and test doubles are unchanged) |
| Which providers exist | `IPaymentProviderRegistry` (built from every registered `IPaymentProvider`; `Features/PaymentProviderRegistry.cs`) |
| Attempt row | already had `provider` + `provider_reference` (unique per provider); one migration adds nullable `checkout_url` (`AttemptCheckoutUrl`, additive, historic rows untouched) |
| Starting a payment | `PaymentService.InitiateAsync(customer, order, Idempotency-Key, providerName?)` |
| Verified result | provider adapter `Parse(WebhookRequest)` -> `PaymentWebhookService` -> `PaymentService.CompleteAsync` (the same path the simulator uses) |
| Ambiguity / recovery | `ReconciliationService` (status lookup through the attempt's own provider), `RefundService` (refund through the refund's own provider) |
| Adapters | `Providers/Stripe/StripePaymentProvider.cs`, `Providers/Paymob/PaymobPaymentProvider.cs` (plain `HttpClient`, **no SDK dependency**) |
| Configuration | `Providers/ProviderConfiguration.cs` (validation) + `AppHost/Composition/PaymentProvidersComposition.cs` (registration) |
| HTTP | `POST /api/webhooks/{stripe|paymob}` (anonymous by nature, signature-verified), `GET /api/orders/{id}/payment-options`, `POST /api/orders/{id}/payments` now accepts `{ "provider": "<name>" }` |

## Provider selection (server-controlled)

* The customer can only **name** a provider; amount, currency, URLs and account never come from the client.
* `GET /api/orders/{id}/payment-options` lists enabled providers that can charge the order's currency. An empty list means
  "payments unavailable" (the UI says so and shows no payment button).
* One provider enabled -> it is the default; several -> the request must name one (`Payment.ProviderRequired`). Unknown,
  disabled or currency-incapable names are refused (`Payment.ProviderNotEnabled`, `Payment.CurrencyNotSupported`).
* The provider is stored on the attempt **before** the provider is called, together with the idempotency key. A replay with
  the same key returns the same attempt, provider, reference and checkout URL; naming a different provider on a replay is a
  conflict (`Payment.ProviderMismatch`). One in-flight attempt per order is still enforced by PostgreSQL, so a second
  provider cannot be started while the first is unresolved.
* **No failover after uncertainty.** A timeout, connection loss or unreadable answer from the provider returns
  `Payment.ProviderTemporarilyUnavailable` (503) and leaves the attempt in place; the client retries with the **same** key.
  Only a *definitive* refusal (HTTP 400/401/403/404 from the provider, an unsupported currency, an unrepresentable amount)
  cancels the unreferenced attempt (`Payment.ProviderRejected`) so another provider may then be chosen.
* Refunds and reconciliation always use the provider recorded on the refund/attempt (`registry.Find(name)`), even if that
  provider is no longer *enabled*; a provider that is no longer configured at all defers the work instead of routing it
  elsewhere.

## Webhooks and verified completion

The browser redirect is **never** evidence of payment: the return page only reads the order from the API.

`PaymentWebhookService` accepts an event only after the adapter proved authenticity (signature, timestamp, merchant
context), then cross-checks it against PulseReserve's own records:

1. the attempt exists and belongs to **this** provider (a Stripe event can never complete a Paymob attempt, or vice versa);
2. the event's order (if it names one) is the attempt's order;
3. the provider reference equals the stored one (checked in the store, in the same transaction that records the verdict);
4. for a success: the amount **and** currency equal the persisted order total (integer minor units, no rounding).

Anything that disagrees changes nothing (the attempt stays unresolved, an error is logged with ids only). A verified success
then goes through `PaymentService.CompleteAsync`, so: duplicates are idempotent (one `Paid` transition, one outbox pair, one
ticket per seat), a success after the reservation expired ends as `RefundRequired` (never `Paid`, no tickets), a late success
for a cancelled attempt is still recorded, and a verdict that contradicts an already recorded one is **not** overwritten:
a *failure* after a recorded success is acknowledged and logged (the success stands), while a verified *success* after a
recorded failure keeps the `Failed` verdict and becomes a durable payment exception for an administrator
([18](18-late-payment-success-exceptions.md); the order is never paid by it). HTTP mapping (the full matrix, with the reasoning, is in [21](21-payment-anomalies.md), section 4b): applied, acknowledged, or
**durably recorded as an anomaly** -> 200 (a verified event whose evidence is stored must not be retried again); not authentic, unreadable or of
another merchant context -> 400 with a reason code only (so the provider dashboard shows the failing delivery); the evidence could not be stored,
or our own order data is unusable -> 5xx (so the provider delivers it again); provider not enabled -> 503; unknown provider segment -> 404;
body > 256 KiB -> 413.

### Stripe (implemented)

Documentation used: <https://docs.stripe.com/api/checkout/sessions/create>, <https://docs.stripe.com/api/refunds/create>,
<https://docs.stripe.com/webhooks>.

* **Initiation**: `POST /v1/checkout/sessions` (form-encoded, `Authorization: Bearer <secret>`), `mode=payment`, one line item
  with `unit_amount` = order total in minor units, `client_reference_id` = attempt id, metadata (attempt/order ids on the
  session and the PaymentIntent), `success_url`/`cancel_url` under the configured return URL, `expires_at` = +31 minutes (the
  documented minimum is 30). `Idempotency-Key: pulsereserve-attempt-<attempt id>` makes a retry after a lost answer return the
  same session. The response must echo our reference, amount and currency and carry an `https` URL, otherwise nothing is
  shown to the customer. Persisted: session id (`cs_...`) as the provider reference and the hosted URL.
* **Result**: `checkout.session.completed` / `checkout.session.async_payment_succeeded` with `payment_status = paid` = success
  (a completed-but-`unpaid` session for a delayed method is ignored until `async_payment_succeeded`);
  `checkout.session.expired` = the attempt failed (the only failure the webhook records).
* **Signature**: `Stripe-Signature` verified manually exactly as documented (`t=` timestamp, `v1=` HMAC-SHA256 of
  `"{t}.{rawBody}"` with the endpoint secret, other schemes ignored, constant-time comparison, 5-minute tolerance, rolled
  secrets give several `v1` values). `livemode` must match the configured mode; events of a connected account are refused.
* **Status lookup**: `GET /v1/checkout/sessions/{id}`; `complete`+`paid` = succeeded, `open`/unpaid-complete = pending,
  `expired` = failed, 404 = not found. Reference, amount and currency are verified; a mismatch throws (unresolved).
* **Refunds**: the session is read for its `payment_intent`; `POST /v1/refunds` with `payment_intent`, `amount` and metadata
  (`refund_id`, `idempotency_key`), `Idempotency-Key = <refund idempotency key>`. `succeeded` = complete; `pending`/
  `requires_action` = pending; `failed`/`canceled` = rejected; 5xx/429/`charge_already_refunded`/timeouts are *ambiguous* and
  throw so the worker **asks first**. **Refund lookup**: Stripe cannot find a refund by idempotency key (and keys expire after
  24 h), so refunds are found by listing the payment's refunds and matching the `idempotency_key` + `refund_id` metadata; an
  incomplete (`has_more`) list is inconclusive, not "not found".

### Paymob (partially implemented, on purpose)

Documentation used: <https://developers.paymob.com/> (Create Intention, Unified Checkout redirection, HMAC transaction
callback and its published sample).

* **Initiation**: `POST {api}/v1/intention/` with `Authorization: Token <secret key>`; body `amount` (cents), `currency`,
  `payment_methods` (configured integration ids), one `items` entry equal to the total (the documented rule), 
  `special_reference` = attempt id (returned on the callback as `order.merchant_order_id`), `notification_url`,
  `redirection_url`. No customer data is sent. The customer is sent to
  `https://eg.checkout.paymob.com/?publicKey=...&clientSecret=...`. The Paymob **order id** (`intention_order_id`) is the
  provider reference (callbacks carry it as `order.id`).
* **Result**: the HMAC-verified *transaction processed* callback. The 20 documented `obj` fields are concatenated in the
  documented order and hashed with HMAC-SHA512 (hex); the unit tests pin the concatenation to the string published with the
  documentation. Only a captured transaction counts as success (`success`, not `pending`, no error, not voided/refunded, no
  parent transaction, not an uncaptured authorization). A *declined* transaction is deliberately **not** turned into a failed
  attempt, because the customer may retry inside the same intention and still pay. The integration id must be one of ours;
  `special_reference` must be an attempt of THIS provider.
* **Not implemented: payment status lookup and API refunds.** The official pages reachable while building this did not
  specify those endpoints for Egypt (and the regional mirrors disagreed on authentication), so none was guessed.
  `SupportsPaymentLookup = false`: an unresolved Paymob attempt stays `Initiated` until a verified callback or an operator
  decides (the stale-attempt task keeps it queued, then abandons it for an unpayable order while still accepting a late
  verified callback). `SupportsRefunds = false`: a refund for a Paymob payment goes straight to **NeedsReview** with
  "refund the payment in the provider dashboard"; the order stays `RefundRequired` and nothing is ever marked complete by
  guessing.
* **Documented gaps to confirm against a real sandbox callback**: where the HMAC arrives (the documentation says the `hmac`
  query parameter; that is what is read), and how `null` source-data fields are written into the signed string (written as
  empty; if Paymob differs the signature will not match and the callback is *rejected*, never accepted).

## Configuration

All settings live under `Payments:Stripe:*` and `Payments:Paymob:*` (environment variables use `__`). Both providers are
**off** unless `Enabled=true`. Placeholders are in `.env.example` (and mapped in `compose.dev.yaml`); real values belong only
in a private `.env` or the deployment's secret store.

| Setting | Stripe | Paymob |
|---|---|---|
| `Enabled` | `Payments:Stripe:Enabled` | `Payments:Paymob:Enabled` |
| `Mode` (**required**) | `Test` or `Live` | `Test` or `Live` |
| secret | `SecretKey` (`sk_test_`/`rk_test_` for Test, `sk_live_`/`rk_live_` for Live) | `SecretKey` (name must not contradict `Mode`) |
| public/other | - | `PublicKey`, `IntegrationIds` (comma-separated) |
| webhook secret | `WebhookSecret` (`whsec_...`) | `HmacSecret` |
| currencies | `AllowedCurrencies` (required, e.g. `usd,eur`) | `AllowedCurrencies` (required, e.g. `egp`) |
| return URL | `ReturnBaseUrl` (https; `http://localhost` only outside Production) | `ReturnBaseUrl` |
| callback host | register `https://<api>/api/webhooks/stripe` in Stripe | `PublicApiBaseUrl` -> `<it>/api/webhooks/paymob` |
| endpoints | `ApiBaseUrl` (override only outside Production) | `ApiBaseUrl`, `CheckoutBaseUrl` (same rule) |

Rules enforced at startup (`ProviderConfigurationValidator`, messages name the setting, **never a value**):
Production accepts only `Live`, every other environment only `Test` (sandbox and live cannot mix); keys must match the mode;
currencies are mandatory (a provider charges nothing until told which currencies it may); URLs must be https. An enabled but
invalid provider is **not created** (payments through it are unavailable, the reasons are logged) and the rest of the
application starts normally. The simulator is unchanged: it still refuses to start outside Development/Testing.

## Customer UI (Angular)

* Checkout lists only the methods returned by `payment-options`, as a labelled radio group (a single method is preselected but
  still shown); no payment button exists while none is offered (explicit "Payments are unavailable" state).
* Starting a real payment redirects to the **server-returned** `https` URL (`Redirector` refuses anything else); "Continue to
  payment" and "Check payment status" remain for a blocked redirect or an in-flight attempt, with a note that returning does
  not confirm anything and cancelling after paying means a refund.
* `/account/orders/:id?payment=return|cancelled` shows the order's **current server status**. After `return` it re-reads the
  order a few times ("Confirming your payment", then a manual "Check now"); it says "Payment confirmed" only when the server
  says `Paid`. The marker is a hint; any other value is ignored.
* Labels: "(simulated)" is shown **only** for simulator payments (`statusView(kind, value, simulated)`); real payments read
  "Paid", "Refund pending", "Refunded". The simulator banner/controls appear only for the simulator.

## Tests

* Unit (no network, no credentials): provider contract tests for both adapters (request shapes, rejection vs ambiguity,
  status/refund mapping, signature and HMAC verification including replay window, tampering, wrong merchant context, duplicate
  deliveries), configuration validation (no secret leaks), registry, multi-provider `PaymentService`, webhook service
  (duplicates, out-of-order, late success after expiry, amount/currency/reference/order mismatch, cross-provider isolation),
  reconciliation/refund workers with several providers, simulator-only-in-Development/Testing.
* Integration (`ProviderPaymentsHttpTests`, real HTTP pipeline + PostgreSQL, fake provider HTTP): written and compiled, **but
  they need Docker (Testcontainers) and were NOT executed in the session that added them** because the Docker engine was
  unavailable. Run them before relying on the database-level claims (single ticket/outbox pair on duplicate webhooks, late
  success -> refund, the `checkout_url` column).
* Angular: provider selection, redirect safety, return handling, honest labelling.

## Logging, request handling and operator visibility (hardening)

* **Webhook query strings are not logged.** ASP.NET Core's hosting layer logs `Request starting ... {path}{query}`
  (`Microsoft.AspNetCore.Hosting.Diagnostics`, Information) *before* any middleware runs, so
  `/api/webhooks/paymob?hmac=<signature>` was written to logs wherever that category is at Information (Development by
  default; production defaults to Warning but is often raised). `WebhookRequestRedaction` replaces `IHttpContextFactory` and
  moves the query of requests under `/api/webhooks` into `HttpContext.Items` at the moment the context is created (before the
  hosting log), where only the webhook endpoint reads it. Other requests and log lines are untouched. Bodies and the
  `Stripe-Signature` header are never logged by the application. Tests: `WebhookLoggingTests` (a characterization test proves
  the leak without redaction; with redaction the signature appears in no line in Development or in Production with framework
  logging raised to Trace, and a valid signed callback still verifies).
* **Not covered:** a reverse proxy or load balancer in front of the API may log full URLs itself; configure it to drop the
  query for `/api/webhooks/*`. The Development exception page (framework behaviour, Development only) can show framework
  exception text for malformed JSON; production returns a bare 400.
* **Request handling.** `PaymentEndpointHttpTests` drives the real endpoints, policies and antiforgery filter in an in-memory
  host: an empty body is fine when one provider is enabled; malformed or wrongly typed JSON is 400, a non-JSON content type
  with a body is 415, an unknown provider is 400 `Payment.ProviderNotEnabled`, a missing/overlong idempotency key is 400,
  no session is 401, no CSRF token is 403 `Csrf.Invalid`, someone else's order is 404 - and none of them creates an attempt or
  changes the order. Extra JSON fields (amount, currency, URLs) are ignored; the charge comes from the order. Webhooks stay
  anonymous and need no CSRF; they are decided by signature, not content type; bodies over 256 KiB are 413 whether or not a
  length is declared; GET is 405; unknown provider segment 404; provider not enabled 503.
* **Operator visibility.** `GET /api/admin/payments/providers` (Admin + antiforgery group, read-only) lists each provider's
  name, simulated/available flags and whether it supports payment lookup and API refunds (no configuration). Reconciliation
  tasks about an attempt now carry its `provider`. The console shows the provider on refunds, orders awaiting a refund and tasks,
  hides "Retry" for a refund that only a person can complete (a provider that cannot refund through the API) and says
  "Refund this payment in the <provider> dashboard first; this app cannot confirm it; once it is done, record it here" (see
  *Manual refund completion* below), adds a callout counting such refunds, and tells the operator where to look for an unresolved attempt of a provider that cannot be queried.
  If the provider list cannot be loaded nothing is hidden and nothing is guessed. **Still not visible:** a Stripe session
  that may be paid after its order was cancelled (no authoritative signal exists; the session expires on its own after
  31 minutes and any late payment appears as an order awaiting a refund).

## Manual refund completion

Paymob refunds cannot be made or looked up through the API, so the worker moves such a refund to `NeedsReview` and a person
refunds the payment in the Paymob dashboard. An administrator can now **record** that it was done.

**State model.** A new terminal refund state `ManuallyRecorded`, deliberately different from `Succeeded` ("the provider
confirmed it"). It is reachable only from `NeedsReview`, only for a provider the registry knows cannot refund through the API
(`SupportsRefunds = false`; an unknown provider is never assumed manual and Stripe/the simulator are refused, so a hand-made
record can never be followed by a second automatic refund), only by an administrator. The refund stays a **full** refund (one per
order, amount and currency copied from the order); no partial refund was introduced. `Requested`/`Unknown`/`Pending` refunds
are not recordable: for a manual-only provider the worker escalates to `NeedsReview` on its next pass, and recording earlier
would race it.

**API.** `POST /api/admin/payments/refunds/{id}/manual-completion` (Admin policy + antiforgery, like every operations route).
Body: `confirmedPerformedExternally` (must be `true`), `amount` and `currency` (an *echo* of what the console showed, verified
against the stored refund, never used as the amount owed), optional `externalReference` (<= 200) and `note` (<= 300). Provider,
order and payment are never read from the request. Answers: `200` the refund (`state: ManuallyRecorded`, plus `manualReference`,
`manualNote`, `manuallyRecordedBy`, `manuallyRecordedAtUtc`); `400` not confirmed / invalid; `404`; `409`
`Operations.ManualRefundNotAllowed` (wrong state or provider), `ManualRefundMismatch` (amount/currency), `ManualRefundConflict`
(already recorded with different details). Nothing is sent to any provider.

**Persistence.** One transaction under `SELECT ... FOR UPDATE` on the refund row: verify state/amount/currency, set the state and
the `manual_*` columns, move the order `RefundRequired -> Refunded`, write the audit row `RecordManualRefund` (actor, refund,
order, provider; the free-text note is deliberately not copied into the log). An identical repeat is an idempotent `200` that
changes and audits nothing; a different repeat is `409` and never overwrites. Concurrent submissions serialise on the row lock.
Migration `20261022112035_ManualRefundRecord` adds `manual_reference`, `manual_note`, `manual_recorded_by`,
`manual_recorded_at_utc`; widens `chk_refunds_state`; adds `chk_refunds_manual_fields` (the four columns are present exactly when
the state is `ManuallyRecorded`); and extends `guard_refund_update` so a manually recorded refund is final (state, provider id
and every `manual_*` column immutable) and `ManuallyRecorded` can only be entered from `NeedsReview`. `Down` restores the
previous constraint and function and therefore **fails while any manual record exists**, on purpose: the record of a refund
made outside the system is not silently discarded. `MarkRefundSucceededAsync` also refuses to overwrite a manual record.

**Console.** For a `NeedsReview` refund of a provider that cannot refund through the API the refunds table shows *Record manual
refund*. The dialog says the refund must already have been made in the provider's dashboard, nothing is sent to the provider
and the provider has not confirmed it, shows the read-only amount, takes an optional reference and note and requires an
explicit checkbox before the button enables. A request in flight disables every control (no double submit); a `409` keeps the
dialog open, says nothing new was recorded and refreshes the list; a network/server error says nothing was recorded and allows
a retry. The result is labelled **Refunded (manually recorded)** (not "provider confirmed"), with the administrator id, the time
and the reference. Retry remains for `Failed`/automatic refunds.

**What this does not prove.** The app cannot verify that the money moved: the record is an administrator's statement, audited
as such. Reconcile it against the Paymob dashboard/settlement report. Unit and in-memory HTTP tests cover the rules,
authorization, antiforgery and binding; the row lock, constraints, trigger and concurrent behaviour are covered only by
`PaymentWorkflowHttpTests.ManualRefund_*`, which are compiled but **have not been run** (no Docker).

## Integration-test checklist (needs Docker / PostgreSQL)

In-memory tests prove HTTP behaviour and service logic only. They do **not** prove locking, isolation, uniqueness or
concurrent webhook correctness. When Docker is available run, in this order, and read the failures before anything else:

1. `dotnet test tests/PulseReserve.IntegrationTests` (the existing 187 plus `ProviderPaymentsHttpTests`).
2. Specifically confirm (all in `ProviderPaymentsHttpTests` unless noted):
   * `Stripe_AVerifiedWebhook_PaysTheOrder_AndDuplicatesNeverDuplicateTicketsOrOutboxEvents` - one `Paid` transition, one ticket,
     two outbox rows (`uq_tickets_order_seat`, `uq_outbox_events_type_aggregate`), `checkout_url` cleared;
   * `Paymob_AVerifiedCallback_PaysTheOrder_AndRejectsForgedOrMisdirectedOnes`;
   * `Stripe_ASuccessAfterTheReservationExpired_RequiresARefund_WhichIsCompleteOnlyAfterStripeConfirms` and
     `Paymob_ALateSuccess_RequiresARefund_ThatWaitsForAnOperator_NeverMarkedCompleteByGuessing`;
   * `ProviderChoice_IsServerControlled_ListedForTheOrder_AndNeverSwitchedSilently`,
     `AnAmbiguousProviderTimeout_LeavesTheAttemptInPlace_AndTheSameKeyResumesIt`,
     `ADefinitiveProviderRefusal_FreesTheOrderForAnotherProvider` (`uq_payment_attempts_one_initiated_per_order`,
     `uq_payment_attempts_provider_reference`);
   * `ACancelledOrder_NoLongerExposesTheHostedCheckoutLink_AndTheStoredLinkIsCleared`;
   * `InvalidStartPaymentRequests_LeaveNoAttemptRowsAndTheOrderUntouched`, `TheAdminProviderList_ExposesCapabilitiesOnly_ToAdministrators`;
   * `PaymentWorkflowHttpTests` (reconciliation lease/backoff/NeedsReview, refunds query-before-resubmit, operations 401/403 theory
     including the new `/providers` row) and `CheckoutApiHttpTests` (start payment with no body).
3. **Written, compiled, never executed (needs PostgreSQL):** two *concurrent* identical success webhooks
   (`ConcurrentIdenticalSuccessWebhooks_...`, started together but not interleaving-controlled); a success racing a
   `checkout.session.expired` for the same attempt (`ASuccessRacingASessionExpired_...` repeated rounds, and
   `SuccessAndExpiredEvent_BothContendingForTheOrder_...` which holds the order lock until PostgreSQL reports both waiting); two
   concurrent `InitiateAsync` with the same key (`ConcurrentStartsWithTheSameKey_...`). Document [19](19-financial-domain-invariants-and-runbook.md)
   lists what each of them does and does not establish. The code path relies on `FOR UPDATE` on the
   order row (then the attempt row) in `RecordAttemptOutcomeAsync`, the conditional `UPDATE ... WHERE status IN (...)` in
   `FinalizeAfterSaleAsync`, and `ON CONFLICT DO NOTHING` / unique indexes; that reasoning is unverified until those tests run.
4. **Migration `20261021204239_AttemptCheckoutUrl`:** apply to a database that already contains attempts and confirm existing rows
   read back (`checkout_url` NULL) and `Down` removes only the column. Apply the migration **before** deploying the application
   version that selects the column; a database one migration behind makes every attempt read fail.

## Limitations and blockers

* No real or sandbox provider verification (no credentials, no merchant account in this environment).
* Paymob status lookup and refunds are not implemented (see above); Paymob refunds are completed by an operator in the
  Paymob dashboard; an administrator then records it in the console (*Manual refund completion*). Until recorded, the order
  stays `RefundRequired`, and a recorded refund is never described as provider-confirmed.
* A Stripe Checkout Session is not expired when the order is cancelled or expires (session lifetime is 31 minutes); a customer
  who pays after that is refunded through the normal `RefundRequired` path.
* Paymob has no idempotency header for intentions, so this application **never repeats** the initiation call for an attempt
  (`IPaymentProvider.IdempotentInitiation == false`): after an ambiguous answer the attempt is closed (`initiation_uncertain`),
  nothing is shown to the customer, and the customer starts a new attempt (document 22). It is **not** claimed that a duplicate
  or orphan intention cannot exist at Paymob; if one is paid, the verified callback cannot match the closed attempt and becomes a
  payment anomaly for an operator (document 21). This replaces the earlier "a retry may create a second intention" behaviour.
* The Paymob `client_secret` is stored inside `checkout_url`, returned only to the order's owner while the attempt is in flight.
* Business eligibility is not checked: Stripe availability in your country, supported currencies/payment methods, Paymob
  integration ids and KYC are account matters. Refund of a completed paid purchase is still not offered (doc 14).

## Before going live (checklist)

1. Create sandbox/test credentials, set `Mode=Test`, `AllowedCurrencies`, return/callback URLs; register the webhook endpoints.
2. Pay a test order end to end for each provider; confirm the order becomes `Paid` only after the webhook, duplicates change
   nothing, tickets are issued once, and the return page shows the server status.
3. Stripe: refund a test payment (including the pending path) and confirm it is `Succeeded` only after Stripe confirms.
4. Paymob: confirm where the `hmac` actually arrives and how null fields are signed on a real callback; decide the manual
   refund procedure.
5. Run the integration suite with Docker available.
6. Only then switch to `Live` in Production with secrets from the secret store.

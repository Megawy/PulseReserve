# 22. Payment eligibility and ambiguous payment initiation

Evidence labels are those of document 19 (U executed unit test, C integration test compiled but **not executed**, S source review, P real
provider sandbox - **none**). No provider was contacted for this work.

## 1. Eligibility is a fact about the order, not about a provider

Before this change `GET /api/orders/{id}/payment-options` answered with a provider list only. An order that could **never** be paid again
(a payment exception was recorded for it, see document 18) still listed providers, and the customer discovered the refusal by pressing
"Pay" and receiving `409`. Provider capabilities and order eligibility were also easy to confuse.

The response is now

```json
{ "currency": "USD", "providers": [ { "name": "Stripe", "displayName": "...", "isSimulated": false } ], "eligible": true, "reason": null }
```

| `eligible` | `reason` | `providers` | Meaning |
| --- | --- | --- | --- |
| `true` | `null` | every enabled provider that can charge the order's currency | the order can be paid now |
| `false` | `OrderNotPayable` | empty | not awaiting payment (paid, cancelled, expired, refund pending, refunded) or its payment window has ended |
| `false` | `OrderHeldForReview` | empty | an earlier payment for this order needs the operator's attention; **permanent**: the order can never be paid again (any exception, open or resolved) |
| `false` | `NoPaymentMethod` | empty | payable, but no enabled provider can charge this currency |

Rules (all in `PaymentService.OptionsAsync`, evaluated in this order, U: `PaymentEligibilityTests`):

* another customer's order, or an unknown id, is `404` - never an eligibility answer;
* `eligible = false` always comes with an empty provider list, so nothing misleading is offered;
* the reason is a stable code, never free text, never an administrator detail (no exception id, amount, provider, resolution) - the
  endpoint is customer-visible (U: `TheEndpoint_ExposesEligibilityAndAReasonOnly_NeverAnAdministratorDetail`);
* `PaymentService.InitiateAsync` applies the same rule: paying a held order is refused with `409 Payment.OrderHeldForReview`, and the
  options and the start can never disagree (U: `PayingAHeldOrder_IsRefused_ConsistentlyWithTheOptions`);
* the database half of the rule is the check under the order lock in `BeginPaymentAsync`, so a race between "options" and "pay" is still
  safe (C: `PaymentStart_RacingALateSuccess_...`, `PaymentOptions_OfAnOrderWithAnException_OfferNothing_ForEveryProvider`).

**Provider capabilities are a different thing and stay separate.** Whether a provider deduplicates initiation, can look a payment up or can
refund through an API is operator information (`GET /api/admin/payments/providers`, `supportsIdempotentInitiation`, `supportsRefunds`,
`supportsPaymentLookup`). None of it is in the customer response (U: `TheEndpoint_ForAPayableOrder_IsEligible_AndSeparateFromProviderCapabilities`).

### Angular

`order-api.models.ts` carries `eligible` and `reason`; `checkout-flow.ts` exposes `heldForReview` and `ineligibleReason`; the checkout panel
shows a message instead of a payment form when `eligible` is false (for a held order: "This order can no longer be paid. An earlier payment for
this order needs attention from our team, so no payment method is offered for it. You have not been charged again. Cancel this order to release
your seats, then start a new one."; for any other reason: "This order is not awaiting payment any more, so no payment method is offered."). A payable order for which no provider can charge the currency
(`NoPaymentMethod`) is reported as "Payments are unavailable", **not** as an order that is no longer payable (`checkout-flow.ts`:
`ineligibleReason` excludes it; U: `multi-provider.spec.ts`). The provider list is rendered from the response: nothing is
hard-coded, and a provider that is not in the list cannot be chosen. A `409` on start is handled the same way (U: `checkout-flow` and
`checkout-panel` specs).

## 2. Ambiguous initiation: Paymob cannot deduplicate

Starting a payment calls the provider to create a hosted checkout. If the call ends ambiguously (timeout, connection loss, an unreadable
answer) the provider may or may not have created something.

| | Stripe | Paymob |
| --- | --- | --- |
| Initiation carries an idempotency key | yes (`Idempotency-Key` per attempt) - a repeat returns the same session | **no** - a repeat may create a second intention |
| `IPaymentProvider.IdempotentInitiation` | `true` (default) | `false` |

This application **does not claim** that a repeat call could not create a second payment session at Paymob: the adapter has no way to know,
and no lookup exists. The rules below make a duplicate session impossible *from this application's own retries*, and make any payment that
nevertheless arrives visible to an operator. They cannot make duplicates at the provider impossible.

For a provider with `IdempotentInitiation == false` (`PaymentService.InitiateAsync`):

1. **At most one provider call per attempt.** The provider is asked once for an attempt. A replay (same idempotency key) whose answer was
   never stored is **not** sent again: if the first call is still running it answers `409 Payment.AttemptInProgress`; if the attempt was closed
   it answers `409 Payment.InitiationUncertain` ("We could not confirm whether the payment page was created, so this attempt was closed. Nothing
   has been charged. Please start the payment again.") (U: `ReplayingTheSameKey_AfterTheAmbiguousAnswer_DoesNotCallTheProviderAgain`,
   `ARepeatWhileTheFirstCallIsStillRunning_...`).
2. **An ambiguous answer closes the attempt.** `CancelUncertainAttemptAsync` moves an `Initiated` attempt that has **no provider reference** to
   `Cancelled` with failure code `initiation_uncertain`, clears any link, and writes a history row with reason `initiation_uncertain`
   (basis `ApplicationDecision`). The conditional `UPDATE ... WHERE status = 'Initiated' AND provider_reference IS NULL` cannot close an attempt
   that has a reference. (U for the service and labelling; C for the SQL.)
3. **Recovery is a new attempt.** The customer starts again with a new idempotency key; the new attempt has its own id, which is what the
   merchant-order reference sent to the provider carries, so a payment is always correlated to exactly one attempt.
4. **Never retried elsewhere.** An ambiguous answer is never re-tried on another provider (unchanged for every provider).
5. **If the first call did create a session and the customer pays it,** the closed attempt has no stored reference, so the verified callback
   cannot match: it becomes a `ReferenceMismatch` **payment anomaly** (document 21) that an operator sees and refunds in the Paymob dashboard.
   That is the only recovery for an orphan Paymob intention, and it is manual.
6. A **definitive refusal** (`ProviderRejectedException`) is unchanged for every provider: the attempt is closed as `provider_refused_initiation`
   and the customer may choose another provider (U: `ADefinitiveRefusal_IsUnchanged_ForEveryProvider`).

A provider that deduplicates (Stripe) keeps its idempotent resume: the attempt stays `Initiated`, the next press of "Pay" reuses the key and
receives the same session (U: `AProviderThatDeduplicates_KeepsItsIdempotentResume`).

Stale attempts that never obtained a provider reference are closed by the reconciliation worker after the stale-attempt age
(`stale_attempt_cleanup`); an attempt that does have a reference is never abandoned while the order is still payable.

### What remains unknown

* Whether Paymob creates a second intention for a repeated call, and whether an orphan intention can be paid (P: unverified).
* How long an orphan intention stays payable at Paymob. If the customer pays after the order's payment window, the late-success rules apply
  only when the reference matches; otherwise it is an anomaly.
* The ambiguity window is the HTTP timeout; a customer who sees `InitiationUncertain` and immediately pays the *first* page (opened from an
  earlier tab) is the scenario in item 5.

## 3. Tests

| Area | Tests | Status |
| --- | --- | --- |
| Adapter declarations (Paymob cannot deduplicate, Stripe can) | `AmbiguousInitiationTests` (7) | U |
| Eligibility, held order, not payable, past deadline, currency, ownership, endpoint contract | `PaymentEligibilityTests` (9) | U |
| Options of an order with an exception are empty for every provider; start refused after an exception | `ProviderPaymentsHttpTests.Audit.cs`, `.Races.cs` | C |
| Checkout panel / flow wording and gating | Angular `checkout-panel` and `checkout-flow` specs | U |

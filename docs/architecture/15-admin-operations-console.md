# 15. Admin Operations Console

Operator UI for payment reconciliation, refunds, the outbox and the audit log. It consumes the existing
`/api/admin/payments/*` contract (doc 14 §6); the backend gained only read-side additions described below.

## Routes (Angular, all lazy)
| Route | Page |
|---|---|
| `/admin/payments` | Overview: live counts, per-state breakdown, **Process now** |
| `/admin/payments/reconciliation` | Reconciliation tasks (`?state=&limit=`), re-queue `NeedsReview` |
| `/admin/payments/refunds` | Orders awaiting a refund + refund records, request/retry with confirmation |
| `/admin/payments/outbox` | Outbox events, re-queue `NeedsReview`, at-least-once wording |
| `/admin/payments/audit` | Read-only audit log |
| `/access-denied` | Shown to a signed-in non-administrator |

## Access control
* `adminGuard`: anonymous → `/login?returnUrl=<requested console URL>`; signed in but `isAdmin === false` → `/access-denied`.
  `isAdmin` is the server's answer from `GET /api/identity/session` (derived from `Identity:AdminUserIds`); the browser never
  sends or stores a role, and there is no role-request or promotion flow.
* The guard is a courtesy. Every `/api/admin/*` call is authorised again by the Admin policy (401 anonymous, 403 non-admin) and
  mutations need the antiforgery token (`Csrf.Invalid` → one-shot session refresh + replay in `authInterceptor`).
* A 401 during use flips the shell to "Your session has ended" with a sign-in link that returns to the same page.
* A 403 from an action keeps its dialog open with "administrator access is required"; a 403 on load shows an error state.

## Backend additions (no migration, no module-boundary change)
* `GET /overview` now also returns `tasksByState`, `refundsByState`, `outboxByState` (real `GROUP BY state` counts).
* Refund and outbox payloads expose `nextAttemptAtUtc`/`updatedAtUtc` (columns that already existed).
* `GET /orders?status=&limit=` and `GET /orders/{id}`: an operator view of orders (money, status, refund id/state, provider, simulated flag).
  It deliberately has **no customer identifier, email, provider reference, idempotency key or token**.
* Tests: per-state counts match the database; lists expose timing fields; orders show refund progress without customer data;
  all new routes are in the 401/403 theory.

## Behaviour rules the UI enforces
* Only items the server would accept are offered an action (mirrors `WorkflowStore`): tasks/outbox from `NeedsReview`; refunds from
  `Failed` or `NeedsReview`; a refund request only for a `RefundRequired` order with no refund. The server still re-checks under a
  row lock; 404/409 closes the dialog, says the item is no longer eligible and reloads.
* **Re-queue is not resolve.** After a retry the toast says it is not resolved; the row stays `Queued` until the worker completes it.
* **Refund complete = provider-confirmed.** Only `Succeeded` with `completedAtUtc` reads "Refunded (provider confirmed)". Requesting
  or retrying shows "not complete until the provider confirms". The refund dialog shows the exact order id and amount and, for
  simulated payments, that no real money moves.
* Outbox wording: delivery is **at least once**; a retry can repeat an effect such as a confirmation message.
* Process now shows the server's actual counts (processed/failed per area) and a warning tone if any failed.
* Lists are bounded (50/100/200 newest). The API has no paging or free-text search, so the search box narrows **loaded rows only** and says so.

## Design-system pieces
Reused: `Alert`, `StatusBadge`, `ErrorState`, `EmptyState`, `ToastService`, `.table`, `.facts`, `.badge`, skeleton classes, tokens.
Added: `StatTile` (+ `.tiles/.tile` styles), `ConfirmDialog` extended (details slot, `busy`, `tone`; Escape ignored while busy),
`.table--stack` (rows become labelled cards below 48rem), presentation tables `work` and `refund` and label helpers
(`taskKindLabel`, `outboxTypeLabel`, `refundReasonLabel`, `auditActionLabel`, `subjectTypeLabel`), `shortId`.
No UI/icon/chart library; no charts (counts are shown as numbers).

## Known limits
* No customer-facing view of any of this; no per-order detail page; no pagination beyond the 200 newest.
* Refund of a completed paid purchase is still not exposed.
* Actor and subject ids are shown shortened (full value in the tooltip / confirmation dialog); there is no user-name lookup.

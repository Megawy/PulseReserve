# PulseReserve Web

Angular 22.2.3 (zoneless, standalone, signals), TypeScript 6.0.x, Vitest, ESLint (flat config), Prettier.

## Status

Checkout: a held reservation can be turned into an order (server-calculated lines and total, shown as returned),
paid through the development/test payment simulator (clearly labelled; no card data), cancelled, and recovers after a
refresh. Real payment and the production provider do not exist yet.

Booking journey: event list (`GET /api/catalog/events`), event details, seat map from the Catalog
layout plus Reservation availability, seat hold (`Idempotency-Key`), server-based countdown, release,
and refresh recovery. Payment, authentication and SignalR do not exist yet.

**Admin operations console** (`/admin/payments`, administrators only): live overview counts, reconciliation tasks,
refunds, outbox events, a read-only audit log and "process now", all against `/api/admin/payments/*`. The route guard
only keeps non-admins out of the way (anonymous → sign-in with return URL, non-admin → `/access-denied`); the API
authorises every call. See `docs/architecture/15-admin-operations-console.md`.

**Payment providers**: checkout lists only the methods the server offers for the order (`GET /api/orders/{id}/payment-options`), sends the provider NAME only, and redirects to the server-returned `https` payment page for a real provider. Coming back from it (`?payment=return`) shows the order's current server status and never confirms anything itself. See `docs/architecture/16-multi-provider-payments.md`.

**Authentication** is a same-origin, server-side cookie session (`/api/identity/*`). The session cookie is
HttpOnly, so the SPA never sees a password after submitting it, a token, or the cookie, and nothing
sensitive is stored in browser storage. Mutating requests carry the antiforgery token that Angular
echoes from the `XSRF-TOKEN` cookie. Reservation ownership comes only from the session; the client
never sends a customer ID. A 401 on a protected call drops the UI to signed-out with a sign-in prompt.

Docker (recommended): see `docs/DEVELOPMENT.md` � `scripts/dev.ps1 up` serves this app with HMR on http://localhost:4200.

Manual local run: API on `http://localhost:5000` (`dotnet run --project src/PulseReserve.AppHost
--no-launch-profile`, with `ConnectionStrings__Catalog` set to a disposable database), then `npm start`.

## Design system

Mirrors the Mailora/Platinumail system (same semantic token names, hand-written SCSS, no UI/icon/animation
library) with PulseReserve's own palette. Tokens live in `src/styles/_tokens.scss` (light, dark and
`data-theme` overrides), global controls and components in `_controls.scss` / `_components.scss`, motion in
`_animations.scss` (reduced-motion aware). Shared Angular pieces are in `src/app/shared/ui`; money, dates and
status wording come only from `shared/format.ts` and `shared/presentation.ts`. Components never hardcode colours.

## Commands

```bash
npm ci --ignore-scripts      # reproducible install from package-lock.json, no lifecycle scripts
npm run lint
npm run test:ci              # single run
npm run build
npm run format:check
npm start                    # dev server; proxies /api via proxy.conf.mjs
```

## Unverified assumptions (keep in sync with the code)

1. **Typed-ID wire format** is assumed to be `{ "value": "<guid>" }` for response IDs. It is inferred
   from the C# source (`record struct` IDs, no custom JSON converter) and has never been observed
   on a running API. It is isolated in `src/app/core/api/wire-id.ts`; revisit it and its spec once
   the backend serialisation contract is confirmed.
2. Response property casing is camelCase (ASP.NET Core minimal-API default), unobserved.
3. Only `status`, `title`, `detail` of ProblemDetails are used. Unhandled server errors may return
   non-JSON bodies (backend doc 10, M-03); these are handled generically.
4. Responses expose no concurrency version (backend doc 10, H-03); none is modelled.
5. `EventResponse.status` is kept as a `string`; allowed values are not a verified contract.
6. The dev proxy target (`PULSERESERVE_API_URL`, default `http://localhost:5000`) was verified against the running API.
7. API base `/api` assumes same-origin deployment (proposal D-03 only).

## Test evidence levels

Unit tests use fixtures derived from the C# contracts. The booking journey (list, details, seat map,
hold, refresh restore, release, conflict) was also exercised manually against the live .NET 10 API
on a throwaway PostgreSQL container.

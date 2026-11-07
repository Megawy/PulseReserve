# Browser verification report — customer and administrator journeys, live updates

Date: 2026-10-23. Authorized by the owner for this purpose only: build the production images, and use the built-in browser on the **dev stack**
(`pulsereserve-dev-*`, `http://localhost:4200`), desktop and 375 px, light and dark. No provider sandbox was contacted; payments used the
development **simulator** (clearly labelled "(simulated)" everywhere). Nothing was committed.

## Method and limits

* Built-in browser pane (Chromium). Viewports: the pane's default (about 982 × 880, called "desktop" below) and an emulated 375 × 812;
  `prefers-color-scheme` emulated light and dark. Reading was done mostly from the DOM (`get_page_text`, `find`, small scripts) with screenshots
  for layout. **Screenshots lag one frame behind a state change** in this pane (a screenshot taken straight after a click showed the old state);
  conclusions were therefore taken from the DOM, and a second screenshot where the visual mattered.
* Accounts: a throw-away customer registered through the UI (generated test credentials, kept outside the repository), the project's dev admin
  (credentials from the project's own `.env`, created by `scripts/dev-seed.mjs`), and a **second customer driven by script through the real API**
  (`qa-actor.mjs` in the session scratchpad, not in the repository) so that live updates came from someone else's committed changes.
* The dev stack's database kept the test data (two `@pulsereserve.test` customers, a few reservations/orders paid with the simulator). Nothing
  was deleted.
* **Not covered:** other browsers (Firefox, Safari), real touch devices, screen readers, `prefers-reduced-motion` (the CSS rule exists; the pane
  has no switch for it), zoom/large-text, slow-network profiles, the WebSocket frames themselves (only the negotiate call and the resulting
  "Live" state were observed; resource timing does not list a WebSocket), a second administrator, any provider-hosted payment page.

## What was verified

| # | Journey step | Viewport / scheme | Result |
|---|---|---|---|
| 1 | Event list renders; demo event, price from $60 | desktop dark | ✔ |
| 2 | Register a new customer through the UI → redirected home, header shows the name and "My orders" | desktop dark | ✔ |
| 3 | Event page: seat map, legend, **"Live" badge** appears once the server has identified the connection | desktop dark + light | ✔ |
| 4 | **Another customer holds B3 → this page shows it "held by someone else" with no reload**; they release → "available" again | desktop | ✔ (both ways, within about 2 s) |
| 5 | Select two seats, running total, "Hold selected seats" → server-confirmed hold, "Seats held", 10:00 countdown | desktop light | ✔ |
| 6 | **Clear all local state and reload → the page finds the hold on the server** ("You already hold seats for this event. This is your current hold.", countdown 9:53) | desktop | ✔ (new `GET /api/reservations/active`) |
| 7 | Proceed to checkout → order with server totals, "Awaiting payment", "Payment simulator — Development/test only: no real money moves", method labelled "Simulated" | desktop light | ✔ |
| 8 | Second tab on the same order; in the first tab "Pay now (simulator)" → **second tab shows "Payment #1 In progress" by itself** | two tabs | ✔ |
| 9 | "Simulate successful payment" → **second tab shows "Paid (simulated)" / "Succeeded (simulated)" by itself; then, about 15 s later when the worker issued them, the two tickets appear by themselves** | two tabs | ✔ |
| 10 | Order page layout: summary, tickets, status badges | desktop light, mobile dark | ✔ no horizontal overflow at 375 px |
| 11 | Seat map at 375 px | mobile dark | ✔ page does not overflow; the map scrolls horizontally *inside its card* (columns 7–8 are off-screen until scrolled: pre-existing design, see "Observations") |
| 12 | Sign in as the dev admin (returnUrl honoured), "Signed in as Dev Admin", console with "Live" badge | desktop light | ✔ |
| 13 | **Admin overview updates live** when another customer pays: "Outbox queued 2" appeared without a reload | desktop | ✔ — but see finding B: the later change *queued → done* did **not** arrive |
| 14 | All eight console pages (Overview, Reconciliation, Refunds, Exceptions, Anomalies, History, Outbox, Audit log) load; empty states are worded; no error banners; "Live" badge on the pages that follow live areas | mobile dark | ✔ no horizontal overflow on any |
| 15 | Console overview at 375 px | mobile dark | ✔ tiles stack, sub-navigation scrolls horizontally |
| 16 | Worker closes an **expired hold** (the seat held by the second customer at 23:25 expired at 23:35) → the open page shows it "available" **without any request**; the API log says "Closed 1 expired hold(s)" | desktop | ✔ |
| 17 | **Outage:** page-level fetch of `/hubs/` made to fail; at the 10-minute connection rotation the badge changed to **"Reconnecting"** with the announcement "Live updates are paused. This page catches up as soon as the connection returns." | desktop | ✔ |
| 18 | While down, another customer holds B4 → the page does not know immediately, **the 30 s fallback poll brings it in** ("held by someone else") | desktop | ✔ |
| 19 | While still down, that customer releases B4; connection allowed again → **badge "Live" and B4 "available" within 14 s** (reconnect backoff, then the announced re-read) | desktop | ✔ |
| 20 | Sign out through the button → home page, "You have been signed out." toast; live connection count stays equal to the number of open tabs (3): the signed-in connection was replaced, nothing leaked | desktop | ✔ |
| 21 | Keyboard: Tab moves through the header links with a 3 px solid focus ring (`:focus-visible` true) | desktop dark | ✔ for the links tried; the seat grid was not traversed key by key |
| 22 | Server-side view as the admin: `GET /api/admin/live/status` — listener connected, 0 reconnects, 0 dropped signals, connections = open tabs | script | ✔ |

## Findings

| | Finding | Severity | Status |
|---|---|---|---|
| A | **Two hub connections per signed-in page load.** `LiveService` opened one at start and the identity effect immediately reopened it because the session had just become known. Seen as two simultaneous `negotiate` calls. | Medium (wasted connects, counts against the connect rate limit) | **Fixed** (identity is compared with the identity the connection was started for) + regression spec; re-verified: one `negotiate` per load |
| B | **Admin overview went stale while the workers moved items between queue states.** The first version of the trigger announced only items entering or leaving `NeedsReview`, but the overview shows a count for every state (queued / processing / completed / needs review): the page said 2 queued, the server said 0. | Medium (visibly wrong numbers until the next unrelated change) | **Fixed in code**: new migration `20261022232110_LiveQueueStateNotifications` announces every insert and state change of both queues; PostgreSQL test `TheWorkQueues_AnnounceEveryStateChange…` added and passing (deterministic, with its own LISTEN connection). **Not yet re-verified in the browser**: the dev stack's API hot-reloaded the code but applies migrations only when it restarts; restarting the owner's container was not part of the authorization |
| C | `GET /api/reservations/active` answered **404** when there is no hold, so every event-page visit put two red errors in the browser console. | Low | **Fixed**: now `204 No Content` (the client still reads an older server's 404 as "none"); PostgreSQL test and Angular spec updated; verified in the browser (204) |
| D | **A hard page load reads twice**: the page's first read starts before the hub connection is confirmed, so the announced "connected" gap triggers one more read (visible: two `availability`/`active` calls about 350 ms apart; two `overview` calls on the console). | Info — by design (it closes the gap between "loaded" and "subscribed"); costs one extra read per hard load or reconnect | Documented in doc 28 §7 |
| E | The connection of a *signed-out* tab in another window keeps its old identity until the 10-minute rotation (e.g. the customer tab left open after the admin signed out in a sibling tab: the cookie is shared, the other tab's connection is not told). | Info — documented limit (max connection age) | Documented in doc 28 §4 |

## Observations that are not defects of this work

* Tickets display their full code string; there is no QR rendering (decision pending, feature matrix #23).
* The 375 px seat map needs a horizontal scroll inside its card.
* The Live badge sits directly under the date line on the order page; spacing is tight but legible in both schemes.
* "Pay within …" showed an ellipsis in a background tab (timers are throttled there); it counts down normally in the foreground (9:22 observed).

## What this does and does not establish

Established (on the owner's dev stack, simulator payments): the live layer works end to end through the real dev proxy, real cookies and the
real database, in a real browser, for seats, my reservation, my order, tickets, hold expiry, outage and recovery, and the administrator overview
(except finding B's re-check). **Not established:** provider-hosted payment pages and return redirects, other browsers/devices, assistive
technology, reduced motion, behaviour behind the production nginx image (built, never run), and capacity.

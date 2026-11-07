# Load and contention scripts

k6 scripts for `docs/reports/realtime-load-test-plan.md`. **Written but never run.** Read the plan first: these must only be pointed at a
dedicated, disposable test environment, never at production or at a database that holds anything you care about.

| Script | What it checks |
|---|---|
| `hold-contention.k6.js` | N customers race for one seat: exactly one `201`, the rest `409`. |
| `live-latency.k6.js` | Time from a seat hold to the `availability` message on other browsers' hub connections; no announcement is missed. |

Install k6 from <https://k6.io/> (free, open source), then see the header comment of each script for the command line and prerequisites.

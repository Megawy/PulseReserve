# 26. Operational signals for unresolved payment items

The repository has no notification service, no metrics exporter and no monitoring infrastructure, and none was added. It has `ILogger` and two
health probes (`/health/live`, `/health/ready`). This document describes the smallest set of signals built on those, how they behave, and what an
operator should do with them. Evidence labels are those of document 19. The signalling rules and the counters are unit-tested (**U**); the
database counts behind them are covered by compiled-only PostgreSQL tests (**C**); no alert rule here has been exercised in a real monitoring system.

## 1. The authoritative number

**The unresolved count is always read from the database, never from memory.** `GET /api/admin/payments/overview` (administrators only) returns:

| Field | Meaning |
| --- | --- |
| `paymentAnomaliesOpen` | open anomalies (signature-verified success events that were not applied) |
| `paymentAnomaliesOpenByKind` | the same, by kind (`AmountOrCurrencyMismatch`, `ReferenceMismatch`, `UnknownPaymentReference`, `InconsistentAssociation`) |
| `oldestOpenPaymentAnomalyAgeSeconds` | seconds, **on the database clock**, since the oldest open anomaly was detected; `null` when none is open. The number that must not keep growing |
| `paymentExceptionsOpen`, `oldestOpenPaymentExceptionAgeSeconds` | the same for late-success exceptions |

The console shows them on the Overview tiles ("Oldest has waited 3 d 2 h"). The Anomalies page lists open items **first and oldest first** (then
resolved ones, newest first), shows each item's category, its age (`ageSeconds`, database clock) and its status, and warns when the page may hide
more rows. An acknowledged anomaly therefore cannot drop out of sight: the webhook is acknowledged only after the row exists (document 21), the row
is never deleted by the application (document 25), and it keeps being counted and listed until an administrator records an outcome.

## 2. What the process emits

### Log events (stable ids; identifiers and codes only)

Filter on `EventId.Id` or `EventId.Name`. No message carries a payload, signature, secret, provider reference, amount of a customer, name or email;
a storage failure logs the exception **type**, not its text.

| Id | Name | Level | When | Alert guidance (suggestion, untested) |
| --- | --- | --- | --- | --- |
| 7101 | `AnomalyRecorded` | Warning | a **new** anomaly was stored (once per provider event) | route to the payments operator queue; business-hours ticket |
| 7102 | `AnomalyDuplicate` | Information | the provider redelivered an event that is already recorded; acknowledged | **do not alert**; a burst only means the provider is still sending |
| 7103 | `AnomalyPersistenceFailed` | **Error** | the evidence could **not** be stored; the webhook was **not** acknowledged and the provider will deliver it again | alert if it persists (for example more than 15 minutes): events are currently not being recorded. Usually accompanies a database incident |
| 7104 | `UnmatchedEventIgnored` | Information | a verified failure/expiry matching no payment moved no money; acknowledged without a record | no alert; high volume means a provider account is shared with another environment |
| 7105 | `EventNotJudgeable` | **Error** | a verified success could not be judged because our own order data is unusable; **not** acknowledged | alert on any: investigate the order/attempt data; the provider keeps the event |
| 7110 | `UnresolvedBacklog` | Warning | payment items wait for an administrator (see 3) | business-hours reminder |
| 7111 | `UnresolvedBacklogCleared` | Information | nothing is waiting any more | close the ticket |

### Counters (.NET `Meter` named `PulseReserve.Payments`; part of the framework, no package, no exporter)

| Instrument | Tags | Counts |
| --- | --- | --- |
| `pulsereserve.payments.webhook.events` | `provider`, `outcome` (`Applied`, `Acknowledged`, `AnomalyRecorded`, `Rejected`, `Failed`, `ProviderUnavailable`, `Error`) | every webhook answered |
| `pulsereserve.payments.anomalies.recorded` | `provider`, `kind` | new anomalies stored |
| `pulsereserve.payments.anomalies.deduplicated` | `provider`, `kind` | redeliveries absorbed by an existing record |
| `pulsereserve.payments.anomalies.persistence_failures` | `provider`, `kind` | evidence that could not be stored |

They count what **this process** saw since it started (several instances each have their own). Read them without installing anything into the
application with the free `dotnet-counters` tool, for example `dotnet-counters monitor --process-id <pid> --counters PulseReserve.Payments`, or
export them with whatever OpenTelemetry collector the platform already runs (not provided). The tags are bounded sets: they contain no ids.

## 3. The backlog reminder (no per-request noise)

Each host's payments worker looks at the durable counts once a minute and logs through `UnresolvedPaymentItemsReporter`:

* **at once** when the number of open anomalies or exceptions **grows** (a new item needs attention now);
* **again as a reminder at most once every 6 hours** while anything stays open (a forgotten item keeps surfacing);
* **once, at Information**, when everything is resolved;
* otherwise **silent** (including when the count shrinks but is not zero), so it cannot bury an incident.

Its state is per process: a restart logs the current backlog once more, and N instances give up to N reminders per interval. A failed check logs
the exception type at Warning and never stops the worker (U for the rules; the worker loop itself is covered only by the host's integration tests, C).

## 4. Persisted anomaly versus transient failure

| Situation | HTTP answer | Row stored? | Log | Counter | What the operator sees |
| --- | --- | --- | --- | --- | --- |
| Valid event, matching data | 200 | no | - | `webhook.events{Applied}` | nothing to do |
| Verified mismatch, first delivery, storage works | 200 | **yes** | 7101 Warning | `anomalies.recorded` | Overview tile, Anomalies list, backlog log |
| Same event delivered again | 200 | no new row (one already exists) | 7102 Information | `anomalies.deduplicated` | unchanged count |
| Verified mismatch but storage is failing | **5xx** | **no** | 7103 Error | `anomalies.persistence_failures`, `webhook.events{Error}` | no new row: the **provider retries** and the next delivery is stored; fix the database |
| Verified success whose order data is unusable | **5xx** | no | 7105 Error | `webhook.events{Failed}` | the provider keeps the event; repair the data |
| Verified failure/expiry matching nothing | 200 | no | 7104 Information | `webhook.events{Acknowledged}` | none (no money moved) |
| Invalid signature, unreadable body, wrong merchant context | 400 | no | Warning (existing) | `webhook.events{Rejected}` | the provider's dashboard shows a failed delivery; nothing here is trusted |

The distinction matters for incident response: a **persisted anomaly** needs an investigation of a payment; a **persistence failure** needs a fix
to the platform and resolves itself on redelivery; a **rejected** delivery is either a forgery or a mis-registered endpoint.

## 5. Detecting a growing backlog (what to put in a monitoring system)

Pick whichever your platform already has; none requires a new component:

1. **Logs** (simplest): alert on `EventId = 7110` (business hours), on `7103` persisting, and on any `7105`.
2. **Counters**: alert when `increase(anomalies.recorded)` over a day exceeds what the team can investigate, and on any `persistence_failures`.
3. **Database** (for a DBA or a scheduled report; read-only):
   `SELECT kind, count(*) AS open, floor(extract(epoch FROM now() - min(detected_at_utc))) AS oldest_seconds FROM payments.payment_anomalies WHERE state = 'Open' GROUP BY kind;`
4. **The console**: the Overview tile turns into an attention state while anything is open and states the age of the oldest item. There is no
   service credential for the admin API, so do not point an automated probe at it with a person's session.

## 6. Investigating

Follow the runbook in document 19, section 9.3: use the **provider event id** shown in the log message and the Anomalies page to find the payment
in the provider dashboard, decide, and record the outcome. Remember the provider's resend windows (document 25, section 4) if the cause was a
configuration fault.

## 7. Limits

* No push notification exists (no email, chat or paging integration): a human or the monitoring platform must watch the signals above.
* The reminder interval and check interval are constants in the host (`PaymentsWorker.BacklogReminderEvery`, `BacklogCheckEvery`), not settings.
* The counters and the reporter state are per process and reset on restart; the database count is the only durable one.
* Threshold values in the tables are suggestions that depend on the team; none was validated against real traffic.

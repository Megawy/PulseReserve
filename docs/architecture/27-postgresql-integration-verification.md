# 27. PostgreSQL integration verification: how it is isolated, what exists, how to run it

**Status (updated 2026-10-23): the PostgreSQL integration tests have now been executed.** The owner authorized starting Docker/Testcontainers on
2026-10-23; the whole integration project (266 tests, one throw-away `postgres:17.11-alpine` container per test class) was run, found real
defects (section 0), and then passed **266 / 266 in four consecutive full runs** after the fixes. Sections 1-6 below were written *before* that
run and describe the design; where they say "compiled, not executed" the result in section 0 supersedes them. What is still not covered is
listed in section 5 (and, for providers and browsers, in the final report). Evidence labels are those of document 19.

## 0. First execution against PostgreSQL (2026-10-23)

Environment: Docker Desktop 29.8 (Linux engine, WSL2); each test class started its own container and destroyed it afterwards; the user's
development stack that Docker Desktop restarts on its own (`pulsereserve-dev-*`) was left untouched; `PULSERESERVE_TEST_DATABASE` was removed
from the test process environment (`env -u`) and the native PostgreSQL was not used. Command: `dotnet test tests/PulseReserve.IntegrationTests --no-build`.

| Run | Result | Notes |
| --- | --- | --- |
| Stage 1 only (`PaymentsMigrationPostgreSqlTests`) | 9 / 9 passed | migrations forward/back on populated data, every rollback guard, the documented least-privilege role |
| First full run | 249 passed, **17 failed** | all in `ProviderPaymentsHttpTests` (16) and `PaymentWorkflowHttpTests` (1) |
| After the fixes below | 261 passed, 5 failed | |
| After the remaining fixes | **266 / 266** | then 3 more full runs: 266 / 266 each; the 7 live tests were also repeated 5 times in isolation: all green |

What the 17 failures were (none was a defect in the evidence-protection design itself):

| Cause | Where | Fix |
| --- | --- | --- |
| **Product defect.** An HTTP-client timeout (`TaskCanceledException`) was treated as a caller cancellation and escaped as an HTTP 500 instead of "the provider did not answer" (503 `Payment.ProviderTemporarilyUnavailable`); the same filter (`ex is not OperationCanceledException`) was used at nine sites in the payment service, webhook service and workers, where a provider timeout during a status lookup, refund or outbox step would have bypassed the "outcome unknown, retry later" handling | `PaymentService`, `PaymentWebhookService`, `PaymentWorkflows` | new `CancellationRules.IsCallerCancellation(ex, token)`: only a cancellation the caller requested propagates. Unit regression tests added (initiation, status lookup, refund timeouts; caller cancellation still stops the request). The existing unit tests used `TimeoutException`, which is why they never caught it |
| Test isolation. Test classes share one container across tests but asserted global row counts ("no anomaly was stored") | `ProviderPaymentsHttpTests`, `PaymentWorkflowHttpTests` | `PostgreSqlDatabaseFixture.ResetAsync()` empties the four module schemas in that container before each test of those classes |
| Test expectation. `bool` read back as `True`, test expected `true` | two `checkout_url IS NULL` checks | query casts to text |
| Test expectation. The response leak guard rejected the word "Exception" even inside the legitimate code `Operations.ExceptionNotConfirmed` | `AssertProblemAsync` | the expected code is removed before the guard looks for leaked exception text |
| Tests that depended on data left by other tests | `RetryOperations_ValidateTheCurrentState…`, `OperationsLists_ExposeTimingFields…` | each now creates the data it asserts on (a reconciled crashed payment; a paid order) |
| Races in two of the new live tests (a client that connects after the announcement was never going to receive it) | `LivePostgreSqlTests` | the tests now wait on the server's counters and connect after the first announcement |

What this run proves and does not prove is as in sections 3-6: the lock-barrier tests prove contention was observed, not a particular
interleaving; everything ran as the container's superuser (the least-privilege behaviour is shown by the dedicated test, not by the whole suite).

## 1. How the isolation works (read from the source)

| Safeguard | Where | What it guarantees |
| --- | --- | --- |
| **The database is a Testcontainers container, nothing else** | `PostgreSqlDatabaseFixture` | The fixture starts `postgres:17.11-alpine` (pinned by tag **and** digest) and exposes only that container's connection string. It has **no fallback** to a local or configured database, no environment-variable path and no connection-string parameter. If Docker is unavailable, `InitializeAsync` fails with "[Docker Prerequisite] ...". |
| **One throw-away container per test class** | `IClassFixture<PostgreSqlDatabaseFixture>` | Each test class migrates a fresh database and destroys the container afterwards. Destructive operations (`TRUNCATE` in `CleanupAsync`, migrations down and up) can only reach that container. |
| **The host under test must target exactly that container** | `CatalogApiFactory` + `TestDatabaseGuard` | After the host starts and before any client or service is handed out, the *effective* connection string of **every** module context (catalog, reservation, identity, payments) is compared with the container's on host, port, database and user. A mismatch throws and disposes the host. This is what stops an ambient `ConnectionStrings__Catalog` or the legacy `PULSERESERVE_TEST_DATABASE` variable from redirecting a test at another server. (Executed for the guard logic with fake strings: 6 tests, no Docker.) |
| **No background database work runs before the guard** | the factory sets `Payments:Workers:Enabled=false`; `PaymentsWorker` waits 200-2000 ms before its first query | The guard's premise ("startup performs no database I/O before the check") is pinned by a static test (U: `FinancialDatabaseBoundaryTests.TheIntegrationHostKeepsTheWorkerOff...`). |
| **Migration tests create their own databases inside the container** | `PaymentsMigrationPostgreSqlTests` | Each test runs `CREATE DATABASE payments_migration_<guid>` on the fixture's container and drops only the databases it created. |
| **No other route to a server exists in the test project** | search of `tests/PulseReserve.IntegrationTests` | Every `NpgsqlConnection` is built from the fixture's string; the only literal hosts are `127.0.0.1:1/2` and `192.0.2.1` in the guard tests, where nothing listens. No test reads the environment. |
| **Hosted test environment is explicit** | `UseEnvironment("Testing")` plus `Payments:Simulator:AllowInTestingEnvironment=true` | `Testing` is not permissive by name alone (document 23). |

**Not safeguards** (be precise about them): the guard compares identities, it does not prove the target is empty or disposable by inspection;
`PULSERESERVE_TEST_DATABASE` still exists in the *application's* connection-string resolver as a last-resort fallback (never used by the test
factory, which always configures a string) and should not be set on any machine that runs tests; nothing stops a person from running the tests
with a hand-edited fixture.

## 2. What is discoverable

`dotnet test tests/PulseReserve.IntegrationTests --no-build --list-tests` lists tests **without running them or contacting Docker**. On this tree it
lists **206** test methods (258 cases; theories counted once), 183 of which need Docker:

| Class | Methods | Needs Docker? |
| --- | ---: | --- |
| `ProviderPaymentsHttpTests` (partial: base, `.Audit`, `.Races`) | 48 | yes |
| `PaymentWorkflowHttpTests` | 45 | yes |
| `CheckoutApiHttpTests` | 29 | yes |
| `ReservationApiHttpTests` | 14 | yes |
| `IdentityApiHttpTests` | 12 | yes |
| `CatalogSecurityTests` | 12 | yes |
| `PaymentsMigrationPostgreSqlTests` | 9 | yes |
| `CatalogOptimisticConcurrencyPostgreSqlTests` | 6 | yes |
| `CatalogApplicationPostgreSqlTests` | 3 | yes |
| `CatalogApiHttpTests` | 5 | yes |
| `TestDatabaseGuardTests`, `PaymentsConfigurationTests`, `SecurityConfigurationTests`, `AdminConfigurationTests`, `PaymentWorkflowConfigurationTests`, `FoundationIntegrationTests` | 6 + 5 + 4 + 4 + 3 + 1 | **no** - executed here (40 test cases, all passed) |

Two checks were made so that "compiled" also means "will run": (1) every `public` method of the integration project is either a helper or carries
`[Fact]`/`[Theory]` (no orphaned test method exists), and (2) every test name that documents 16-26 cite exists in the discovered lists (as a name
or as the prefix of one).

## 3. The tests that need PostgreSQL, by concern (all **C**: compiled, not executed)

| Concern | Tests (class) |
| --- | --- |
| Webhook acknowledgement against real SQL: mismatch recorded then acknowledged (200), redelivery deduplicated, unknown attempt stored unlinked, forged signature stores nothing, storage failure not acknowledged and recorded on the retry | `AMismatchingEvent_IsKeptAsEvidence_...`, `AnEventForAnUnknownAttempt_...`, `AMismatchWebhook_IsNotAcknowledgedWhenTheEvidenceCannotBeStored_...`, `Stripe_ABadSignatureOrWrongAmount_ChangesNothing`, the Paymob webhook test (`ProviderPaymentsHttpTests`) |
| Transition history: whole life of a payment, no duplicate rows on redelivery, nothing for a rolled-back change, append-only, no secrets, a refused conflicting event once per provider event | `TransitionHistory_*` (`ProviderPaymentsHttpTests.Audit`) |
| Anomaly constraints, triggers and resolution | `PaymentAnomalies_AreProtectedByTheDatabase`, `AnAnomaly_CanOnlyBeResolvedAsAnAdministratorStatement_...` |
| Unresolved items counted from the database, aged on its clock, oldest listed first, resolved ones kept | `UnresolvedAnomalies_AreCountedFromTheDatabase_AgedOnItsClock_AndTheOldestIsAlwaysListedFirst` |
| Races (lock-barrier: contention observed through `pg_stat_activity`, wake order **not** controlled) | `PaymentStart_RacingALateSuccess_...`, `ResolutionInFlight_AndARepeatedLateSuccess_...`, `SuccessAndExpiredEvent_BothContendingForTheOrder_...`, `RetryAndManualCompletion_Contending...`, `ConcurrentAdministratorRefundRequests_...` and others in `PaymentWorkflowHttpTests` |
| Concurrent identical **mismatch** deliveries (start-together, 8 at once): all acknowledged, one anomaly, one history row, no worker resolves it | `ConcurrentIdenticalMismatchWebhooks_AreAllAcknowledged_AndLeaveOneRecordAndOneHistoryRow` |
| The store never reports "duplicate" for an insert that stored nothing, and confirms a real duplicate exists | `AnAnomalyInsertThatMatchesNoRow_IsNeverReportedAsADuplicate_AndARealDuplicateIsConfirmed` |
| The runbook's own least-privilege grants, executed in the throw-away container: exact privilege matrix, history trigger works with INSERT-only, UPDATE/DELETE/TRUNCATE/DISABLE TRIGGER/`session_replication_role`/DDL are refused by **privilege** (SQLSTATE 42501) | `TheDocumentedLeastPrivilegeRole_DoesWhatTheApplicationNeeds_AndCannotErase_OrDisableTheEvidenceProtection` (`PaymentsMigrationPostgreSqlTests`) |
| Forced failure and redelivery | `WebhookRedelivery_AfterAForcedDatabaseFailure_IsRecordedOnTheRetry_AndNeverAcknowledgedBefore` (a failing trigger is installed, then removed) |
| Migrations on populated data, forward and back; every rollback guard refuses with rows and succeeds without | `EveryFinancialMigration_AppliesToRepresentativeExistingRows_...`, `The*Rollback_Refuses...`, `TheFullChain_RollsBackAndForwardOnAnEmptyDatabase_...` (`PaymentsMigrationPostgreSqlTests`) |

What the race tests do **not** establish: which waiter PostgreSQL wakes first, or true parallel arbitration of the unique indexes (the parallel
redelivery cases start callers together but do not control the interleaving). Each such test asserts invariants that hold for either order and
says so. `ConcurrentIdenticalSuccessWebhooks_...` and the duplicate-resolution tests are start-together tests, not lock-barrier tests.

## 4. Exact steps to run them (prerequisites first)

1. **Docker engine running** (Docker Desktop on Windows). Check, read-only: `docker info` must print server details. Nothing else needs starting by
   hand: the fixture pulls the pinned image if it is not present (it needs network access to the registry) and starts and removes its own container.
2. **No special environment.** Do not set `ConnectionStrings__Catalog` or `PULSERESERVE_TEST_DATABASE` for the test run (the guard would refuse
   a mismatch, but there is no reason to provoke it). Do not point anything at the native PostgreSQL.
3. Build and confirm discovery (no Docker needed):

```bash
dotnet build
dotnet test tests/PulseReserve.IntegrationTests --no-build --list-tests
```

4. Run the stages in this order, reading **every** failure before the next stage. The filters were validated by discovery only (counts are methods
   selected by `--list-tests` with the same filter; theories add cases). Each test class starts its own container, so expect minutes per stage:

| Stage | What it covers | Filter | Methods (cases) |
| --- | --- | --- | --- |
| 1 | financial migrations, rollback guards, the least-privilege role | `FullyQualifiedName~PaymentsMigrationPostgreSqlTests` | 9 (9) |
| 2 | webhook acknowledgement and anomalies | `FullyQualifiedName~ProviderPaymentsHttpTests&(FullyQualifiedName~Webhook\|FullyQualifiedName~Anomal\|FullyQualifiedName~Mismatch\|FullyQualifiedName~UnknownAttempt\|FullyQualifiedName~BadSignature\|FullyQualifiedName~VerifiedCallback\|FullyQualifiedName~VerifiedWebhook)` | 15 (15) |
| 3 | transition history, append-only and evidence (late-success exceptions, anomalies, `*_AreProtectedByTheDatabase`) | `FullyQualifiedName~TransitionHistory_\|FullyQualifiedName~_AreProtectedByTheDatabase\|FullyQualifiedName~History_Keeps\|FullyQualifiedName~LateSuccess_\|FullyQualifiedName~ResolvingALateSuccessException\|FullyQualifiedName~ResolutionInFlight\|FullyQualifiedName~Anomal` | 27 (27) |
| 4 | reconciliation, refunds, outbox, tickets, concurrency and races | `FullyQualifiedName~PaymentWorkflowHttpTests\|(FullyQualifiedName~ProviderPaymentsHttpTests&(FullyQualifiedName~Concurrent\|FullyQualifiedName~Racing\|FullyQualifiedName~SuccessAndExpired\|FullyQualifiedName~ASuccessRacing\|FullyQualifiedName~Contending\|FullyQualifiedName~Stripe_ASuccessAfterTheReservationExpired))` | 55 (67) |
| 5 | the whole integration project (also catalog, reservation, identity, checkout and payment-start tests) | none | 206 (258) |

   (In a shell, quote the filter; the `\|` above is the table's escape for the vstest OR operator `|`.) Example for stage 1:

```bash
dotnet test tests/PulseReserve.IntegrationTests --no-build --filter "FullyQualifiedName~PaymentsMigrationPostgreSqlTests"
```

   Stages overlap on purpose (a test may belong to several); stage 5 is the only complete run. Tests that appear in none of stages 1-4 are the
   payment-start, eligibility and ambiguous-initiation tests of `ProviderPaymentsHttpTests` and the checkout/reservation/catalog tests.

How to read the outcome: a failure of the guard ("Test database guard]") means the host resolved a different database than the container - stop and
find out why before running anything else. A timeout from the race helper ("Expected N session(s) blocked on a lock") means a caller did not
contend for the locked row, i.e. the test did not test what it claims; treat it as a defect in the test or in the code under test, never as flakiness
to be retried away.

## 5. Scenarios that remain unexecuted even after a green run (needs different tooling)

* Behaviour under the **recommended least-privilege role** (document 24): the suite runs as the container's superuser, so privilege-denied paths
  are untested. The verification queries in document 24, section 6, are the check.
* A **populated copy of a real database** (lock duration of `CREATE TRIGGER` and constraint replacement, migration time).
* **Connection pooling** with the transaction-local `set_config(..., true)` labels under real concurrency (two requests with different contexts never
  seeing each other's values).
* **True parallel arbitration** of the unique indexes `uq_payment_anomalies_kind_dedupe` and `uq_financial_transitions_rejected_event` by many
  simultaneous deliveries of one event.
* `TRUNCATE`, owner and superuser bypass of the append-only triggers (by design not prevented; document 24).
* Any real provider: no sandbox call has been made (see the final report of this phase).

## 6. Invariant-to-test map: what each PostgreSQL test would prove, and what it would not

Every "PostgreSQL" entry below is **compiled, not executed**: until a green run on an isolated container exists, none of these is *proven* against
PostgreSQL. The map exists so that a green run can be read correctly. "Unit" means executed against in-memory stores.

| Invariant | PostgreSQL tests (would prove) | Unit evidence (executed) | Not covered by any test |
| --- | --- | --- | --- |
| A verified anomaly is acknowledged only after it is durably stored | `AMismatchingEvent_...` (3 deliveries, 200, one row, one history row, state untouched), `AMismatchWebhook_IsNotAcknowledged...`, `AnEventForAnUnknownAttempt_...`, `ConcurrentIdenticalMismatchWebhooks_...` | `WebhookAcknowledgementTests` (21 + mapping guard) | the commit itself failing after the insert (only the insert is failed by injection); a connection dropped mid-request |
| A failed database write is never acknowledged | `AMismatchWebhook_IsNotAcknowledged...` (a failing trigger on `payment_anomalies`: >= 500, zero rows, then 200 and one row on the retry), `WebhookRedelivery_AfterAForcedDatabaseFailure_...` | `APersistenceFailure_IsNotAcknowledged...`, `AFailureBeforeAnythingCouldBeJudged...` | failures injected anywhere except the anomaly insert and the verdict write |
| A duplicate is acknowledged only after the record is confirmed | `AnAnomalyInsertThatMatchesNoRow_IsNeverReportedAsADuplicate_...` (the defensive branch), `AMismatchingEvent_...`, `ConcurrentIdenticalMismatchWebhooks_...` | in-memory recorder semantics | READ COMMITTED behaviour of `ON CONFLICT DO NOTHING` against a concurrent uncommitted insert is assumed, not shown |
| Invalid signatures and wrong merchant contexts stay rejected | `Stripe_ABadSignatureOrWrongAmount_ChangesNothing`, `LateSuccess_ThatIsForgedOrDoesNotMatchTheOrder_...`, `AnEventForAnUnknownAttempt_...` (forged: 400, zero rows), `Paymob_AVerifiedCallback_...` (wrong integration: 400) | all Stripe and Paymob adapter rules incl. live/test and connected-account events | a Stripe wrong-context event through the real host (unit only) |
| A late success never silently turns a failed payment into a paid order | `LateSuccess_AfterARecordedFailure_...` (Failed verdict kept, order `PendingPayment`, reservation not confirmed, no tickets, outbox or refunds, workers create no task), `LateSuccess_For*`, `LateSuccess_RepeatedAndRacing_...` (8 parallel, one exception), `PaymentStart_RacingALateSuccess_...` (lock-barrier), `LateSuccess_WithdrawsAnAttemptAlreadyInFlight_...` | `PaymentWebhookServiceTests` late-success group | wake order of the lock-barrier waiters; a customer who pays the withdrawn attempt is **allowed** to complete the order (documented, shows as an additional charge) |
| An amount or currency mismatch never fulfils an order or issues tickets | `AMismatchingEvent_...` (order `PendingPayment`, zero tickets, outbox, refunds; reservation not `Confirmed`), `Stripe_ABadSignatureOrWrongAmount_...`, `LateSuccess_ThatIsForgedOrDoesNotMatchTheOrder_...`, the Paymob mismatch cases | `WebhookAcknowledgementTests` | - |
| Concurrent deliveries create no duplicate evidence | `ConcurrentIdenticalSuccessWebhooks_...` (8 at once: one transition, one ticket per seat, two outbox rows), `ConcurrentIdenticalMismatchWebhooks_...`, `LateSuccess_RepeatedAndRacing_...`, `TransitionHistory_KeepsAConflictingEventOnRecord_OncePerProviderEvent` (sequential), `ManualRefund_ConcurrentSubmissions_...` | - | the same conflicting (`Rejected`) event delivered concurrently; the interleaving is never controlled in start-together tests |
| Workers cannot overwrite or resolve exceptions and anomalies | `LateSuccess_AfterARecordedFailure_...` (a workflow pass leaves no task for the attempt), `AMismatchingEvent_...` (a workflow pass leaves the anomaly `Open`), `PaymentAnomalies_/PaymentExceptions_AreProtectedByTheDatabase` (facts immutable, resolved is final, no delete) | static: resolution exists only in the administrator service; no worker class references the tables (reviewed) | a workflow pass run **while** an administrator resolves (only `ResolutionInFlight_...` covers a resolution racing a redelivery) |
| A manual refund record is never presented as provider-confirmed | `ManualRefund_IsRecordedAtomically_...` (`provider_refund_id` stays null, provider moved no money, audit says "NOT provider-confirmed"), `ManualRefund_TheWorkerNeverTouchesIt_AndTheDatabaseKeepsItFinal`, `ManualRefund_ConstraintsReject...`, `History_KeepsAManualRefundApartFromAProviderConfirmedOne` (fact basis `AdministratorStatement` vs `ProviderVerified`) | service and Angular label tests | - |
| Refund completion is never inferred from an administrator statement | `Refund_ProviderConfirmation_IsRequiredBeforeAnythingIsClaimed` (Pending is not Refunded), `ResolvingALateSuccessException_...ChangesNothingElse` (no refund created), `Paymob_ALateSuccess_...NeverMarkedCompleteByGuessing` | `PaymentAnomalyOperationsTests` (statements change nothing) | - |
| Outbox and tickets match confirmed payment state | `PaidOrder_AlwaysHasItsOutboxEvents_CommittedWithThePaidState`, `NoTickets_ForUnpaidCancelledOrRefundPendingOrders`, `Tickets_AreUniquePerSeat_Immutable_...`, `ConcurrentDelivery_IssuesExactlyOneTicketPerSeat`, `RepeatedOutboxDelivery_...`, `Reconciliation_FinishesAPaymentThatCrashedBetweenTheVerdictAndTheSale_ExactlyOnce` | - | - |
| History is append-only **under the intended application role** | `TheDocumentedLeastPrivilegeRole_...` (role created from the runbook; `UPDATE`/`DELETE`/`TRUNCATE`/`DISABLE TRIGGER`/`session_replication_role`/DDL refused with 42501; INSERT-only suffices for the trigger) and, at trigger level as superuser, `TransitionHistory_IsAppendOnly_...` | static privilege-matrix test | the owner and superuser can bypass (by design); the other three schemas' grants; a `SECURITY DEFINER` variant |
| Every destructive rollback protects evidence before its first destructive statement | the 7 guard tests in `PaymentsMigrationPostgreSqlTests` (refuses with rows, succeeds without, migration history untouched) | static ordering test; the generated rollback scripts were inspected: the guard precedes the first `DROP` in each | lock duration and behaviour on a populated database |
| Locking and idempotency under real PostgreSQL semantics | all of the above | in-memory models only | - |

Subsets rather than proofs: the lock-barrier tests prove that callers were *observed waiting on a lock* in one database before release, not
that a particular interleaving occurred; the start-together tests prove invariants for whatever interleaving happened. Neither can be called
exhaustive.

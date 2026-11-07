# Architecture Document 10: Backend Audit and Execution Plan

**Project:** PulseReserve
**Audit date:** 2026-10-20
**Audited state:** commit `41b2ef4` plus the uncommitted working tree (most Catalog persistence, application, API and test code is untracked or modified)
**Type:** Read-only audit. No application source was changed. This file is the only file written.
**Status:** **Proposed. Awaiting approval. No implementation has started.**

Relationship to doc 09: `09-system-audit-and-target-architecture.md` is an earlier audit of the same tree. This document re-verifies findings against code, the build and tests, and adds a build and test result that doc 09 could not rely on. Where the two disagree, this document records what was observed on 2026-10-20.

---

## 1. Method and limitations

| Item | Detail |
|---|---|
| Read | All project files, central package props, `Program.cs`, `CatalogEndpoints.cs`, `ErrorMapping.cs`, the domain aggregates, handlers, repositories, EF configurations, the interceptor, the migration, test fixtures and the docs. |
| Ran | `dotnet build PulseReserve.sln`, `dotnet test` for `PulseReserve.UnitTests`, `dotnet test` for `PulseReserve.ArchitectureTests` (blocked by the build failure). |
| **Did not run** | `PulseReserve.IntegrationTests`. See §1.1. |
| Environment | SDK 10.0.401. Runtimes 8, 9 and 10 installed. Project targets `net9.0`. Docker CLI present, Docker daemon **not running**. |
| Not measured | No performance, load or query-count measurement was done. Every performance statement below is a code-reading inference and is labelled as such. |

### 1.1 Why the integration tests were not run

- `tests/PulseReserve.IntegrationTests/Catalog/PostgreSqlDatabaseFixture.cs` takes its connection string from `PULSERESERVE_TEST_DATABASE` (User, Process or Machine scope) or `ConnectionStrings__Catalog`. It falls back to `Host=localhost;Database=pulsereserve_test;Username=postgres`.
- The fixture applies migrations and `CleanupAsync` runs `TRUNCATE TABLE catalog.* ... CASCADE`. Pointing it at a database holding real data would destroy that data.
- Environment variables matching `PULSERESERVE` exist on this machine. I counted them but did not read their values, so I cannot say what database they point to.
- The Docker daemon is not running, so I could not start a disposable PostgreSQL container.
- The tests also cannot compile today (see C-01), because the test project references the AppHost project.

**Needed from you:** either start Docker Desktop, or confirm that a named throwaway PostgreSQL database may be used. I will not run the `TRUNCATE` fixture against anything else.

### 1.2 Verified results

| Command | Result |
|---|---|
| `dotnet build PulseReserve.sln` | **FAILED**, 14 errors, all in `src/PulseReserve.AppHost/Api/CatalogEndpoints.cs`. |
| `dotnet test tests/PulseReserve.UnitTests` | **Passed 60 of 60.** |
| `dotnet test tests/PulseReserve.ArchitectureTests` | **Did not run.** Compilation of the referenced AppHost project fails. |
| `dotnet test tests/PulseReserve.IntegrationTests` | **Not run.** See §1.1. |

`PROJECT.md` (modified, uncommitted) claims "129 tests passing (60 Unit, 59 Architecture, 10 Real PostgreSQL Integration)". Only the 60 unit tests are verified here. The test attribute count in the source (`[Fact]`/`[Theory]` declarations) is 45 architecture and 10 integration, but Theory rows can change the executed count, so I do not treat 59 as verified or refuted. That claim is **Unable to verify**. The same file says Phase 2D is "PLANNED" while the API layer exists in the working tree and does not compile.

---

## 2. Current implementation status

| Area | Status | Evidence |
|---|---|---|
| Catalog domain (Venue, SeatingLayout, Event, pricing) | Implemented, unit tested | `src/Modules/Catalog/.../Domain/` |
| Catalog application layer (11 commands, 3 queries as handlers) | Implemented, unit tested with fakes | `.../Features/` |
| Catalog persistence (EF Core 9, `catalog` schema, one migration) | Implemented, untracked in git. Not re-verified against PostgreSQL | `src/Shared/PulseReserve.Infrastructure/Persistence/Catalog/` |
| Aggregate-level optimistic concurrency interceptor | Implemented | `Interceptors/CatalogOptimisticConcurrencyInterceptor.cs` |
| Catalog HTTP API | Written, **does not compile** | `src/PulseReserve.AppHost/Api/CatalogEndpoints.cs` |
| Identity, Reservation, Payments, Notifications | **Empty shells**: one `Module.cs` with a static `Name` plus a marker interface each | `src/Modules/*/Module.cs` |
| Booking, holds, payments, tickets, QR check-in | **Not present** | no code |
| Outbox, Kafka, Redis | **Not present in code or package references.** README and PROJECT.md describe them as the intended architecture | grep of `src` for `Redis`, `Kafka`, `Outbox` returned nothing |
| Authentication, authorization, exception handler, health checks, rate limiting, OpenTelemetry, background services | **Not present** | grep of `src` for `AddAuthentication`, `UseExceptionHandler`, `AddHealthChecks`, `AddRateLimiter`, `OpenTelemetry`, `BackgroundService`, `IHostedService` returned nothing |

---

## 3. Verification of the preliminary findings

| # | Preliminary finding | Verdict | Notes |
|---|---|---|---|
| 1 | Only Catalog has business logic | **Confirmed** | §2 |
| 2 | Other modules are placeholders | **Confirmed** | §2 |
| 3 | AppHost compile errors: `.WithOpenApi()` and ambiguous `EventId` | **Confirmed** | C-01 |
| 4 | Domain events raised but not dispatched or cleared | **Confirmed** | H-02 |
| 5 | Lifecycle allows invalid transitions such as OnSale back to Published | **Confirmed** | H-01 |
| 6 | API responses do not expose version | **Confirmed** | H-03 |
| 7 | Seating layout load causes excessive joins | **Partially confirmed** | M-01: the query shape is the known cartesian pattern. No measurement was taken. |
| 8 | Case-insensitive uniqueness claimed but not enforced | **Confirmed** | H-04: enforced only in the domain, not in the database. |
| 9 | No auth, exception handling, health checks, rate limiting, observability, background services | **Confirmed** | M-03 to M-07. `GET /` returns a hard-coded "Healthy" and is not a health check. |
| 10 | Redis locks, Kafka, Outbox exist only in documentation | **Confirmed** | §2 |

---

## 4. Confirmed issues

Severity: **Critical** blocks all work. **High** breaks an invariant or a documented contract. **Medium** is a correctness or operability gap. **Low** is hygiene.

### C-01 (Critical): The solution does not build
- **Evidence:** 9 errors CS1061 (`WithOpenApi`) at `CatalogEndpoints.cs` lines 65, 81, 111, 129, 164, 200, 239, 273, 305. 5 errors CS0104 (`EventId` ambiguous between `PulseReserve.Modules.Catalog.Contracts.EventId` and `Microsoft.Extensions.Logging.EventId`) at lines 322, 354, 375, 410, 442.
- **Cause:** `WithOpenApi` lives in `Microsoft.AspNetCore.OpenApi`, which is not referenced. The project references only `Swashbuckle.AspNetCore` 10.2.3. The `EventId` clash comes from implicit usings in the Web SDK.
- **Impact:** AppHost, ArchitectureTests (59 claimed tests) and IntegrationTests cannot compile. CI would be red.
- **Fix direction:** remove the `.WithOpenApi()` calls (Swashbuckle already reads `Produces`/`WithName` metadata; `WithOpenApi` is also deprecated in .NET 10) and alias `EventId`. No new package needed.

### H-01 (High): Event lifecycle permits invalid transitions
- **Evidence:** `Event.Publish` (`Event.cs`) rejects only `Cancelled` and `Closed`. It therefore accepts `OnSale -> Published` and re-publishing an already `Published` event, resetting `PublishedAtUtc` and raising a duplicate `EventPublishedDomainEvent`. `PublishEventHandler` does not add a status guard before calling it.
- **Impact:** an event with sold or held seats can drop back to a pre-sale state, and consumers receive duplicate "published" signals. Unit tests do not cover this case (`EventDomainTests` has no OnSale-to-Published test).
- **Related:** `Event.Close` and `Event.Cancel` exist but no handler or endpoint exposes them. `Event.Close` does not raise a domain event. Cancellation therefore cannot be triggered, and when it is, nothing downstream (refunds, seat release) can react.

### H-02 (High): Domain events are never dispatched or cleared
- **Evidence:** `AggregateRoot.RaiseDomainEvent` stages events. `ClearDomainEvents` is called only from a unit test. `CatalogUnitOfWork.SaveChangesAsync` simply calls `DbContext.SaveChangesAsync`. No dispatcher, no outbox table. `EventPublishedIntegrationEvent`, `EventOnSaleIntegrationEvent`, `EventCancelledIntegrationEvent` exist in the Contracts project but nothing creates or publishes them.
- **Impact:** the events are silently dropped. Nothing is lost today because nothing consumes them, but this must be solved before Reservation depends on Catalog changes.

### H-03 (High): API does not expose the concurrency version, and the version is optional
- **Evidence:** `EventResponse`, and the Venue and SeatingLayout responses, have no `Version` field. Write requests carry `int? ExpectedVersion` (`ApiContracts.cs`). Repositories apply it only when non-null (`EventRepository.cs`, `SeatingLayoutRepository.cs`). Create endpoints return only an id.
- **Impact:** a client cannot learn the version it must send. When `ExpectedVersion` is omitted, the entity is loaded fresh and saved, which is a last-writer-wins lost update. Concurrency protection exists in the database but not as an API contract. Recommend ETag/`If-Match` or a required `expectedVersion`.

### H-04 (High): Case-insensitive uniqueness is not enforced by the database
- **Evidence:** the index names `uq_rows_section_label_lower`, `uq_seats_row_label_lower`, `uq_sections_layout_name_lower` are created on plain columns (migration lines about 298 to 347, for example `columns: new[] { "section_id", "label" }`). `docs/architecture/03-catalog-persistence-design.md` lines 331 to 333 say they use `lower(...)`. Case-insensitive comparison exists only in `SeatingLayout.AddSection/AddRow/AddSeat` (`OrdinalIgnoreCase`).
- **Impact:** two concurrent requests, or any write path that bypasses the aggregate, can create "VIP" and "vip" in the same layout. Also, the names are misleading. A duplicate that slips past the domain surfaces as an unhandled `DbUpdateException` (see M-03).

### H-05 (High): An event can be linked to a layout from a different venue
- **Evidence:** `CreateEventHandler` checks that the venue exists and that the layout exists, but never that `layout.VenueId == command.VenueId`. `EventConfiguration` has foreign keys `fk_events_venues` and `fk_events_seating_layouts` separately, with no composite constraint tying them together.
- **Impact:** an event at venue A can sell seats from venue B's layout. The check belongs in the aggregate or handler plus a composite FK. This was not listed in the preliminary findings.

### M-01 (Medium): Seating layout load shape
- **Evidence:** `SeatingLayoutRepository.GetByIdAsync` uses four `Include` calls (Sections, Rows, Seats, PriceTiers) with no `AsSplitQuery()`. Every handler that mutates a layout (`AddSeat`, `AddRow`, `AddSection`, `AddPriceTier`, `LockSeatingLayout`) loads the whole graph.
- **Inference, not measured:** with a single-query load the row count is the product of the collections, and loading all seats to add one seat is O(seat count). For a large venue this is a likely hot spot. **Unable to verify** the size at which it matters without a benchmark. Measure first (Phase 8) and consider split queries or targeted loads.

### M-02 (Medium): Event time and status checks do not use time rules
- `Event.CreateDraft` does not reject start times in the past. `StartSale` does not check `ScheduledStartUtc`. No sale window. Low urgency until reservations exist.

### M-03 (Medium): No global exception handling or ProblemDetails pipeline
- **Evidence:** only `DbUpdateConcurrencyException` is caught, per endpoint (`CatalogEndpoints.cs` lines 151, 187, 226, 260, 292, 385, 417, 450). `ErrorMapping.MapToStatusCode` classifies by substring (`Contains("Concurrency")`, `Contains("NotFound")`). Unique-constraint violations, FK violations and database outages return 500 with default behaviour.
- Domain "conflict" errors such as `Event.NotPublished`, `SeatingLayout.Locked` and the duplicate-name errors map to 400 rather than 409.

### M-04 (Medium): No authentication or authorization
- Every Catalog write endpoint, including create venue and start sale, is anonymous. `Identity` is an empty shell.

### M-05 (Medium): No health checks, readiness probe or rate limiting
- `GET /` returns a constant. Nothing checks PostgreSQL.

### M-06 (Medium): No structured logging, correlation IDs, metrics or tracing
- Only the default `appsettings.json` logging section exists.

### M-07 (Medium): Swagger, hosting and configuration hygiene
- `Program.cs` reads `PULSERESERVE_TEST_DATABASE` as the production connection-string override. A test-only variable name in application code is risky: a developer machine with that variable set and the host run normally would connect the API to the test database that the integration fixture truncates.
- `appsettings.json` has no `ConnectionStrings` section, so the host throws on start unless an env var is set. That is acceptable but undocumented.

### L-01 (Low): Hygiene
- Interceptor uses `DateTime.UtcNow` instead of `IDateTimeProvider` (`CatalogOptimisticConcurrencyInterceptor.cs:149`), so `UpdatedAtUtc` is untestable with a fake clock.
- `Directory.Packages.props`, `PROJECT.md`, `README.md` and about 40 source and test files are uncommitted. One baseline commit exists. There is no branch protection against losing this work.
- Line endings: Git warns LF will become CRLF for several files; `.editorconfig`/`.gitattributes` policy should be set.
- Module boundary architecture tests for the four empty modules pass trivially and prove nothing yet.
- `README.md` says `.NET 9 / ASP.NET Core 9`, `Redis` and `Kafka` in the present tense in its summary lines.

---

## 5. Architecture and dependency-boundary assessment

- **Layering is sound for Catalog.** Domain and Features reference only SharedKernel and `Catalog.Contracts`. Infrastructure references the Catalog module for mappings (`PulseReserve.Infrastructure.csproj`), so the dependency direction is Infrastructure to Domain. Persistence interfaces (`IEventRepository` and so on) live inside the Catalog module. This matches ADR-001 and ADR-003.
- **Shared Infrastructure project owns module persistence.** Catalog's `DbContext`, migrations and repositories live in `PulseReserve.Infrastructure`, not in the Catalog module. That centralises EF but means every future module adds code to a shared project, and a module cannot be extracted without untangling it. Recommendation: keep it for now, but decide in an ADR (D-04) whether new modules own their persistence in their own assembly. Do not move Catalog code without approval.
- **Cross-module boundaries:** the Contracts projects are the only cross-module surface, which is the right shape. Today there are no real cross-module calls, so there is nothing to violate yet. Reservation will need seat identity and price from Catalog. The audit recommends a query contract in `Catalog.Contracts` (read-only DTOs, already partly there as `EventCatalogDto`, `SeatMetadataDto`) rather than reading `catalog.*` tables.
- **Composition root:** `Program.cs` references module `Name` properties as "anchors". There is no module registration contract (`AddXModule`). Each module will need one.
- **Handlers are concrete classes** registered by hand in `CatalogPersistenceExtensions`. Fine at this size. It will not scale to many handlers without a registration convention.
- **Inventory model gap:** Catalog describes seats. There is no per-event seat inventory/state table, which is where ownership and holds will live. This is the central design item for the Reservation module (§7, Phase 3).

---

## 6. Database and migration assessment

- One migration, `20261003131943_InitialCatalogCreate`, and a snapshot. Schema `catalog`, snake_case, UUID keys, `NUMERIC(18,4)` money, composite FKs for hierarchy (`fk_rows_sections_composite` and similar), `version` integer concurrency token on `venues`, `seating_layouts`, `events`.
- Good: hierarchy integrity is enforced in the database, and the optimistic-concurrency design (root version bumped for child changes) is sound.
- Gaps: H-04 (case-insensitive indexes), H-05 (no venue-consistency constraint), no `CHECK` for the event state machine or for `scheduled_start < scheduled_end` was not confirmed in this pass (**Unable to verify**; read `Migrations/...Designer.cs` before relying on it).
- Not verified against PostgreSQL in this audit (§1.1). Docs 07 and PROJECT.md report a pass on PostgreSQL 18 at an earlier time. I cannot confirm that.
- No seed data, no migration application strategy for deployment (startup migration vs. a separate step), and no backup/restore note.
- All migrations and the model snapshot are untracked in git. Do not delete or regenerate them without approval. A change such as H-04 means a **new** migration, not an edit of the existing one.

---

## 7. Test coverage gaps

| Gap | Detail |
|---|---|
| Arch and integration tests cannot compile | C-01 |
| No test for illegal event transitions | H-01 |
| No API-level tests | no `WebApplicationFactory` tests; `FoundationIntegrationTests` is one test |
| No race tests beyond Catalog optimistic concurrency | no hold, double-booking, expiry or check-in tests (the features do not exist) |
| No tests for H-03, H-04, H-05 | |
| No test for unique-violation mapping | |
| Integration tests share one mutable database and use `TRUNCATE` | not parallel-safe, and dangerous if pointed at a non-disposable database (see §1.1) |
| No coverage tooling reports | `coverlet.collector` is referenced but no coverage was run |

---

## 8. Architecture decisions (proposed defaults)

These apply the principles in the brief. Each needs your approval before it becomes an ADR.

1. **Modular monolith** stays. No microservices.
2. **PostgreSQL is authoritative** for seat ownership, reservation state and ticket use. Add a Reservation-owned `event_seats` inventory (one row per event and seat) with a status column and a unique key on `(event_id, seat_id)`.
3. **Booking correctness through the database.** Acquire a seat with a single conditional statement, for example `UPDATE event_seats SET status='Held', hold_id=@h, hold_expires_at=@t WHERE event_id=@e AND seat_id=ANY(@s) AND (status='Available' OR (status='Held' AND hold_expires_at <= now()))`, and succeed only if the affected row count equals the number requested, inside one transaction. The expiry predicate is part of the acquire condition, so a delayed cleanup job cannot cause a stale hold to block a sale. `READ COMMITTED` plus the conditional update is sufficient. Use `SERIALIZABLE` only where a multi-row invariant needs it, and retry on SQLSTATE 40001.
4. **Hold expiry** uses the database clock (`now()`), not application clocks. A background sweeper is an optimisation that only reclaims rows and emits events.
5. **No Redis lock.** Revisit only after a measured requirement (Phase 8).
6. **Transactional Outbox first**, written in the same transaction as the state change, processed by a polling background service with `FOR UPDATE SKIP LOCKED`, idempotent consumers, and retry with a dead-letter state. Kafka only after that works and a consumer needs it.
7. **Payments** behind `IPaymentProvider`, with a simulated provider. Every payment attempt carries a client-supplied idempotency key with a unique constraint. An `Unknown`/`Pending` outcome is never retried blindly; a reconciliation job queries the provider by idempotency key and settles the payment.
8. **QR check-in:** random 128-bit-plus tokens, stored as a SHA-256 hash with a unique index; check-in is `UPDATE tickets SET status='Used', used_at=now() WHERE id=@id AND status='Issued'`, success iff one row is affected. A second concurrent scan affects zero rows and is rejected.
9. **Authentication:** same-origin secure cookie auth (HttpOnly, Secure, SameSite=Lax or Strict, antiforgery for unsafe methods) with ASP.NET Core Identity or a minimal user store. This suits an Angular app served behind the same origin as the API. If the Angular app is hosted on a different origin, switch to the BFF pattern or token auth; this depends on the deployment (D-03).
10. **Observability:** Serilog is a third-party package, so first use built-in `ILogger` with JSON console logging and `ActivityId`/correlation middleware, built-in `AddHealthChecks`, `System.Diagnostics.Metrics`, and OpenTelemetry (Apache-2.0) only if you approve the dependency.
11. **Framework version.** `net9.0` is STS and, per Microsoft's support policy, **reaches end of support on 10 November 2026**, 21 days after this audit. .NET 10 is the current LTS (supported to November 2028). SDK 10.0.401 and the 10.0.12 runtimes are already installed here. Recommendation: upgrade to `net10.0` in Phase 1, after the build is green, as a separate commit. Compatibility needs checking for Npgsql EF Core (a 10.x release exists for EF Core 10), EF Core 10, Swashbuckle, NetArchTest.Rules 1.3.2, xUnit 2.9.2. Do not upgrade packages blindly; change one thing at a time and re-run tests. Sources: [Microsoft support policy](https://dotnet.microsoft.com/platform/support-policy), [.NET 8/9 end-of-support notice](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/).
12. **Module ownership:** Reservation owns inventory and holds; Payments owns payments; ticket issuance and check-in live in Reservation (or a Ticketing module, D-05). Cross-module calls go through Contracts interfaces and the outbox, never through another module's tables.

---

## 9. Prioritised implementation phases

Each phase ends with a green build, tests run and reported honestly, and a short report of changed files, risks and the next step.

### Phase 1: Build stability and broken Catalog invariants
- **Scope:** fix C-01; fix H-01 (state machine with explicit allowed transitions; reject re-publish, `OnSale -> Published`); expose Close/Cancel only if approved; fix H-05 (venue/layout consistency in the aggregate or handler, plus a composite FK in a new migration if approved); replace `PULSERESERVE_TEST_DATABASE` use in `Program.cs` (M-07); use `IDateTimeProvider` in the interceptor.
- **Optional, needs approval (D-02):** retarget to `net10.0`.
- **Acceptance:** `dotnet build` has zero warnings or errors; all unit and architecture tests pass; new unit tests cover every invalid transition and the venue mismatch; integration tests run only against an approved disposable database.

### Phase 2: Database correctness and API contract
- **Scope:** H-03 (return `Version`, use `ETag`/`If-Match` or a required expected version); H-04 (new migration to `lower(...)` functional unique indexes, or `citext`; fix doc 03 wording); global exception handler and `ProblemDetails`; map unique, FK and concurrency violations to 409/400; split-query or targeted loading for layouts (M-01, only after measuring).
- **Acceptance:** concurrency test passes through the HTTP layer; duplicate-name race test (two parallel creates differing by case) yields exactly one success; no 500 for known constraint violations; migration applies cleanly on an empty database and on one created from the current migration.

### Phase 3: Reservation lifecycle, holds, expiry, concurrency
- **Scope:** Reservation module: `event_seats` inventory seeded when an event goes OnSale (consuming `EventOnSale`), `holds` and reservations with the conditional-update acquire from §8.3, expiry evaluated at read and write time, sweeper as optimisation.
- **Acceptance:** a test with N parallel clients contending for M seats, run against real PostgreSQL, proves zero double-booking; a test with a stopped sweeper proves an expired hold can be re-acquired; partial acquisition of a multi-seat request rolls back fully.

### Phase 4: Payments and ticket issuance
- **Scope:** `IPaymentProvider`, simulated provider, idempotency keys with a unique constraint, payment state machine including `Unknown`, reconciliation; ticket issuance after confirmed payment, exactly once.
- **Acceptance:** same idempotency key twice yields one charge; simulated timeout leads to `Unknown` and reconciliation resolves it without a double charge; ticket issued exactly once under duplicate callbacks.

### Phase 5: Atomic QR check-in
- **Scope:** token generation, hashed storage, conditional-update check-in endpoint.
- **Acceptance:** 50 concurrent scans of one ticket give exactly one success; the plaintext token is never stored or logged.

### Phase 6: Transactional Outbox and background processing
- **Scope:** outbox table per DbContext write path, dispatcher hosted service, domain-event to outbox mapping (resolves H-02), inbox/idempotency for consumers, hold-expiry sweeper, retry and dead-letter.
- **Acceptance:** killing the process between commit and dispatch loses no message; duplicates are harmless; two dispatcher instances do not process the same row.

### Phase 7: Security, observability, operations
- **Scope:** authentication and authorisation (M-04), roles for organiser, customer, scanner; rate limiting on hold and payment endpoints; health checks (live and ready, including PostgreSQL); structured logs, correlation IDs, metrics, optional OpenTelemetry.
- **Acceptance:** anonymous write calls return 401/403; `/health/ready` fails when PostgreSQL is down; a request carries one correlation ID through logs.

### Phase 8: Measured performance work
- **Scope:** benchmark first (layout load, hold acquisition under contention), then optimise (split queries, indexes, read models). Redis only if measurements justify it.
- **Acceptance:** results are recorded with the environment and method, and no claim is made without them.

---

## 10. Risks and decisions that need your approval

| ID | Decision or risk | Recommendation |
|---|---|---|
| D-01 | **Disposable PostgreSQL for integration tests.** Docker daemon is stopped. Fixture truncates tables. | Start Docker, or name a throwaway database. Add a guard so the fixture refuses a database whose name does not contain `test`. |
| D-02 | **Upgrade `net9.0` to `net10.0`** (.NET 9 EOL 2026-11-10). | Approve, as a separate commit in Phase 1 after the build is green. |
| D-03 | **Auth model** depends on how Angular is deployed (same origin vs separate). | Same-origin cookie auth by default. Tell me if the SPA will be on another origin. |
| D-04 | **Persistence ownership** for new modules (shared Infrastructure project vs. per-module). | Per-module for new modules; leave Catalog where it is. |
| D-05 | **Ticketing** inside Reservation or its own module. | Keep inside Reservation until Payments is complete. |
| D-06 | **New migrations** (H-04, H-05) rather than editing the existing one. | Approve additive migrations only. No deletion of existing migrations. |
| D-07 | **Event Close/Cancel endpoints** are missing. | Approve exposing them with explicit semantics, including what happens to held or sold seats. |
| D-08 | **Commit the uncommitted work** before changes begin so the audit baseline is recoverable. | Recommend a commit on a feature branch. I will not commit without your instruction. |
| D-09 | **Third-party packages** (OpenTelemetry, a logging package). | Use built-in facilities first. OpenTelemetry (Apache-2.0) is the only candidate and is optional. |
| R-01 | Documents (README, PROJECT.md, doc 03, doc 09) describe unbuilt features and unverified test counts in the present tense. | Correct them as each phase lands. |
| R-02 | Unmeasured performance claims (sub-100 ms, high throughput) in README. | Treat as goals, not facts. |

---

## 11. Stopping point

The audit is complete. I have stopped. I will not implement anything until you approve this plan, and I need an answer on D-01 and D-02 first.

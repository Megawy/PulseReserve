# 24. Database roles, privileges and the limits of the append-only protection

Evidence labels are those of document 19 (**U** executed unit test, **C** integration test compiled but **not executed**, **S** source review,
**SQL-syntax** parsed offline). Nothing in this document was verified against a PostgreSQL server: no role was created, no privilege checked, no
trigger fired. The verification queries in section 6 exist precisely so that an operator can do that on a real deployment.

## 1. What the repository provisions today: nothing

| Fact (from the source) | Consequence |
| --- | --- |
| `compose.dev.yaml` connects the API as `POSTGRES_USER`, the bootstrap role of the dev container, which is a **superuser** that owns every object | Development only. Never reuse its setup, role or password anywhere else. |
| There is **no** `CREATE ROLE`, `GRANT`, `REVOKE` or `ALTER DEFAULT PRIVILEGES` anywhere in the repository (migrations, scripts, compose) | A deployment gets whatever role its connection string names. Least privilege is the operator's job, so this document is a runbook (U: `FinancialDatabaseBoundaryTests` fails if application code starts changing privileges). |
| All four modules (catalog, identity, reservation, payments) share the single connection string `ConnectionStrings:Catalog` | One application role needs privileges on **all four schemas**. This document derives only the `payments` schema (the financial one); the other three need the same review, which has **not** been done. |
| `Database:MigrateOnStartup` is honoured only in the `Development` environment | In every other environment migrations are a separate, deliberate step run by a different identity than the application. That makes role separation possible without code changes. |
| The integration fixture runs as the superuser of its own throw-away Testcontainers PostgreSQL | Tests prove nothing about privileges. |

## 2. What the triggers protect against, and what they do not

The history, anomaly, exception, audit-log, ticket and refund triggers enforce rules **on every write that goes through ordinary SQL by a role
that is not allowed to bypass them**. They are a guard against application bugs and against an application credential that is compromised or
misused, **not** against whoever controls the database.

| Actor | Can it change or erase a history/anomaly/audit row? | Why |
| --- | --- | --- |
| The application role, as recommended below | No | no `UPDATE`/`DELETE`/`TRUNCATE` privilege on those tables, and the triggers reject it even if granted (C) |
| The application role if it were granted `UPDATE`/`DELETE` | No for `financial_transitions`, `admin_audit_log`, `tickets`, `payment_anomalies`, `payment_exceptions` | the triggers raise (C). It could still alter a still-open row's permitted columns |
| The table **owner** | **Yes** | `ALTER TABLE ... DISABLE TRIGGER`, `DROP TRIGGER`, `DROP TABLE`, `TRUNCATE` are owner operations |
| A **superuser** | **Yes** | also `SET session_replication_role = replica` skips triggers; superusers bypass all privileges |
| A member of `pg_write_all_data` | Not past the triggers (they still fire), but it bypasses every table privilege, so the least-privilege matrix below would mean nothing | Never grant it to the application role |
| Anyone who can restore an older backup over the database | **Yes** | no protection in the database against that |

So the append-only guarantee is real against the application and against mistakes, and **absent** against a privileged operator. Do not describe
it as tamper-proof evidence. Compensating controls that are the operator's to put in place (none exists in the repository): separate duties for
the owner/migrator credential; PostgreSQL's own statement logging for DDL (`log_statement = 'ddl'`) or the free `pgaudit` extension; WAL archiving
and regular, **restore-tested** backups; periodic export of the history/anomaly/audit tables to storage that the database operator cannot
modify.

## 3. Recommended role model

Three roles, none with a password in this repository. Passwords come from the platform's secret store and are passed to `psql` as variables
(`-v app_password=...`), never written into a file or a ticket.

| Role | Purpose | Attributes |
| --- | --- | --- |
| `pulsereserve_owner` | owns the schemas and tables; used **only** by the deployment pipeline to run migrations and the grants below | `LOGIN`, not superuser; its credentials are never given to the application |
| `pulsereserve_app` | the role named in `ConnectionStrings:Catalog` | `LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`, **not** a member of `pulsereserve_owner` or of any `pg_*` administrative role, owns nothing |
| `pulsereserve_ops_readonly` (optional) | read-only inspection of the financial tables by humans/auditors | `LOGIN`, `SELECT` only |

Because ownership decides who can disable triggers, **the migration run must create the objects as `pulsereserve_owner`** (connect as that
role, or `SET ROLE pulsereserve_owner` before running `dotnet ef database update` / the idempotent script).

## 4. The privileges the application needs, per table

Derived by reading every SQL statement in the stores, and pinned by a test (U: `FinancialDatabaseBoundaryTests.TheApplicationAsksForExactlyTheDocumentedPrivileges_OnEveryPaymentsTable`):
if the code ever needs more, that test fails until this table, the test's matrix and the deployment grants are changed together. The
application **never** issues `DELETE`, `TRUNCATE`, DDL, `GRANT`/`REVOKE` or trigger/`session_replication_role` statements (U).

| Table | Privileges | Notes |
| --- | --- | --- |
| `payments.admin_audit_log` | `SELECT, INSERT` | append-only |
| `payments.financial_transitions` | `SELECT, INSERT` | append-only; the insert is made by the history trigger **as the invoking role** (see 5) |
| `payments.notification_deliveries` | `SELECT, INSERT` | ledger |
| `payments.order_items` | `SELECT, INSERT` | immutable once written |
| `payments.tickets` | `SELECT, INSERT` | append-only |
| `payments.orders` | `SELECT, INSERT, UPDATE` | status moves are constrained by triggers |
| `payments.payment_attempts` | `SELECT, INSERT, UPDATE` | |
| `payments.refunds` | `SELECT, INSERT, UPDATE` | |
| `payments.payment_exceptions` | `SELECT, INSERT, UPDATE` | the only update is the resolution |
| `payments.payment_anomalies` | `SELECT, INSERT, UPDATE` | the only update is the resolution |
| `payments.reconciliation_tasks` | `SELECT, INSERT, UPDATE` | work queue |
| `payments.outbox_events` | `SELECT, INSERT, UPDATE` | work queue |

The application role must **not** hold: `DELETE`, `TRUNCATE`, `REFERENCES` or `TRIGGER` on any payments table; `UPDATE` on the insert-only tables
above; `CREATE` on schema `payments` (or `public`); ownership of anything; membership in `pulsereserve_owner`, `pg_write_all_data` or any
`pg_*` administrative role.

Template (run as the database administrator on the **target** database, after the migrations, and again after every migration that adds a
table; it is deliberately explicit per table instead of `ALTER DEFAULT PRIVILEGES`, which would silently grant `UPDATE` on future append-only
tables). Not executed anywhere:

```sql
-- psql -v app_password=... -f grants.sql   (the password comes from the secret store, never from this file)
CREATE ROLE pulsereserve_app LOGIN PASSWORD :'app_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;

REVOKE ALL ON SCHEMA payments FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA payments FROM PUBLIC;
GRANT USAGE ON SCHEMA payments TO pulsereserve_app;

GRANT SELECT, INSERT ON
    payments.admin_audit_log, payments.financial_transitions, payments.notification_deliveries,
    payments.order_items, payments.tickets
  TO pulsereserve_app;

GRANT SELECT, INSERT, UPDATE ON
    payments.orders, payments.payment_attempts, payments.refunds, payments.payment_exceptions,
    payments.payment_anomalies, payments.reconciliation_tasks, payments.outbox_events
  TO pulsereserve_app;
```

## 5. Two honest caveats

1. **The history trigger runs as the invoking role.** `payments.record_financial_transition()` is a normal (`SECURITY INVOKER`) function, so the
   application role needs `INSERT` on `financial_transitions`, and a compromised application credential could therefore insert rows directly
   (it still cannot change or delete existing ones). It could equally set the transaction-local labels (`source`, `fact_basis`, `reason`) to
   anything, because they come from the application: the history is trustworthy about *what moved*, not about *who really caused it* when the
   application itself is hostile. A `SECURITY DEFINER` function owned by `pulsereserve_owner` (with `SET search_path = pg_catalog, payments`) would
   remove the direct-insert path. It was **not** implemented: changing trigger privileges without a PostgreSQL to test against risks breaking every
   financial write, and the benefit is limited by the point above. Listed as a follow-up requiring a real database.
2. **Column-level `UPDATE` grants** (for example only the resolution columns of `payment_anomalies`) would narrow the matrix further. The
   triggers already enforce this, so it is optional hardening; not provided because it is unverified.

## 6. Verification queries (read-only; run as an administrator on the deployed database)

Expected results in the comment. **None has been executed**; they were written to be run during the first deployment rehearsal.

```sql
-- 1. The application role is unprivileged: expect  f | f | f | f | f | t
SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolcanlogin
FROM pg_roles WHERE rolname = 'pulsereserve_app';

-- 2. It is a member of no other role: expect zero rows
SELECT r.rolname FROM pg_auth_members m
JOIN pg_roles r ON r.oid = m.roleid JOIN pg_roles u ON u.oid = m.member
WHERE u.rolname = 'pulsereserve_app';

-- 3. It owns nothing in the financial schema: expect zero rows from both
SELECT tablename FROM pg_tables WHERE schemaname = 'payments' AND tableowner = 'pulsereserve_app';
SELECT nspname FROM pg_namespace WHERE nspname = 'payments' AND nspowner = (SELECT oid FROM pg_roles WHERE rolname = 'pulsereserve_app');

-- 4. Effective table privileges equal the matrix in section 4 (compare line by line)
SELECT c.relname, string_agg(p.priv, ', ' ORDER BY p.priv) AS privileges
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER')) AS p(priv)
WHERE n.nspname = 'payments' AND c.relkind = 'r' AND has_table_privilege('pulsereserve_app', c.oid, p.priv)
GROUP BY c.relname ORDER BY c.relname;

-- 5. It cannot create objects in the schema: expect false
SELECT has_schema_privilege('pulsereserve_app', 'payments', 'CREATE');

-- 6. Every financial trigger is enabled (origin): expect zero rows
SELECT c.relname, t.tgname, t.tgenabled FROM pg_trigger t
JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'payments' AND NOT t.tgisinternal AND t.tgenabled <> 'O';

-- 7. Reproduce the protection as the application role (inside a transaction, rolled back).
--    Expect "permission denied" for each; if one succeeds, the grants are wrong.
SET ROLE pulsereserve_app;
BEGIN; UPDATE payments.financial_transitions SET reason = reason; ROLLBACK;
BEGIN; DELETE FROM payments.payment_anomalies;                    ROLLBACK;
BEGIN; TRUNCATE payments.admin_audit_log;                          ROLLBACK;
RESET ROLE;
```

## 7. Rollback guards

Every migration whose `Down` would destroy financial or customer-facing records now **refuses** while such records exist (explicit
`RAISE EXCEPTION` with `ERRCODE 23000`, raised before the first `DROP`, so the surrounding transaction changes nothing):

| Migration | `Down` refuses while ... holds a row |
| --- | --- |
| `20261021125806_InitialPayments` | `payments.orders` (which carries order items and payment attempts) |
| `20261021132335_ReconciliationRefundsOutboxTickets` | `payments.refunds`, `payments.tickets`, `payments.admin_audit_log`, `payments.notification_deliveries` (work queues - tasks and outbox events - do not block) |
| `20261021204239_AttemptCheckoutUrl` | no guard: it drops only the stored hosted-page link, which is a convenience copy and not evidence |
| `20261022112035_ManualRefundRecord` | `payments.refunds` in state `ManuallyRecorded` |
| `20261022140828_LateSuccessPaymentExceptions` | `payments.payment_exceptions` |
| `20261022151129_FinancialTransitionHistory` | `payments.financial_transitions` |
| `20261022152016_PaymentAnomalies` | `payments.payment_anomalies` |

A static test (U: `EveryMigrationRollbackThatDropsATable_RefusesFirst_WhileFinancialRowsExist`, `OnlyTheCheckoutLinkColumn_MayBeDroppedByARollbackWithoutAGuard`)
fails if a future `Down` drops a table or column without one. The PostgreSQL behaviour of each guard (refuses with rows, succeeds without, leaves
the migration history untouched) is covered by compiled-only tests in `PaymentsMigrationPostgreSqlTests` (C). These guards are a **safety
catch against an accidental rollback, not a security boundary**: the owner can drop the tables directly (section 2). To truly remove a table:
export the rows, delete them deliberately as the owner, then run the rollback.

## 7b. A test that runs these grants (compiled, not executed)

`TheDocumentedLeastPrivilegeRole_DoesWhatTheApplicationNeeds_AndCannotErase_OrDisableTheEvidenceProtection` (in `PaymentsMigrationPostgreSqlTests`)
reads the SQL block of section 4 **from this document**, creates the role in a throw-away database inside the Testcontainers container (unique
name, password generated for the run), and checks as that role: the effective privilege matrix equals section 4; an order insert and a status
change work and the history trigger writes its rows with only `INSERT` on the history table; and `UPDATE`/`DELETE`/`TRUNCATE` on the evidence
tables, `ALTER TABLE ... DISABLE TRIGGER`, `DROP TRIGGER`/`DROP TABLE`, `SET session_replication_role` and `CREATE TABLE` are all refused with
SQLSTATE 42501 (privilege), not by a trigger. If the document and the grants drift, that test fails. It has **not been executed**; the SQL block
was only syntax-parsed offline.

## 8. What remains unverified

* every statement in this document against a real PostgreSQL server (roles, grants, the verification queries, trigger behaviour under the
  recommended role);
* the same review for the `catalog`, `identity` and `reservation` schemas that share the application's connection string;
* whether the managed PostgreSQL service in use allows the role attributes above (some managed services restrict `SUPERUSER`-adjacent settings
  differently).

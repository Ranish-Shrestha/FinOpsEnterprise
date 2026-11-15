# Verification log

What was actually run, when, and what was not. The newest entry comes first. CI (`.github/workflows/ci.yml`) runs the same checks on every push and pull request once the repository is on GitHub.

## Reproduce

From the repository root, with the README prerequisites installed:

```powershell
dotnet restore FinOpsEnterprise.slnx --locked-mode
dotnet build FinOpsEnterprise.slnx --no-restore
dotnet test FinOpsEnterprise.slnx --no-build --filter "Category!=Database"

# Requires Docker Desktop's Linux engine. Starts a disposable SQL Server container.
dotnet test tests/FinOps.IntegrationTests --no-build --filter "Category=Database"

dotnet tool restore
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet ef migrations has-pending-model-changes --project src/FinOps.Infrastructure --startup-project src/FinOps.Api --no-build

Push-Location src/finops-web
npm ci
npm run typecheck
npm test
npm run build
npm audit --omit=dev
Pop-Location
```

> **Windows:** run the Angular tests from a path with an **uppercase** drive letter (`F:\...`, not `f:\...`). Some shells, such as Git Bash and some VS Code terminals, report a lowercase drive. Vitest then loads modules twice and every spec fails with "Vitest failed to find the runner". PowerShell opened normally is fine.

---

## 2026-11-14: Phase 7, Azure deployment (templates and CD) and portfolio

Scope: [ADR 0007](decisions/0007-azure-deployment.md). The owner chose to write and validate the templates and CD workflow but **not deploy yet**. Worked on `main`.

| Check | Result |
| --- | --- |
| .NET locked restore and Release build | Passed; 0 warnings, 0 errors. No new NuGet packages |
| Unit tests | 131 passed |
| HTTP tests (`Category!=Database`) | 46 passed. New `DeploymentHostTests`: CORS preflight and actual request for the configured origin; no grant for others or for credentials; invalid origins stop startup; the outbox runs in the API only when `Messaging:RunInApi=true` |
| Real SQL Server tests (`Category=Database`) | 77 passed. New `DeploymentTests` (the class passed 3 more consecutive runs): the full flow (create → post, reports including Excel and audit activity, outbox admin, worker, notifications) runs as a login holding **only** the runtime grants. DELETE, audit rewrites, identity writes, CREATE TABLE and DROP PROCEDURE are refused (errors 229, 262, 3701). Grant input is validated |
| EF model drift | None (no migration in this phase) |
| Angular | Typecheck clean; 41 tests passed (8 new for runtime config and API-origin routing); production build 244.4 kB, which now includes `config.json` and `staticwebapp.config.json` |
| Playwright E2E | 3 journeys passed against an isolated stack, with the SPA now loading `/config.json` at startup. The screenshots spec regenerated the 6 README images |
| Container images (`docker build --target api/worker`) | Built. 351 MB and 349 MB after removing other platforms' native assets (from about 500 MB). They run as UID 1654 with no shell |
| Images against a throwaway SQL Server | `migrate` applied migrations in Production. `seed-demo-data` was refused in Production and worked in Development. `grant-app-access` without a name exited 2 |
| PDF and Excel on Linux | `GET /api/reports/expense-summary?format=pdf` returned a valid `%PDF` (33,696 bytes) and the `xlsx` a valid zip package, on the chiseled image. This closes the Phase 4 "QuestPDF on Linux" item |
| Production-mode container | Behind simulated ingress (`X-Forwarded-Proto: https`): readiness 200 with HSTS; unknown `Host` 400; no token 401; mock header 401; CORS preflight 204 for the configured origin; in-process outbox worker started. The worker image ran as non-root |
| `infra/sql/provision-user.sql` | Inserted a user, role and membership. A second run was idempotent; the placeholder object ID is refused |
| `TYPE = E` user statement | The generated `CREATE USER … WITH SID = 0xFF19966F…, TYPE = E` was checked for byte order against the client ID. It can't be executed outside Azure SQL |

Found and fixed during this phase:

- **`EXEC()` with `QUOTENAME()`.** The grant SQL called `QUOTENAME()` inside `EXEC(...)`, which T-SQL rejects; it would have failed in the deploy pipeline. Found by `DeploymentTests`.
- **`migrate` needed Entra settings.** It couldn't run in Production because the web host's security setup demanded Entra settings. Admin commands now use a minimal host that registers only the data layer. Found by running the image.
- **Seed check came too late.** `seed-demo-data` checked the environment after resolving the database, so CI would have passed for the wrong reason. It now refuses first.
- **`CREATE USER … FROM EXTERNAL PROVIDER`.** Run by a service principal, it needs Directory Readers or Graph permissions on the SQL server. Replaced with `WITH SID = <client id>, TYPE = E`.
- **Probes and the host filter.** A host allow-list would have failed Container Apps' IP-addressed probes; the host is now filtered at ingress.
- **Baseline measured at a different time.** The performance harness measured its commit baseline separately from the endpoints, which made "the report is commit-bound" look doubtful. Interleaved probes now confirm it (see [performance](performance/expense-queries.md#the-report-is-bound-by-one-commit-not-by-its-query)).

**Final local latency run:**
- queue p95 33.9 ms; my expenses 37.9 ms; report 285.4 ms (met this time; 263–411 ms across runs).
- Commit probe p95 130.8 ms.
- Outbox, earlier full run: 2.08 s.

**Unexplained:** once, the first run of `DeploymentTests`' end-to-end test got a 500 on create. It did not recur in 5 later runs. The test now includes server-side errors in its failure message.

Not verified (needs Azure, which was deliberately not used):

- **Deployment mechanics:**
  - the Bicep compile and lint (downloading the Bicep CLI here was blocked; the new CI `deployables` job runs it);
  - an actual deployment, the managed-identity SQL login and `TYPE = E` user creation;
  - the deploy workflow itself.
- **Runtime services:** Entra sign-in end to end, the managed OpenTelemetry agent, and Static Web Apps headers.
- **Unobserved CI:** the new `deployables` and `e2e` jobs have not run yet, and neither has the CI enforcement of the Phase 6 targets.

---

## 2026-11-14: Phase 6, quality and DevOps (targets, telemetry, E2E)

Scope: [ADR 0006](decisions/0006-quality-targets-and-telemetry.md). The owner confirmed the targets before measuring. Worked on `main`.

| Check | Result |
| --- | --- |
| .NET locked restore and Release build | Passed; 0 warnings, 0 errors. New packages: OpenTelemetry.Extensions.Hosting, Exporter.OpenTelemetryProtocol, Instrumentation.AspNetCore and Instrumentation.SqlClient, all 1.20.1. No NuGet audit warnings |
| Unit tests | 131 passed (new: trace-parent validation) |
| HTTP tests (`Category!=Database`) | 40 passed (new: telemetry registered only with an OTLP endpoint; invalid endpoint fails at startup) |
| Real SQL Server tests (`Category=Database`) | 68 passed. New: 2 `PerformanceTests` and 2 `TelemetryTests`. `MessagingIntegrationTests` was updated for the handler order |
| EF model drift | None after migration `OutboxTraceContext` (adds `TraceParent`) |
| Angular | Typecheck clean; 33 tests passed; production build 243.2 kB initial |
| Playwright E2E (`npm run e2e`) | 3 journeys passed: 3 consecutive runs, then 2 more with telemetry exporting. Run on an isolated stack against a throwaway SQL Server container (since removed) |
| Telemetry, end to end | With the Aspire dashboard 13.6.2, one trace showed `POST /api/expenses/{id}/post`, its SQL, then the worker's `process expense.posted.v1` with both handler spans. The worker's outbox gauges appeared under Metrics |

Latency on the development laptop (Windows, Docker Desktop), final run:

| Scenario | p50 | p95 | Target p95 | Result |
| --- | ---: | ---: | ---: | --- |
| Manager approval queue | 10.3 ms | 15.0 ms | 150 ms | met |
| Employee: my expenses | 9.2 ms | 14.4 ms | 100 ms | met |
| Budget vs. actual, one month | 76.2 ms | 399.7 ms | 300 ms | **missed locally**: the durable audit commit alone had p95 252.5 ms on this disk |
| Outbox delivery (20 postings) | 1.19 s | 2.49 s | 3 s | met |

Before/after, measured back to back: `GET /api/me` went from p95 22.4 ms to 8.9 ms, and the queue from 38.6 ms to 26.2 ms. See [performance](performance/expense-queries.md#http-latency-targets-phase-6).

Found and fixed during this phase:

- **Four queries per request.** The per-request access lookup made four sequential queries; it now makes one.
- **Notification before the money effect (from E2E).** The "posted, reimbursement scheduled" notification could appear before the reimbursement committed. The reimbursement handler now runs first, and a failure stops the later handlers. ADR 0005 is amended.
- **Batch order.** A claimed batch was processed in GUID order; it is now oldest first.

Not verified:

- **The CI run itself** (the new `e2e` job, the enforced targets on `ubuntu-24.04`), until the change is pushed. Whether CI meets the report target is unknown.
- **Alerts:** described in [observability](operations/observability.md), but not configured anywhere.
- **Load or concurrency at scale:** the requests are sequential, from one client.

---

## 2026-11-14: Phase 5, reliability (outbox, worker, idempotency)

Scope: [ADR 0005](decisions/0005-messaging-outbox.md). Broker choice: a local SQL-backed queue behind a Service Bus-shaped port, chosen by the owner over Azure Service Bus or its emulator. Worked on `main`.

| Check | Result |
| --- | --- |
| .NET locked restore and Release build | Passed; 0 warnings, 0 errors. New project `FinOps.Worker`; no new NuGet packages |
| Unit tests | 130 passed (11 new: retry schedule, outbox, notification and reimbursement rules, both handlers, payload contents) |
| HTTP tests (`Category!=Database`) | 39 passed (new: outbox endpoints need `messaging.manage`; notifications need a provisioned user; OpenAPI paths) |
| Real SQL Server tests (`Category=Database`) | 64 passed (11 new in `MessagingIntegrationTests`). The messaging class then passed **3 more consecutive runs** |
| Mutation check | With the `ProcessedMessages` insert removed, 3 tests failed (redelivery, partial failure, lease expiry). Restored afterwards |
| EF model drift | None after migration `MessagingOutbox` |
| Worker host smoke test | Started against an unreachable SQL Server: options validated, `Outbox worker started` logged, each poll logged the failure and the process kept running |
| Angular | Typecheck clean; 33 tests passed (6 new); production build 243.2 kB initial |

What the database tests prove:

- **Outbox atomicity.** Approve and post each write exactly one event, carrying the request's correlation ID. A stale ETag (412), a self-approval (403) and the losing request of two concurrent approvals write none.
- **Delivery.** The worker creates the requester's notifications and exactly one reimbursement. The reimbursement is audited with no actor and the posting request's correlation ID, and the expense detail shows it.
- **No duplicate effects:**
  - after a lost completion (redelivery);
  - when one handler fails after another committed (only the failed handler re-runs, after the 5 s backoff);
  - when a lease expires mid-handler and a second worker finishes the message (the stale worker can't settle or duplicate);
  - with four concurrent workers over eight messages plus a duplicate publication (each message delivered once, each expense paid once).
- **Failure handling.**
  - Backoff is 5 s, then 10 s; dead-lettering happens at the third delivery with `MaxDeliveryCount=3`.
  - Unknown types, unreadable payloads and exhausted delivery counts are dead-lettered without retrying.
  - Replay is administrator-only, audited, keeps the message ID, and succeeds exactly once after the fault clears.
- **Privacy and backstops.** Notifications are private (another user's is 404), and marking read is idempotent. The database refuses a second reimbursement for one expense.

Not verified:

- The worker running against the local development database with the UI (no manual browser walkthrough).
- Throughput or latency at volume.
- Migration `Down`.
- Any Azure Service Bus implementation, which doesn't exist.

---

## 2026-11-14: Phase 4, reporting

Scope: [ADR 0004](decisions/0004-report-access.md). Worked on `main`.

| Check | Result |
| --- | --- |
| .NET locked restore and Release build | Passed; 0 warnings, 0 errors. New packages: QuestPDF 2026.9.2 and ClosedXML 0.105.1, with no NuGet vulnerability warnings |
| Unit tests | **119 passed**. Adds the report service: catalog by permission, scope, validation, the row limit, auditing |
| HTTP, model and renderer tests | **35 passed**. Adds real PDF generation (empty, small and 400-row reports), and Excel types, formats, totals formulas and formula-injection safety |
| Real SQL Server tests | **53 passed**. Adds 16 report tests; the plan test was re-measured |
| EF model vs migrations | No pending model changes (migration `ReportingProcedures`) |
| RDL generation | Deterministic: regenerating produces byte-identical files |
| Angular typecheck / tests / build | Passed / **27 passed** / 233.7 kB raw, 64.4 kB transferred |

**Total: 234 automated tests passing.**

What the new real-SQL tests establish:

- **Catalog by role:** an employee sees no reports; a manager and finance see 4; an administrator sees 5.
- **Expense summary is exact** on an isolated department: 3 expenses; posted 100, approved 50, pending 25, total 175. Draft and cancelled expenses are ignored.
- **Approval aging** shows the pending 25 in the "0-2 days" band.
- **The transaction ledger is by posting date:** both postings made today appear, including one whose expense date was last month.
- **Isolation inside the files:** for every financial report, the Finance manager's JSON preview **and the downloaded Excel workbook** contain only Demo Finance. The test reads every cell and finds no other department's name.
- **Exports:** PDF and Excel are returned with the correct content type, file name and `Cache-Control: no-store`, and are audited as `report.exported` with their format.
- **Audit activity:** refused for managers and finance, allowed for the administrator.
- **Errors:**
  - Reversed dates, a range over 366 days, month 13 and an unknown format return 400 with the field named.
  - An unknown report returns 404.
- **Procedure self-validation:** a reversed range → 50004, `MaxRows` 0 → 50006, invalid JSON → 50003.
- **RDL contract:** all 5 RDL files are parsed, and every dataset's procedure and query parameters exist in the migrated database.

**Found and fixed during verification:**

- **Reserved word:** `identity` is a reserved word in T-SQL, so the hand-written procedures now use `[identity].UserProfiles`.
- **Wider reporting index:** adding `Category` to it made the whole-year budget report's saving 2.97× (891 vs 2,643 reads). The plan test's thresholds now reflect what each query can achieve: ≥10× for one month (41 reads) and ≥2× for a whole year.

**Not verified:**

- **RDL files:** not rendered on a real SSRS server, and not validated against Microsoft's RDL schema (its published URL returns 404).
- **Browser:** no manual walkthrough of the Reports panel; component tests cover the hidden-when-empty state, the preview, totals and per-report parameters.
- **Linux PDF rendering:** the PDF renderer was exercised on Windows only. CI will be the first run on Linux, where QuestPDF uses its bundled native libraries.
- **Your local database:** the migrations haven't been applied to it. Run `dotnet ef database update`.

---

## 2026-11-14: Phase 3b, budgets, stored procedure and query evidence

Branch `phase-3b-budgets`.

| Check | Result |
| --- | --- |
| .NET locked restore and Release build | Passed; 0 warnings, 0 errors |
| Unit tests | **113 passed**. Adds budget rules and budget scope |
| HTTP and model tests | **29 passed** |
| Real SQL Server tests | **37 passed**. Adds 6 budget and report tests, and the query-plan test over 60,000 rows |
| EF model vs migrations | No pending model changes (migrations `CoveringExpenseIndexes`, `BudgetsAndReporting`) |
| Angular typecheck / tests / build | Passed / **23 passed** / 220.9 kB raw, 61.9 kB transferred |

**Total: 202 automated tests passing.**

What the new tests establish:

- **Stored procedure arithmetic is exact**, checked on a department isolated from other tests' data:
  - Budget 400; posted 100, approved 50, submitted 25 → Remaining 250, Utilization 37.5%.
  - Drafts, cancelled expenses and other months are excluded.
  - Unbudgeted spend is reported with a null budget.
- **Report scope is resolved on the server:**
  - The Finance department's manager sees only Finance rows.
  - The administrator sees all departments.
  - An employee gets 403.
  - A month or year out of range gets 400.
- **The procedure defends itself** when called directly: malformed department JSON (including an injection attempt) → error 50003; month 13 → 50002. No data is affected.
- **Budget management:**
  - Finance creates a budget (201 with `Location` and `ETag`).
  - A duplicate is 409, invalid input is 400, and an unknown department is 400.
  - A change without `If-Match` is 428, and a stale version is 412.
  - Both changes are audited.
  - Managers and administrators cannot change budgets.
- **Query plans** ([performance/expense-queries.md](performance/expense-queries.md)): list pages and the report seek their covering indexes. List pages read at least 10× fewer pages in step 1 than the Phase 3a queries (2,667 → 4, 4 and 62). The report reads at least 3× fewer than without its index (one month: 38 instead of 2,667).

**Not verified:**

- A manual browser walkthrough of the Budgets panel; component tests cover visibility by role, edit controls and the overspend styling.
- Applying the new migrations to your local database. Run `dotnet ef database update`, then `seed-demo-data`, which adds the demo budgets.

---

## 2026-11-14: Phase 3a, expense workflow

Scope: [design/phase3-expenses.md](design/phase3-expenses.md) (round 3a). Branch `phase-3-expenses`.

| Check | Result |
| --- | --- |
| .NET build (Release) | Passed; 0 warnings, 0 errors |
| Unit tests | **101 passed**. Includes all 48 action-by-state cells of the state machine, money and date rules, separation of duties, the access policy, ETag parsing and sort parsing |
| HTTP and model tests | **29 passed**. Adds expense 401, 428 before database work, 403 without the approve permission, 400 for invalid paging, sort and status, and the OpenAPI contract |
| Real SQL Server tests | **30 passed**, in one shared container. Adds 15 workflow tests and 6 validation cases |
| EF model vs migrations | No pending model changes (migration `ExpenseWorkflow`) |
| Angular typecheck / tests / build | Passed / **17 passed** / 206.8 kB raw, 59.6 kB transferred |
| `npm audit --omit=dev` | 0 vulnerabilities |

**Total: 177 automated tests passing.**

What the real-SQL tests establish:

- **Full lifecycle:** employee → engineering manager → finance officer, with four audit rows in order.
- **Separation of duties:** a manager cannot approve their own expense, and finance cannot post their own. Both return 403 and are audited as `Denied`.
- **Visibility:**
  - A manager of another department gets **404**, not 403.
  - An employee cannot read a colleague's expense, even in the same department.
  - A random ID behaves exactly like an invisible one.
- **Permissions:** the employee and the administrator cannot approve; the administrator cannot post.
- **Invalid transitions:** 409.
- **Versions:** a write without `If-Match` returns 428; a stale `If-Match` returns 412.
- **Concurrency, twice over:**
  1. Two parallel HTTP approvals with the same ETag: exactly one 200, one 412, one decision row.
  2. A forced race where both writers pass the in-memory check: SQL Server's rowversion guard rejects the second, and its transaction (decision plus audit) rolls back completely.
- **Rejection:** requires a comment; revise and resubmit keep the decision history.
- **Validation:** 400 with the field named, for amount ≤ 0, more than 2 decimals, non-USD, an unknown or numeric category, and a future date.
- **Creation:** creating in a department you are not a member of returns 403.
- **Lists:** visibility filtering, a department filter that never widens access, sort order, and paging limits.
- **Audit:** the endpoint is restricted to `audit.read`, records carry correlation IDs, and no free text is stored.

**Not verified:**

- A manual browser walkthrough of the Expenses panel. The component tests cover role-based tabs, server-driven action buttons and the approve call.
- The migration against your existing local database. Run `dotnet ef database update`, then `seed-demo-data`.

---

## 2026-11-14: Hardening, demo-data separation and test expansion

Scope: [ADR 0003](decisions/0003-hardening-and-demo-data.md).

| Check | Result |
| --- | --- |
| .NET locked restore | Passed |
| .NET build (Debug and Release) | Passed; 0 warnings, 0 errors |
| Unit tests | **18 passed** (Department, UserProfile, DepartmentMembership, UserRole) |
| HTTP and model tests | **21 passed** (health/contract 4, authorization 11, hardening 4, permission catalog 2) |
| Real SQL Server tests (Testcontainers, `2022-CU28-ubuntu-22.04`) | **9 passed** |
| EF model vs migrations | No pending model changes |
| Angular typecheck | Passed |
| Angular tests | **10 passed** (3 files) |
| Angular production build | Passed; initial bundle 157.6 kB raw / 46.2 kB transferred |
| `npm audit --omit=dev` (shipped code) | **0 vulnerabilities** |
| `npm audit` (including build tooling) | 16 moderate (see below) |

**Total: 58 automated tests passing**, with none skipped.

What the new tests establish:

- **Authorization matrix:**
  - 401 without credentials.
  - 403 for an unprovisioned user.
  - 403 for a provisioned user without the permission.
  - 403 for a permitted user whose token lacks the API scope.
  - Membership filtering for an employee; global read for an administrator.
  - Startup fails when mock auth is enabled outside Development, or when tenant or audience are missing.
- **Against real SQL Server:**
  - Migrations alone create no user accounts.
  - The demo seed is idempotent.
  - An expired membership grants nothing.
  - An inactive user is refused despite an active membership.
  - Global read sees all departments.
  - The unique constraint is enforced.
- **Efficiency:** one access lookup per request (previously two).
- **Hardening:** correlation ID echo and sanitization, security headers and `no-store`, 429 over the rate limit, and health probes exempt from the limit.

**Remaining npm advisories.** All 16 are in Angular CLI build tooling (`pacote`, `minimatch`, `glob`, `sigstore`, `tuf-js` chains), not in the browser bundle. The only fix is Angular CLI 22, a major upgrade, which is deferred to a deliberate framework upgrade. The non-breaking `npm audit fix` reduced the count from 18 to 16. CI gates on `npm audit --omit=dev`.

**Not verified:**

- A real Entra sign-in. No tenant was configured; the token-like test scheme covers the scope logic, not Entra itself.
- A manual browser walkthrough of the persona picker.
- The CI workflow on GitHub. Its steps were run locally, but not on a GitHub runner.

Environment: .NET SDK 10.0.403, Node 22.23.4, npm 11.21.2, Docker Desktop 27.3.1 (Linux engine), Windows 11.

---

## 2026-11-06: Phase 2, identity and authorization

| Check | Result |
| --- | --- |
| .NET build | Passed; 0 warnings, 0 errors |
| Non-database .NET tests | 14 passed (7 unit, 7 HTTP) |
| EF model vs migrations | No pending model changes |
| Angular typecheck, tests, build | Passed; 4 tests |
| Real SQL Server tests | **Not run**: Docker Desktop was not running |
| `npm audit` | 16 moderate advisories reported; no breaking upgrades applied |

---

## 2026-10-29: Phase 1, foundation

| Check | Result |
| --- | --- |
| .NET locked restore, build | Passed; 0 warnings |
| Unit tests | 7 passed |
| HTTP tests | 6 passed |
| Real SQL Server test | 1 passed (migration applied twice, seed read through the API, unique constraint) |
| Angular build and tests | Passed; 4 tests; bundle about 151 kB |
| `npm audit` | 0 vulnerabilities at the time |
| EF idempotent script generation | Passed |
| Docker Compose config | Passed using `.env.example` |

Verification used portable .NET SDK 10.0.403, Node 22.23.4 and npm 11.21.1 from an isolated copy on `C:`. Fixes made during that run:

- A repository-scoped `NuGet.Config`.
- Patched Testcontainers and PKCS dependencies.
- npm 11 required, after npm 10's resolver crashed.
- Database options validated at startup.

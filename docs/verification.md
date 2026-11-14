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

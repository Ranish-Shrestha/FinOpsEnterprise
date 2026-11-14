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

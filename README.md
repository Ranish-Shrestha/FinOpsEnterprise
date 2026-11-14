# FinOps Enterprise

[![CI](https://github.com/Ranish-Shrestha/FinOpsEnterprise/actions/workflows/ci.yml/badge.svg)](https://github.com/Ranish-Shrestha/FinOpsEnterprise/actions/workflows/ci.yml)

A portfolio-quality internal financial operations platform: ASP.NET Core 10, Angular 21, SQL Server and Microsoft Entra ID. It is built in vertical phases, and each phase runs end to end and is tested. **Only synthetic data belongs in this project.**

## Status

| Phase | Scope | State |
| --- | --- | --- |
| 1. Foundation | Solution layout, SQL Server, EF migrations, health, OpenAPI, Angular shell | Done |
| 2. Identity and authorization | Entra OIDC/PKCE, JWT validation, app-owned roles, permissions and dated memberships, mock personas | Done; real Entra sign-in not yet verified with a tenant |
| 2.5 Hardening | Demo data out of migrations, per-request access cache, rate limiting, security headers, CI | Done |
| 3. Expenses and approvals | State machine, concurrency, audit | [Design proposed](docs/design/phase3-expenses.md) |
| 4–7 | SSRS, messaging, DevOps, Azure | Not started |

Full plan: [FINOPS_ENTERPRISE_CODEX_SPEC.md](FINOPS_ENTERPRISE_CODEX_SPEC.md). What has actually been run: [docs/verification.md](docs/verification.md).

## What this demonstrates so far

- **Authentication separate from authorization.** A valid Entra token identifies the caller. Roles, permissions and department memberships come only from SQL Server, and token role claims are never trusted.
- **Server-side resource filtering.** Department lists are filtered by active, dated memberships before projection. Expired memberships and deactivated users grant nothing.
- **Tests at the right level.**
  - Domain unit tests.
  - HTTP tests for the 401/403/429 matrix.
  - Real SQL Server tests through Testcontainers. EF InMemory is never used.
  - Angular component, service and interceptor tests.
- **Operational basics.** Liveness and readiness, Problem Details, correlation IDs in structured logs, security headers, rate limiting, and migrations that are never applied automatically.

## Repository layout

```text
src/
  FinOps.Api/              Composition root: endpoints, auth, middleware, health
  FinOps.Application/      Contracts (readers), DTOs, permission catalog
  FinOps.Domain/           Entities and invariants (no framework dependencies)
  FinOps.Infrastructure/   EF Core mappings, migrations, readers, demo seeder
  finops-web/              Angular standalone SPA
tests/
  FinOps.UnitTests/        Domain invariants
  FinOps.IntegrationTests/ HTTP tests (doubles) + real SQL Server tests (Category=Database)
docs/
  architecture/overview.md        Components, request lifecycle, data model
  security/identity-authorization.md  Boundaries, permissions, personas, Entra setup
  design/phase3-expenses.md       Proposed expense workflow rules
  decisions/                      ADRs 0001–0003
  requirements-review.md          Spec adjustments and open questions
  verification.md                 Dated log of what was run
.github/workflows/ci.yml          Build, tests, real-SQL tests, EF drift, Angular, audit
docker-compose.yml                Local SQL Server only
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0); `global.json` accepts 10.0.100 or later feature bands.
- [Node.js 22](https://nodejs.org/en/download) (22.12 or newer) with npm 11: `npm install --global npm@11.21.1`.
- Docker Desktop running the **Linux** engine, with at least 2 GB of memory for SQL Server.
- Free ports: 14333 (SQL), 5143 and 7143 (API), 4200 (UI).

Commands below are PowerShell. Run them **one line at a time**, from the repository root unless stated otherwise. On Windows, open the folder with an uppercase drive letter (`F:\...`, not `f:\...`; see Troubleshooting). Mock mode needs no Entra tenant or Azure subscription.

## Quick start (mock identities)

You will end up with **two terminals that stay open**, one for the API and one for the UI, while SQL Server runs in Docker in the background. Each long-running command stops when its terminal closes.

| Piece | Runs in | Ready when you see |
| --- | --- | --- |
| SQL Server | Docker (background) | `Container finops-enterprise-sqlserver-1 Healthy` |
| API | Terminal 1 (stays open) | `Now listening on: https://localhost:7143` |
| UI | Terminal 2 (stays open) | `Local: http://127.0.0.1:4200/` |

### 1. Start SQL Server (first time)

Start Docker Desktop, then copy the environment file:

```powershell
Copy-Item .env.example .env
```

Edit `.env` and replace the placeholder with your own local password. SQL Server rejects weak passwords and the container exits with code 255. The password needs at least 8 characters, with three of these four: uppercase, lowercase, digits, symbols. Avoid `$`, `;` and quotes.

```powershell
docker compose up -d --wait sqlserver
```

`.env` configures Compose only and is git-ignored. The password is fixed when the data volume is first created; changing `.env` later does not change it. To start over, run `docker compose down --volumes`, which **deletes the local database**, then run `up` again.

### 2. Configure, migrate and seed (first time)

Use the **same password** as in `.env`, and `127.0.0.1` rather than `localhost`. Compose publishes SQL Server on IPv4 only, and `localhost` may resolve to IPv6 first and time out.

```powershell
dotnet dev-certs https --trust
dotnet tool restore
dotnet user-secrets set "ConnectionStrings:FinOps" "Server=127.0.0.1,14333;Database=FinOpsEnterprise;User Id=sa;Password=<YOUR_LOCAL_SQL_PASSWORD>;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5" --project src/FinOps.Api
```

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet ef database update --project src/FinOps.Infrastructure --startup-project src/FinOps.Api
dotnet run --project src/FinOps.Api -- seed-demo-data
```

Run `database update` **before** `seed-demo-data`: the first creates the tables, the second fills in demo users. A fresh database prints `Demo data seeded: 12 row(s) inserted.`

- **Configuration.** The API reads the connection string from user secrets or `ConnectionStrings__FinOps`, never from `.env`. `TrustServerCertificate` and the `sa` login are for the local container only.
- **Migrations** create schemas, tables and **reference data** (departments, roles, permissions). They never create user accounts and never run on API startup.
- **`seed-demo-data`** inserts the four synthetic personas below. It runs only in Development, is safe to repeat, and only inserts missing rows.
- **Upgrading an existing Phase 2 database:** `database update` removes the old migration-seeded demo users. Run `seed-demo-data` again afterwards.

### 3. Start the API (terminal 1, leave it open)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/FinOps.Api --launch-profile https
```

Wait for `Now listening on: https://localhost:7143`. As a check, `https://localhost:7143/health` should return `{"status":"Healthy"}`.

### 4. Start the UI (terminal 2, leave it open)

```powershell
Set-Location src/finops-web
npm ci        # first time, or after package-lock.json changes
npm start
```

Wait for `Local: http://127.0.0.1:4200/`, then open **http://127.0.0.1:4200**. The dev server proxies `/api` and `/health` to `https://localhost:7143`. Its `secure: false` setting exists only for the local development certificate.

### 5. Try the authorization rules

In mock mode, use the **Development persona** picker:

| Persona | Expected result |
| --- | --- |
| Demo Employee | Sees **Demo Engineering** only |
| Demo Manager | Sees **Demo Finance** only. Their Operations membership has expired |
| Demo Administrator | Sees all three departments (`departments.read.all`) |
| Demo Former Employee | Inactive user: identity shows no access and departments fail to load (403) |

Then stop the database with `docker compose stop sqlserver` and refresh. The API stays live, readiness reports the database is unavailable, and departments report failure. No fake data is substituted. Start it again with `docker compose up -d --wait sqlserver`.

### Starting again later

Steps 1 and 2 are one-time setup. On later days:

1. Start Docker Desktop, then run `docker compose up -d --wait sqlserver`.
2. In terminal 1, start the API (step 3).
3. In terminal 2, start the UI (step 4, without `npm ci`).

After pulling changes that add migrations, run `dotnet ef database update` (as in step 2) before starting the API.

## Real Entra sign-in

1. Register the API and SPA apps.
2. Set the API's token version to v2.
3. Configure the tenant and audience through user secrets.
4. Set `mockMode: false` in `auth.config.ts`.
5. Provision your user by Entra object ID.

Step-by-step: [docs/security/identity-authorization.md](docs/security/identity-authorization.md#entra-registration-real-sso).

## Endpoints

| Endpoint | Auth | Purpose |
| --- | --- | --- |
| `GET /health` | Anonymous | Liveness. 200 while the process runs; never touches SQL |
| `GET /health/ready` | Anonymous | Readiness. 200 only when the migrated schema is queryable, otherwise 503 |
| `GET /openapi/v1.json` | Anonymous, **Development only** | OpenAPI document |
| `GET /api/me` | Scope + provisioned active user | Profile, roles, permissions and active department IDs |
| `GET /api/departments` | Scope + `departments.read` | Departments filtered by membership, or all with `departments.read.all` |

Status codes:

- **401:** missing or invalid credentials.
- **403:** valid caller without the scope, provisioning or permission.
- **429:** rate limit exceeded (default 120 requests per 60 s per caller on `/api/*`; set `RateLimiting:PermitLimit` and `RateLimiting:WindowSeconds` to change it).
- **500:** Problem Details with a `traceId`, never exception text.

Every response carries `X-Correlation-ID`. A safe inbound value is echoed and added to the log scope.

## Build and test

```powershell
dotnet build FinOpsEnterprise.slnx
dotnet test FinOpsEnterprise.slnx --no-build --filter "Category!=Database"
dotnet test tests/FinOps.IntegrationTests --no-build --filter "Category=Database"   # needs Docker

Push-Location src/finops-web; npm run typecheck; npm test; npm run build; Pop-Location
```

- **Database tests:** each test class gets a disposable SQL Server container. It migrates, then seeds, exactly as in the quick start. They fail rather than skip when Docker is unavailable. The first run pulls about 1.5 GB.
- **HTTP tests** use explicit doubles for persistence. They test routing, authorization wiring and error contracts, not SQL behavior.
- **CI** runs all of the above, plus the EF drift check and a production dependency audit, on every push and pull request.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| Browser shows `ERR_CONNECTION_REFUSED` at `127.0.0.1:4200` | The UI isn't running. Its terminal was closed, or `npm start` exited with an error. Start it as in step 4 and keep that terminal open |
| UI loads, but API shows *Unavailable* | The API isn't running. Start it as in step 3 and keep that terminal open |
| UI shows database *Needs setup* | SQL Server is stopped (`docker compose up -d --wait sqlserver`), or the migrations were never applied (`dotnet ef database update ...`) |
| `container finops-enterprise-sqlserver-1 exited (255)` | Usually the `.env` password is still the placeholder, or too weak. Check with `docker compose logs sqlserver`. Fix `.env`, then `docker compose down --volumes` and `up` again |
| `Login failed for user 'sa'` | The user-secret password differs from the one the volume was created with. Make them match, or recreate the volume |
| PowerShell `Unexpected token '$env:...'` | Two commands were pasted onto one line. Run each command on its own line |
| Every Angular spec fails with "Vitest failed to find the runner" | Windows shell reported a lowercase drive (`f:\`). Run from a terminal at `F:\...`, such as a plain PowerShell window |
| Database tests time out creating a container | Docker was still starting or pulling the image. Run `docker pull mcr.microsoft.com/mssql/server:2022-CU28-ubuntu-22.04` once, then retry |
| `SqlException ... error: 0 - The wait operation timed out` | `localhost` resolved to IPv6 `::1`, but Compose publishes SQL Server on IPv4 only. Use `Server=127.0.0.1,14333` in the connection string |
| Every persona gets 403 | Demo data not seeded. Run `dotnet run --project src/FinOps.Api -- seed-demo-data` |
| 401 with a real Entra token | The token is v1, or the audience doesn't match. See the Entra setup notes on `requestedAccessTokenVersion` and `Security:Audience` |
| API exits at startup about `ConnectionStrings:FinOps` | User secret not set, or `ASPNETCORE_ENVIRONMENT` is not `Development` |

## Stopping

Press Ctrl+C in the API and UI terminals, then run `docker compose stop`. `docker compose down` keeps the data volume. **Add `--volumes` only if you mean to delete the local database.**

## Design records

- [Architecture overview](docs/architecture/overview.md)
- [ADR 0001: Foundation](docs/decisions/0001-foundation.md)
- [ADR 0002: Entra authentication and application authorization](docs/decisions/0002-identity-authorization.md)
- [ADR 0003: Demo data out of migrations; HTTP hardening](docs/decisions/0003-hardening-and-demo-data.md)
- [Requirements review and open questions](docs/requirements-review.md)

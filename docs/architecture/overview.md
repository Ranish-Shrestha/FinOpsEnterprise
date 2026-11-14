# Architecture overview

FinOps Enterprise is a **modular monolith** with Clean Architecture layering: one deployable API, one SPA and one SQL Server database. See [ADR 0001](../decisions/0001-foundation.md) for why it is not split into services.

## Components

```mermaid
flowchart LR
  Browser["Angular SPA<br/>(finops-web)"] -- "/api, /health<br/>dev proxy" --> Api
  Browser -. "OIDC + PKCE<br/>(Entra mode)" .-> Entra[(Microsoft Entra ID)]
  Api -. "JWKS / metadata" .-> Entra
  subgraph Api["FinOps.Api (composition root)"]
    direction TB
    Pipeline["Correlation ID → security headers → exception handler<br/>→ authentication → authorization → rate limiter"]
    Endpoints["Minimal API endpoints"]
    Pipeline --> Endpoints
  end
  Endpoints --> App["FinOps.Application<br/>contracts + DTOs"]
  Infra["FinOps.Infrastructure<br/>EF Core, readers, health"] -- implements --> App
  App --> Domain["FinOps.Domain<br/>entities + invariants"]
  Infra --> Sql[("SQL Server<br/>organization.*, identity.*")]
```

Dependencies point inward only. Domain references nothing, Application references Domain, Infrastructure implements Application contracts, and the API wires everything together.

## Request lifecycle: `GET /api/departments`

```mermaid
sequenceDiagram
  participant B as Browser
  participant A as API pipeline
  participant P as PermissionAuthorizationHandler
  participant C as CurrentUserAccess (scoped)
  participant D as SQL Server
  B->>A: GET /api/departments (Bearer token or dev header)
  A->>A: Validate token: signature, issuer, audience, lifetime (else 401)
  A->>A: Require scope FinOps.Access (else 403)
  A->>P: Policy "departments.read"
  P->>C: GetAsync()
  C->>D: User by oid + roles + permissions + active memberships
  D-->>C: CurrentUserDto (or null → 403)
  P-->>A: Succeed if permission held (else 403)
  A->>A: Rate limit per oid (else 429)
  A->>C: GetAsync() (cached, no second query)
  A->>D: Departments filtered by membership IDs, unless read.all
  D-->>B: 200 DepartmentDto[] with X-Correlation-ID
```

## Data model (current)

| Schema | Table | Kind | Notes |
| --- | --- | --- | --- |
| `organization` | `Departments` | Reference/demo data | Unique name. Three synthetic rows seeded by migration |
| `identity` | `Roles`, `Permissions`, `RolePermissions` | Reference data | Seeded by migration. Must match `ApplicationPermissions` |
| `identity` | `UserProfiles` | Operational | Unique `EntraObjectId` and `Email`. Never seeded by migration |
| `identity` | `UserRoles` | Operational | |
| `identity` | `DepartmentMemberships` | Operational | Half-open date range. Check constraint `EndsAtUtc > StartsAtUtc` |
| `finance` | `ExpenseRequests` | Operational | `decimal(19,4)`, `rowversion`, status, amount and currency check constraints, approval-queue and requester indexes. No cascade deletes |
| `finance` | `ApprovalDecisions` | Operational, append-only | One row per approve or reject. A rejection must carry a comment (check constraint) |
| `finance` | `Budgets` | Operational | One per department and month (unique), `rowversion`. Actuals are computed by `finance.usp_BudgetVsActual`, never stored |
| `audit` | `AuditEvents` | Append-only | Successes and denials, with correlation ID and allow-listed JSON metadata. No foreign keys, so history outlives the rows it describes |

## Operational surface

| Concern | Current implementation |
| --- | --- |
| Liveness / readiness | `/health` (process) and `/health/ready` (queries the mapped schema) |
| Errors | RFC 9457 Problem Details with `traceId`; no exception text |
| Correlation | `X-Correlation-ID`: a safe inbound value is echoed, otherwise the W3C trace ID. Added to every log scope |
| Logging | JSON console with scopes |
| Hardening | HSTS (outside Development), `nosniff`, `X-Frame-Options: DENY`, restrictive CSP, `no-store` on `/api` |
| Rate limiting | Fixed window per caller on `/api/*`, configurable via `RateLimiting:PermitLimit` and `RateLimiting:WindowSeconds` |
| CI | `.github/workflows/ci.yml`: locked restore, build, unit and HTTP tests, Testcontainers SQL tests, EF drift check, Angular checks, production dependency audit |

## Planned additions

These are not built yet. They are added in their own phases (see [the spec](../../FINOPS_ENTERPRISE_CODEX_SPEC.md)):

- SSRS reporting.
- Service Bus worker.
- Azure deployment.

# FinOps Enterprise — Project Specification for Codex

> **Purpose:** Build a portfolio-quality enterprise financial operations and reporting platform demonstrating senior-level C#/.NET, Angular, security, SQL Server, reporting, Azure, architecture, testing, and DevOps skills.
>
> **How to use:** Open this file in VS Code and ask Codex (or another coding agent) to read it before making code changes. Work one milestone at a time. Do **not** attempt to implement the entire specification in one pass.
>
> **Current status (2026-11-14):** Phases 1–2 are complete, with hardening and CI added early. Phase 3 is complete: the expense workflow, budgets, the budget-vs-actual stored procedure, and execution-plan evidence. Phase 4 (reporting) is next; it needs a decision on report access first. See §10 and [docs/verification.md](docs/verification.md).

## 1. Product overview

**Product name:** FinOps Enterprise

An internal multi-department financial operations platform where:
- Employees submit expenses and reimbursement requests.
- Managers approve or reject expenses from their own departments, subject to policy.
- Finance staff manage budgets, view transactions, and generate reports.
- Administrators manage users, departments, roles, and permissions.
- Authorized executives and finance staff access dashboards and parameterized reports.
- Sensitive actions are audited.

Use **synthetic sample data only**. This is a learning/portfolio app, not a production financial system.

### Primary goals
1. Demonstrate OAuth 2.0, OpenID Connect (OIDC), SSO, authentication, authorization, role-based and policy/resource-based access control.
2. Demonstrate C#, ASP.NET Core, EF Core, LINQ, SQL Server, complex SQL, and SSRS/RDL paginated reporting.
3. Demonstrate Angular/TypeScript, REST APIs, validation, testing, observability, CI/CD, and Azure deployment.
4. Demonstrate realistic engineering tradeoffs, security controls, and maintainability.

## 2. Technical stack

| Component | Choice |
| --- | --- |
| Backend | **ASP.NET Core 10 Web API** (C#) |
| Frontend | **Angular + TypeScript** |
| Database | **SQL Server**, EF Core with migrations |
| Identity / SSO | **Microsoft Entra ID** with OIDC; OAuth 2.0 authorization code + PKCE for the SPA |
| API security | JWT bearer token validation, scopes, policy and resource-based authorization |
| Authorization data | Application-owned roles, permissions, department membership |
| Reporting | **SSRS 2022 Developer** for local RDL work, with an architecture that can accommodate Power BI Report Server. Note: SSRS runs on **Windows only** (not in the Linux SQL container) and has **no native Entra/OIDC sign-in**; Phase 4 must start with an access design (see `docs/requirements-review.md`) |
| SQL / reporting | LINQ, views, stored procedures, parameters, indexes, execution plans |
| Logging/telemetry | Serilog, OpenTelemetry; Azure Application Insights as deployment option |
| Asynchronous processing | Azure Service Bus + .NET background worker (later phase) |
| Caching | Redis (later phase, only if justified) |
| Testing | xUnit, NSubstitute or Moq, ASP.NET Core integration tests, Testcontainers for SQL Server, **Playwright** for end-to-end tests |
| CI/CD | GitHub Actions |
| Packaging | Docker / Docker Compose for local dependencies |
| Deployment | Azure App Service, Azure SQL, Key Vault (later phase, budget-aware) |

**Versions:** Use stable, supported versions available in the environment when implementing; check package compatibility rather than assuming a particular Angular minor version. Prefer .NET 10 LTS.

**Budget constraint:** Prioritize local development and a reproducible demo before provisioning paid Azure resources. Flag any cloud cost or account prerequisites before creating resources.

## 3. Architecture

Start as a **modular monolith with Clean Architecture**. Do not prematurely create microservices.

```text
FinOpsEnterprise/
├── src/
│   ├── FinOps.Api/                  # HTTP endpoints, authentication, authorization, middleware
│   ├── FinOps.Application/          # Use cases, interfaces, validation, DTOs
│   ├── FinOps.Domain/               # Entities, value objects, business rules
│   ├── FinOps.Infrastructure/       # EF Core, persistence, integrations, identity adapters
│   ├── FinOps.Worker/               # Background processing (later)
│   └── finops-web/                  # Angular frontend
├── tests/
│   ├── FinOps.UnitTests/
│   ├── FinOps.IntegrationTests/
│   └── FinOps.E2ETests/
├── reports/
│   └── SSRS/                        # RDL files, datasets, report deployment notes
├── docs/
│   ├── architecture/
│   ├── security/
│   └── decisions/                   # Architecture decision records
├── .github/workflows/
├── docker-compose.yml
└── README.md
```

**Dependency rule:** Domain has no dependency on application, infrastructure, or API; application depends on domain; infrastructure implements application interfaces; API composes the application.

Use controllers or minimal APIs consistently, based on an explicit architecture decision. Use dependency injection, request/response DTOs, centralized exception handling (Problem Details), structured logging, API versioning if warranted, and OpenAPI documentation.

## 4. Business modules and proposed data model

### Identity and organization
- `UserProfile`: internal user ID, Entra object ID, display name, email, status.
- `Department`: ID, name, parent department (optional).
- `DepartmentMembership`: user, department, membership dates.
- `Role`, `Permission`, `UserRole`, `RolePermission`: application permissions, not merely UI roles.
- `AuditEvent`: actor, action, resource, timestamp, outcome, correlation ID, selected safe metadata.

### Finance
- `ExpenseRequest`: ID, requester, department, amount, currency, category, description, status, dates, row version.
- `ExpenseAttachment`: metadata/reference to storage (never put large file payloads in SQL Server).
- `ApprovalDecision`: request, approver, action, comments, timestamp.
- `Budget`: department, period, amount, currency.
- `TransactionRecord`: posted financial transaction reference, type, amount, timestamp.
- `OutboxMessage`: event payload and delivery status (later phase).

Use decimals for monetary amounts (not floats), explicit currency codes, UTC timestamps, appropriate constraints, concurrency tokens, and carefully designed delete behavior. Never allow client-submitted role/department claims to override server-side authority.

### Key workflows
1. Employee creates a draft expense and submits it.
2. Manager sees pending expenses only for authorized departments.
3. Manager approves/rejects according to state transition rules; prevent self-approval where required.
4. Finance reviews approved requests, posts transactions, and runs reports.
5. Admin manages department memberships and application permissions.
6. Audit events record business-critical operations without logging tokens or sensitive financial details unnecessarily.

## 5. Identity and authorization requirements

Implement these as distinct concepts:

- **Authentication / SSO:** Angular signs users in with Microsoft Entra ID using OpenID Connect.
- **OAuth 2.0:** SPA uses authorization code flow with PKCE to obtain an API access token for a configured scope.
- **API authorization:** Backend validates JWT signature, issuer, audience, lifetime, and scopes; avoid trusting tokens merely because they decode successfully.
- **RBAC:** Roles such as Employee, Manager, FinanceOfficer, Administrator, and Executive map to fine-grained permissions.
- **Policy-based authorization:** Protect API actions by required permissions.
- **Resource-based authorization:** Enforce department, ownership, status, and business rules *on the server*.

Example access policy:

| Action | Employee | Manager | Finance | Admin |
| --- | --- | --- | --- | --- |
| Submit own expense | Yes | Yes | Yes | Configurable |
| Read own expense | Yes | Yes | Yes | Yes |
| Approve department expense | No | Authorized department only | No | Configurable |
| Access aggregate financial reports | No | Limited department reports | Yes | Yes |
| Change user roles | No | No | No | Yes |
| Read audit trail | No | No | Limited | Yes |

**Security controls:** deny by default; return 401 for missing/invalid authentication and 403 for valid users without permissions; do not rely on Angular route guards for security; use least privilege; store secrets outside source control; use HTTPS; validate input; prevent IDOR; consider file-upload validation and rate limiting. Never log access tokens, ID tokens, refresh tokens, or client secrets.

**Local setup:** Identity provider registration and permissions may require an Azure/Entra tenant. Document tenant/app registration, redirect URIs, API scope, and development settings. Provide a clearly labeled **development-only mock authentication mode** if needed, never enabled in a deployed environment.

## 6. API design (initial proposal)

```text
GET    /api/me
GET    /api/departments
GET    /api/expenses?status=&departmentId=&page=&pageSize=
POST   /api/expenses
GET    /api/expenses/{id}
PUT    /api/expenses/{id}
POST   /api/expenses/{id}/submit
POST   /api/expenses/{id}/approve
POST   /api/expenses/{id}/reject
GET    /api/budgets
POST   /api/budgets
GET    /api/reports/catalog
GET    /api/audit-events
```

- Define DTOs and validation for all write endpoints.
- Standardize pagination, filters, sorting, error response, and correlation IDs.
- Enforce authorization *before returning resources*, including list filtering.
- Support optimistic concurrency on workflow updates.
- Generate OpenAPI documentation and examples.

## 7. LINQ, SQL Server, and SSRS learning requirements

### LINQ and EF Core
- Filtering, sorting, pagination, joins, grouping, aggregation, projection.
- `AsNoTracking()` for read-only queries where appropriate.
- Avoid N+1 queries, unbounded result sets, and accidental client evaluation.
- Demonstrate transactions and concurrency conflicts.
- Compare generated SQL and measured performance before/after indexing.

### SQL Server
- Normalized schema with constraints and foreign keys.
- Views and stored procedures with **parameterized** inputs.
- Indexes, execution plans, query optimization, transactions, isolation levels.
- Seeded synthetic data representing multiple departments.

### SSRS / RDL reports
1. Department expense summary.
2. Monthly budget vs. actual variance.
3. Pending approval aging.
4. Transaction ledger.
5. Audit activity (restricted).

Reports must have parameters, totals/grouping, pagination when needed, and Excel/PDF export. Guard report access at both the application layer and the reporting server/data source level; do not assume filtering UI parameters is enough to prevent cross-department data leaks. Document how to install/configure a local report server; do not require it to boot the main API.

## 8. Advanced engineering (after working MVP)

- **Messaging:** Azure Service Bus for business events such as `ExpenseApproved`; a worker consumes safely.
- **Reliability:** retry with backoff, idempotent handling, dead-letter handling, and optional outbox pattern.
- **Observability:** structured logs, correlation IDs, distributed traces, health checks, useful metrics.
- **Caching:** add Redis only when there is a measured use case and invalidation strategy.
- **Cloud:** Azure deployment, managed identity, Key Vault, environment separation, and safe CI/CD approvals.
- **Optional:** OpenTelemetry exporters, infrastructure as code with Bicep or Terraform, threat modeling, accessibility and responsive UI, feature flags.

## 9. Test and quality requirements

- Unit tests for domain rules and expense state transitions.
- Integration tests for API endpoints, authentication/authorization, EF Core, and SQL Server behavior.
- A test matrix for unauthenticated (401), unauthorized (403), wrong-department access, self-approval, tampered identifiers, invalid transitions, and concurrent approvals.
- Use real SQL Server via Testcontainers for DB-dependent integration tests; do not pretend EF Core InMemory matches SQL Server behavior.
- Angular unit tests for important components and end-to-end tests (Playwright, using Development mock personas) for core workflows.
- Lint/format, build, test, dependency scanning in CI **from the first phase that has tests**, not deferred to Phase 6.
- A README with a reproducible setup and a working demo of a full expense approval flow.

## 10. Milestones and acceptance criteria

Status key: ✅ done · 🟡 partially verified · ⬜ not started.

### Phase 1 — Foundation (Week 1) ✅
- Create solution/project skeleton, Angular shell, database setup, EF Core migration, local Docker dependencies, OpenAPI, health endpoint.
- **Done when:** A developer can clone, configure and run API, UI, and SQL Server locally using documented steps.

### Phase 2 — Identity + authorization (Weeks 2–3) 🟡
- Add Entra ID integration, OIDC/OAuth2 flow, JWT validation, role/permission policy, and ownership/department checks.
- **Done when:** Valid users can sign in; permission and cross-department access tests pass.
- *Status:* access tests pass against real SQL Server; a real Entra sign-in still needs to be verified with a tenant. Hardening and CI were pulled forward from Phase 6 (ADR 0003).

### Phase 3 — Finance workflows + SQL (Weeks 4–5) ✅
- Expense CRUD, submission, approvals, budget tracking, migrations, LINQ, stored procedures, indexes.
- Audit events for every state change ship **in this phase**, in the same transaction as the change.
- *Status:* done.
  - **Round 3a:** the expense lifecycle with separation of duties, ETag concurrency proven under parallel approvals, audit, paged API and UI.
  - **Round 3b:** monthly budgets, the parameterized stored procedure `finance.usp_BudgetVsActual`, and covering indexes. Measured list pages went from 2,667 logical reads to about 110–167; the plan test enforces this in CI.
- **Done when:** A complete expense lifecycle works correctly under concurrent requests and access restrictions.

### Phase 4 — SSRS (Week 6) ⬜
- First, record an ADR for the report access boundary (SSRS has no Entra sign-in; see requirements review).
- Build five RDL reports and provide a documented way to access them securely.
- **Done when:** Authorized test users can run parameterized reports and export PDF/Excel; access boundaries are verified.

### Phase 5 — Reliability (Weeks 7–8) ⬜
- Service Bus, worker, audit trail, retries, and idempotency.
- **Done when:** Event processing survives retries without duplicate business effects.

### Phase 6 — Quality + DevOps (Weeks 9–10) ⬜
- E2E tests, telemetry (OpenTelemetry), performance work against **measured targets** (define e.g. p95 latency for the expense queue at a stated row count first). Basic CI already exists.
- **Done when:** Pull-request CI builds and tests including E2E; key failures are observable; performance targets are met with before/after evidence.

### Phase 7 — Azure + portfolio (Weeks 11–12) ⬜
- Optional budget-aware Azure deployment, diagrams, demo data, README, screenshots, recorded walkthrough outline.
- **Done when:** A reviewer can understand architecture, clone/run locally, and follow a working demonstration.

The weeks are **estimates**, not strict deadlines. Work vertically and maintain a runnable project at the end of each phase.

## 11. Implementation instructions for Codex

When acting on this repository:

1. **Read this specification and inspect the repository before changing code.** Identify what already exists and what is missing.
2. **Only implement the requested phase/task.** Do not scaffold unnecessary advanced services ahead of time.
3. **Propose a small plan first:** files to add/change, commands to run, assumptions, and acceptance tests.
4. **Do not invent tenant IDs, credentials, secrets, connection strings, or Azure resources.** Use sample configuration/placeholders and document required user setup.
5. **Prioritize functional vertical slices** (API + DB + UI + tests) instead of isolated boilerplate.
6. **Run restore/build/tests when possible** and report exact results, failures, and any unverified steps.
7. **Explain key design choices** briefly, especially where multiple patterns are plausible.
8. **Add migrations** for database changes; do not manually patch local databases without migration guidance.
9. **Keep security checks server-side** and cover relevant authorization edge cases with tests.
10. **Update `README.md` and relevant docs** as each phase progresses.
11. **Stop and ask** before deploying to a paid service, creating cloud resources, or making destructive changes.
12. Favor simple, maintainable solutions over adding every pattern or dependency at once.

## 12. Next instruction to paste into Codex

> Read `FINOPS_ENTERPRISE_CODEX_SPEC.md`, `README.md`, `docs/requirements-review.md` and `docs/performance/expense-queries.md`, and examine the repository. Start **Phase 4 only**. First ask me which report-access design to use (the three options are in `docs/requirements-review.md`), and record the choice as an ADR. Then present a short plan for the five reports, reusing `finance.usp_BudgetVsActual` and the server-side department scope pattern. Implement in vertical slices with tests that prove cross-department isolation. Run the build, all tests including `Category=Database`, and the EF drift check, and update `docs/verification.md` with exact results.

## 13. Portfolio demonstration checklist

- [x] Realistic enterprise architecture diagram and ADRs.
- [x] SSO with OAuth 2.0/OIDC and API JWT validation (Entra sign-in pending tenant verification).
- [x] Meaningful RBAC plus department/resource-based authorization.
- [x] Expense workflow and auditable approvals.
- [x] Complex EF Core/LINQ queries plus SQL optimization evidence.
- [ ] SSRS/RDL reports with safe permissions and export.
- [x] Automated unit, integration, and authorization tests.
- [ ] Structured logging, monitoring, and health checks (logging + health done; monitoring pending).
- [ ] Background processing and idempotency.
- [ ] Working local setup and CI/CD pipeline (local setup + CI done; CD pending).
- [ ] Optional cloud deployment with no hard-coded secrets.
- [ ] README explaining tradeoffs, screenshots, and demo flows.

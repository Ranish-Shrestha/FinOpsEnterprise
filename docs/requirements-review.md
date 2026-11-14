# Requirements review and open questions

The specification is coherent for a staged portfolio project. The modular monolith and local-first approach suit its size. The proposed weeks are planning estimates, not acceptance criteria.

## Adjustments made to the specification's plan

- **Tests start with the first vertical slice.** Phase 6 strengthens CI and operations rather than introducing testing.
- **CI moved forward.** GitHub Actions runs from Phase 2 onward instead of waiting for Phase 6 (see [verification.md](verification.md)).
- **Liveness and readiness are separate**, so a missing database is diagnosable.
- **Models are added only when their rules are implemented.** Expense, budget, audit, outbox and reporting models do not exist yet.
- **Migrations are explicit.** No automatic database creation runs at API startup.
- **Demo identities are not migration data.** They come from an explicit Development-only seed command ([ADR 0003](decisions/0003-hardening-and-demo-data.md)).
- **Local dependencies are SQL Server only.** Worker, Redis, Service Bus, SSRS and Azure resources are not scaffolded.

## Phase 2 questions: resolved

| Question | Resolution |
| --- | --- |
| Tenant model | Single Entra tenant (`Security:TenantId`). Multi-tenant is out of scope |
| User provisioning | Explicit. Users are never created from a token. Demo users come only from `seed-demo-data` |
| Department membership timing | Dated, half-open `[start, end)`, evaluated per request |
| Permission change invalidation | No authorization cache. Changes apply on the next request ([ADR 0002](decisions/0002-identity-authorization.md)) |
| Role assignment authority | **Still open.** `user-roles.manage` exists but has no endpoint. Decide in the admin feature whether admins can grant Administrator, and record it in the audit log |

## Questions for later phases

- **Phase 3:** decided 2026-11-14 and recorded in [design/phase3-expenses.md](design/phase3-expenses.md). Administrators do not approve, and only USD is accepted. Round 3a (workflow, concurrency, audit) is done; round 3b (budgets, stored procedure, execution plans) remains.
- **Audit** shipped with the first sensitive mutation (Phase 3a), in the same transaction as each state change. Phase 5 improves delivery (outbox, Service Bus).
- **Phase 4 (SSRS):** SSRS 2022 does not support Entra ID or OIDC sign-in natively, and it runs only on Windows, not in the Linux SQL container. Choose one design before building reports:
  1. The API renders reports through the SSRS REST or URL-access endpoint under a service account, after its own authorization. SQL row-level security, keyed on a session context set by the API, guards the data.
  2. Use Power BI Report Server, which has the same constraint.
  3. Export paginated reports from the API (for example QuestPDF and ClosedXML), and keep the RDL files as a learning artifact.

  UI report parameters alone can never enforce department isolation.
- **Phase 6:** operational budgets and performance targets must be measurable (for example p95 latency of the expense queue at N rows) before adding caching or scaling.
- **End-to-end tests:** use Playwright against mock-mode personas, starting with the Phase 3 approval flow.

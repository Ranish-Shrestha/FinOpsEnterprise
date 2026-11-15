# Requirements review and open questions

The specification is coherent for a staged portfolio project. The modular monolith and local-first approach suit its size. The proposed weeks are planning estimates, not acceptance criteria.

## Adjustments made to the specification's plan

- **Tests start with the first vertical slice.** Phase 6 strengthens CI and operations rather than introducing testing.
- **CI moved forward.** GitHub Actions runs from Phase 2 onward instead of waiting for Phase 6 (see [verification.md](verification.md)).
- **Liveness and readiness are separate**, so a missing database is diagnosable.
- **Models are added only when their rules are implemented.** Expense, budget, audit, outbox and reporting models do not exist yet.
- **Migrations are explicit.** No automatic database creation runs at API startup.
- **Demo identities are not migration data.** They come from an explicit Development-only seed command ([ADR 0003](decisions/0003-hardening-and-demo-data.md)).
- **Local dependencies are SQL Server only.** The Phase 5 worker uses the SQL outbox as its queue. Redis, Service Bus, SSRS and Azure resources are not scaffolded.

## Phase 2 questions: resolved

| Question | Resolution |
| --- | --- |
| Tenant model | Single Entra tenant (`Security:TenantId`). Multi-tenant is out of scope |
| User provisioning | Explicit. Users are never created from a token. Demo users come only from `seed-demo-data` |
| Department membership timing | Dated, half-open `[start, end)`, evaluated per request |
| Permission change invalidation | No authorization cache. Changes apply on the next request ([ADR 0002](decisions/0002-identity-authorization.md)) |
| Role assignment authority | **Still open.** `user-roles.manage` exists but has no endpoint. Decide in the admin feature whether admins can grant Administrator, and record it in the audit log |

## Questions for later phases

- **Phase 3:** decided 2026-11-14 and recorded in [design/phase3-expenses.md](design/phase3-expenses.md). Administrators do not approve, and only USD is accepted. Phase 3 is complete. Execution-plan evidence is in [performance/expense-queries.md](performance/expense-queries.md).
- **Audit** shipped with the first sensitive mutation (Phase 3a), in the same transaction as each state change. Phase 5 added a transactional outbox and a worker. **Decided 2026-11-14, [ADR 0005](decisions/0005-messaging-outbox.md):** a local SQL-backed queue behind a Service Bus-shaped port instead of Azure Service Bus, to avoid cost and keep every test runnable in CI.
- **Phase 4 (reporting): decided 2026-11-14, [ADR 0004](decisions/0004-report-access.md).** The API renders PDF and Excel from the reporting procedures and is the access boundary; RDL files ship as an optional artifact with a deny-by-default scope parameter. The options that were considered:
  1. The API renders reports through the SSRS REST or URL-access endpoint under a service account, after its own authorization. SQL row-level security, keyed on a session context set by the API, guards the data.
  2. Use Power BI Report Server, which has the same constraint.
  3. Export paginated reports from the API (for example QuestPDF and ClosedXML), and keep the RDL files as a learning artifact.

  UI report parameters alone can never enforce department isolation.
- **Phase 6: decided 2026-11-14, [ADR 0006](decisions/0006-quality-targets-and-telemetry.md).** The targets were agreed before measuring: queue p95 < 150 ms, my expenses < 100 ms, one-month report < 300 ms (60k expenses, full HTTP stack), and outbox delivery < 3 s. CI is the reference environment. No caching was added; the measured fix was the per-request access lookup (4 queries → 1).
- **Phase 7: decided 2026-11-14, [ADR 0007](decisions/0007-azure-deployment.md).** Container Apps with scale to zero, serverless SQL on the free offer, Static Web Apps Free, managed identity, OIDC CD. The templates and workflow are written; the owner deploys when ready. The outbox runs in the API process by default, so an idle app doesn't keep SQL awake.
- **End-to-end tests:** done in Phase 6. Playwright runs against mock-mode personas on an isolated stack, covering the approval flow through the worker, department isolation and a report download.

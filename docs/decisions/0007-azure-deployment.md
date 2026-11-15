# ADR 0007: Low-cost Azure deployment

Status: accepted (2026-11-14). Infrastructure as code and the deploy workflow are written and validated locally where possible. **Nothing has been deployed yet:** the owner chose to deploy later.

## Context

Phase 7 asks for an optional, budget-aware Azure deployment with no hard-coded secrets. The owner's choices:

- write the templates and the CD workflow, but don't deploy yet;
- use Container Apps;
- use real Microsoft Entra sign-in.

Constraints:

- **Cost.** It is a portfolio demo, idle most of the time. Always-on compute or a provisioned database would cost more than the demo is worth.
- **Mock identities are unusable.** The API refuses to start in mock mode outside Development, so a deployed app needs Entra.
- **The worker keeps SQL awake.** The Phase 5 worker polls SQL every 2 s. On serverless Azure SQL that prevents auto-pause, so the free offer (100,000 vCore-seconds a month) would be used up in a few days. The database would then pause for the rest of the month.

## Decision

| Concern | Choice | Why |
| --- | --- | --- |
| SPA | Static Web Apps, Free | Static files and a global CDN at no cost. The deploy workflow writes `config.json` and the CSP's API origin into the one production build |
| API | Container Apps (Consumption), 0–2 replicas, HTTP scaling | Scales to zero when idle. 0.5 vCPU and 1 GiB per replica |
| Outbox processing | In the API process (`Messaging:RunInApi=true`) by default. A separate worker app is optional (`deployWorker`) | Nothing polls SQL while the API is scaled to zero, so the database can pause. Messages written by a request are drained while the API is still running (within seconds). The idempotent processor (ADR 0005) is unchanged |
| Database | Azure SQL serverless (GP_S_Gen5, 0.5–1 vCore) with the **free offer** (`useFreeLimit`, auto-pause when the allowance is spent) | Pauses after 60 idle minutes; a paused database costs storage only |
| Database authentication | **Entra-only** server. The apps use a **user-assigned managed identity** (`Authentication=Active Directory Managed Identity`) | No SQL password exists anywhere |
| Runtime database rights | Created by `grant-app-access` (see below) | Read-only `organization` and `identity`; read, insert and update on `finance` and `messaging`; read and insert only on `audit`; execute on `finance` and `audit` procedures. No DELETE, no DDL |
| Schema changes | An explicit pipeline step, `FinOps.Api.dll migrate`, run as the deploying identity (the server's Entra admin) | Migrations still never run at app startup (ADR 0003) |
| Images | GHCR, public, tagged with the commit SHA, with SBOM and provenance | Free, and no registry credential to store |
| Login from GitHub | OpenID Connect federated credential (`azure/login`) | No Azure secret is stored in GitHub. The Static Web Apps deploy token is fetched at deploy time and masked |
| Telemetry | The Container Apps environment's managed OpenTelemetry agent sends traces and logs to Application Insights | The agent sets `OTEL_EXPORTER_OTLP_ENDPOINT`, which switches on the apps' existing opt-in export (ADR 0006) with no code change |
| Logs budget | Log Analytics with a 0.5 GB/day cap and 30-day retention | Bounds the main variable cost |
| CD gating | `deploy.yml` runs only after CI succeeds for a push to `main`, or manually from `main`. It does nothing until `AZURE_DEPLOY_ENABLED=true` | A fork, or a repository not yet set up, never attempts a deployment |

**Creating the app's database user.**

- `grant-app-access` creates the user with `CREATE USER … WITH SID = <client ID>, TYPE = E`, instead of `FROM EXTERNAL PROVIDER`.
- When a service principal runs `FROM EXTERNAL PROVIDER`, the SQL server must look the name up in Entra. That needs Directory Readers or Microsoft Graph permissions, which this setup avoids granting.

**Health probes.** All three probes use the process-only `/health`.

- A readiness probe on `/health/ready` would hold every cold start until a paused database resumes, which can take up to about a minute.
- The deploy workflow's smoke test, and any availability test, use `/health/ready`.

## Consequences

- **Expected cost when idle:** close to the price of storage and log ingestion.
  - The SQL free offer covers about 27 compute hours a month at 1 vCore.
  - Container Apps' monthly free grant covers light demo traffic.
  - Static Web Apps Free costs nothing.
  - Exact prices vary by region and change over time, so check the Azure pricing calculator before deploying.
  - The optional always-on worker app adds a few dollars a month.
- **Cold starts.** After an idle period, the first request waits for a replica to start and possibly for SQL to resume (tens of seconds). The SPA shows *Checking connections* while it waits. This is acceptable for a demo; set `minReplicas: 1` and disable auto-pause for a real service.
- **Firewall.** The SQL firewall allows Azure services, because Consumption Container Apps have no fixed outbound IP.
  - Entra-only authentication is the control that matters here.
  - A VNet-integrated environment with a private endpoint is the hardening step, at extra cost.
- **Delivery while scaled to zero.** With the in-process option, a message whose processing fails waits for its retry until the API runs again. Notifications can therefore be delayed while the app is idle. Set `deployWorker=true` if that matters.
- **Verified locally, before any deployment:**
  - The images build and run as non-root, with no shell.
  - Production mode honours forwarded headers and HSTS, and rejects mock identities.
  - CORS allows only the configured SPA origin.
  - PDF and Excel render on the Linux image.
  - The `migrate` and `grant-app-access` commands work.
  - The full business flow and the worker run with only the runtime grants (`DeploymentTests`).
- **Not verified until deployed:**
  - the Bicep deployment itself;
  - managed-identity SQL login and `TYPE = E` user creation, which are Azure-only;
  - Entra sign-in end to end;
  - the managed OpenTelemetry agent;
  - Static Web Apps headers.

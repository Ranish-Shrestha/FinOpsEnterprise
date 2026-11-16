# Demo walkthrough

A script for a 6–8 minute recorded demo, or a live run-through for a reviewer. It uses the local stack in mock mode: start SQL Server, the API, the worker and the UI as in the [README quick start](../README.md#quick-start-mock-identities). It also uses the optional telemetry dashboard. Every name and amount is synthetic.

Prepare before recording:

- **Seed:** run `seed-demo-data`, then `seed-demo-expenses` so the dashboard and reports have a few months of history.
- **Dashboard:** start it with `docker compose --profile observability up -d aspire-dashboard`.
- **Tabs:** open http://127.0.0.1:4200 (it goes to `/login`: pick a persona and click *Continue*) and http://localhost:18888 in two browser tabs.
- **Navigation:** each area is its own page in the sidebar (*Overview*, *Expenses*, *Budgets*, *Reports*, *Notifications*, *Outbox*). Switch persona from the top bar; the sidebar shows only the pages that persona may use.

## 1. What it is (30 s)

- An internal finance app: employees claim expenses, managers approve them, finance posts them, administrators audit.
- **Built to show** how authorization, data integrity and reliability are engineered, not just screens.
- **Stack:** ASP.NET Core 10, EF Core, SQL Server, Angular 21, Entra ID, OpenTelemetry, Playwright; Azure-ready.

## 2. Identity is not authorization (60 s)

1. **Demo Employee:** the dashboard greeting lists only *Demo Engineering*, and there's no *To approve* tile.
2. **Demo Manager:** only *Demo Finance*, because the Operations membership has expired.
   - Point out that the token only says who the caller is.
   - Roles, permissions and dated memberships come from SQL Server.
3. **Demo Former Employee:** no access (403). The account is inactive even though the token would be valid.

## 3. The expense workflow and its guard rails (2 min)

1. **Demo Employee:** *New expense* → $37.25 Travel → *Create draft* → *Submit for approval*.
2. **Demo Engineering Manager:** *Approval queue*, open it, then *Approve*.
   - Open their own "Team lunch after the release": there is no approve button.
   - The API would refuse with 403 anyway. That's separation of duties, enforced in the domain.
3. **Demo Finance Officer:** *Ready to post* → *Post to ledger*.
4. **Concurrency (optional):** open the same expense in two tabs and approve it in both.
   - The second gets *Modified by someone else* (412, from ETag and rowversion).
5. **Demo Administrator:** the audit trail (`GET /api/audit-events`, or the *Audit activity* report) shows every step, including denied attempts.

## 4. Reliable background processing (90 s)

1. **Demo Employee**, *Notifications* → *Refresh*:
   - "approved" and "posted" notifications appear;
   - the expense shows *Reimbursement: Scheduled*.
2. **Explain the mechanism:**
   - The post wrote an outbox event in the same transaction.
   - The worker delivered it at least once.
   - The handlers are idempotent, so exactly one reimbursement exists.
3. **Stop the worker** (Ctrl+C), post another expense, and refresh: nothing yet.
   - Start the worker again, and the notification arrives. No event was lost.
4. **Demo Administrator** → *Outbox* (`/admin/outbox`): pending and dead-letter counts, and *Replay*.

## 5. One trace across processes (60 s)

In the dashboard's *Traces* view, open `POST /api/expenses/{id}/post` ([screenshot](screenshots/trace.png)):

- the HTTP request and its SQL statements, including a single query for the caller's access;
- then, later and in the worker process, `finops.outbox expense.posted.v1` with the reimbursement handler running before the notification handler;
- the same correlation ID in the logs and on the audit row.

## 6. Reports with server-side isolation (60 s)

1. **Demo Manager:** in *Reports*, *Preview* the *Department expense summary*, then download *Excel*.
2. Every row, including inside the file, is Demo Finance only.
   - The API resolves the departments; nothing in the request can widen them.
   - Exports are audited.
3. **Demo Finance Officer:** the same report covers all departments.

## 7. Evidence, not claims (60 s)

Show the repository:

- **Tests:**
  - [CI](../.github/workflows/ci.yml) runs unit, HTTP and real SQL Server tests (Testcontainers);
  - Playwright journeys;
  - container builds and Bicep lint.
- **Performance:** [execution-plan evidence and latency targets](performance/expense-queries.md), with before/after numbers measured against targets agreed in advance.
- **Decisions:** [ADRs](decisions/), including what was traded away and what is still unverified ([verification log](verification.md)).
- **Deployment:** [Azure templates and CD](operations/deployment.md).
  - It scales to zero, uses the free SQL offer, stores no secrets, and connects to SQL through a managed identity with least-privilege grants.
  - Tests prove those grants are sufficient.

## Likely questions

| Question | Short answer |
| --- | --- |
| Why not trust token roles? | The app owns authorization data; tokens only identify. Revoking access takes effect on the next request |
| Why a SQL outbox and not Service Bus? | Cost, and testability in CI. The broker port is shaped like Service Bus ([ADR 0005](decisions/0005-messaging-outbox.md)) |
| What happens if the worker crashes mid-message? | The lease expires, another delivery runs, and the idempotency records stop duplicate effects (tested) |
| Is the report target met? | In CI, the reference environment. Locally, a durable commit on Docker Desktop alone can exceed it ([ADR 0006](decisions/0006-quality-targets-and-telemetry.md)) |
| What is not production-ready? | Private networking for SQL, alerting rules, a user-admin UI, and a real payment integration. They are listed in the ADRs |

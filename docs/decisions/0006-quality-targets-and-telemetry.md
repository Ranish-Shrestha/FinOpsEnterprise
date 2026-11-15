# ADR 0006: Measured targets, OpenTelemetry and end-to-end tests

Status: accepted (2026-11-14).

## Context

Phase 6 asks for end-to-end tests in pull-request CI, observable key failures, and performance work "against measured targets", with before/after evidence. The targets had to be agreed before any tuning, so they could not be chosen to fit the results.

## Decision

1. **Targets.** The owner confirmed these on 2026-11-14:

   | Scenario | Target |
   | --- | --- |
   | Manager approval queue, `GET /api/expenses?status=Submitted&sort=submittedAt` | p95 < 150 ms |
   | Employee "my expenses", `GET /api/expenses?mine=true` | p95 < 100 ms |
   | Budget vs. actual, one month, JSON (`GET /api/reports/budget-vs-actual`) | p95 < 300 ms |
   | Outbox delivery, from posting to the reimbursement being scheduled, worker polling every 2 s | p95 < 3 s |

   - **Data:** 60,000 synthetic expenses, the same deterministic set as the query-plan tests.
   - **Method:** full HTTP stack on the in-memory test server (no network or TLS), 20 warm-up requests and then 200 measured; 20 postings for the outbox.
   - **Reference environment:** CI (GitHub `ubuntu-24.04`). `PerformanceTests` fail CI if a target is missed.
   - **Local runs:** the tests print the same table, plus the machine's own SQL round-trip and durable-commit latency, but don't fail. A laptop's Docker disk can make one commit slower than a whole target.
2. **OpenTelemetry, off by default.**
   - Turned on only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The development launch profiles set it to the optional Aspire dashboard (`docker compose --profile observability`).
   - **Traces:** ASP.NET Core, SqlClient, and the `FinOps` source: one consumer span per outbox delivery and one per handler.
   - **Metrics:** ASP.NET Core, the .NET runtime, and the `FinOps` meter.
   - **Logs:** exported with their trace context.
   - **Libraries:** OpenTelemetry 1.20.1, all stable packages, Apache-2.0.
3. **One trace per business action, across processes.** The outbox stores the writing request's W3C `traceparent`, and the worker's span uses it as parent. A posting and its asynchronous reimbursement therefore show as one trace in the API and the worker.
4. **Application telemetry uses BCL types.** `FinOpsDiagnostics` defines its `ActivitySource` and `Meter` in the Application layer and has no SDK dependency.
   - Tags have low cardinality (action, outcome, message type, reason).
   - Tags never include user, expense or department IDs.
5. **End-to-end tests (Playwright, Chromium only).**
   - **Journeys covered:**
     - the full employee → manager → finance flow, through the worker's notification and reimbursement;
     - department isolation, in the UI and at the API (404);
     - an Excel report download.
   - **Isolated stack:** the tests start their own API (port 5199, plain HTTP) and UI (port 4300) against a database named in `E2E_CONNECTION_STRING`. Global setup starts the worker. So they never touch a developer's running stack.
   - **In CI:** a new `e2e` job runs them on every push and pull request, against a disposable SQL Server with a per-run password.

## Consequences

- **Before/after evidence.**
  - The first measurement found that the per-request access lookup made four sequential queries; it is now one.
  - Measured back to back on the same machine, `GET /api/me` went from p95 22.4 ms to 8.9 ms. The list endpoints dropped by about 10 ms. See [performance](../performance/expense-queries.md#http-latency-targets-phase-6).
- **The report target is commit-bound locally.**
  - On the development laptop, a one-row durable commit alone has p95 250–360 ms, and every report run commits its `report.exported` audit row.
  - The audit write was **not** weakened (no delayed durability, no fire-and-forget) to meet the number.
  - CI decides whether the target is met. If CI misses it, the options are an owner decision: audit file exports only, or relax the target.
- **E2E found a real bug.**
  - The "posted, reimbursement scheduled" notification could appear before the reimbursement committed, because the notification handler ran first.
  - Handlers now run with the money effect first, and a failure stops the rest. ADR 0005 has been amended.
- **The dashboard is unauthenticated.** It binds to `127.0.0.1` only and is for local use. A deployed collector needs authentication (Phase 7).
- **Short-lived processes may not export metrics.** Metrics export every 60 s. A process killed sooner (for example the E2E worker) may export traces and logs but no metrics. Long-running hosts are unaffected.

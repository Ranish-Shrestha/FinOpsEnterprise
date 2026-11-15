# Observability

What the API and the worker emit, how to look at it locally, and which signals indicate a failure. Decision: [ADR 0006](../decisions/0006-quality-targets-and-telemetry.md).

## Turning it on

Telemetry is exported only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The development launch profiles of both the API and the worker set it to `http://localhost:4317`, so locally you only need a receiver:

```powershell
docker compose --profile observability up -d aspire-dashboard
```

Then open **http://localhost:18888**. If the dashboard isn't running, export attempts fail quietly in the background and the app is unaffected. Stop it with `docker compose --profile observability stop aspire-dashboard`.

The dashboard is unauthenticated and binds to `127.0.0.1` only. Never expose it.

## Signals

| Signal | Source | Notes |
| --- | --- | --- |
| HTTP server spans and metrics | ASP.NET Core instrumentation | `/health*` is excluded from traces |
| SQL spans | SqlClient instrumentation | Statement summaries such as `SELECT [finance].[ExpenseRequests]`. No parameter values |
| `process {type}` spans (consumer) | `FinOps` source, worker | Parent is the request that wrote the event (stored `traceparent`). Tags: message ID, type, delivery count, correlation ID |
| `handle {handler}` spans | `FinOps` source, worker | `finops.messaging.skipped=already_processed` on idempotent skips |
| Logs | Both | Structured, with scopes (correlation ID, message ID) and trace context |
| Runtime metrics | `System.Runtime` meter | GC, thread pool, exceptions |

## Application metrics (`FinOps` meter)

| Instrument | Type | Tags | Use |
| --- | --- | --- | --- |
| `finops.expense.transitions` | Counter | `action` | Business throughput |
| `finops.authorization.denials` | Counter | `action`, `reason` | Spikes can mean probing or a broken client |
| `finops.messaging.settled` | Counter | `messaging.destination.name`, `outcome` (`completed`, `retried`, `dead_lettered`, `lock_lost`) | Retry and dead-letter rates |
| `finops.messaging.delivery.latency` | Histogram (s) | `messaging.destination.name` | From the business change to completion. Target p95 < 3 s |
| `finops.messaging.backlog` | Gauge | none | Undelivered messages, sampled every 15 s by the worker |
| `finops.messaging.dead_letters` | Gauge | none | Messages awaiting an operator |
| `finops.messaging.oldest_pending.age` | Gauge (s) | none | Grows when the worker is down or stuck |

Tags never contain user, expense or department IDs.

## Suggested alerts

These are not configured anywhere yet; they belong with a deployed collector (Phase 7).

| Condition | Meaning | First step |
| --- | --- | --- |
| `finops.messaging.dead_letters` > 0 | A message needs an operator | Open the *Notifications → Outbox* panel as an administrator. Read `LastError`, fix the cause, then *Replay* |
| `finops.messaging.oldest_pending.age` > 60 s | The worker is down, stuck or overloaded | Check the worker's logs and that it can reach SQL Server |
| Rate of `outcome=retried` rising | A dependency is failing transiently | Look at the error spans of `process …` for the exception |
| `http.server.request.duration` p95 above the targets | A latency regression | Compare with `PerformanceTests` and `docs/performance` |
| `finops.authorization.denials` spike | Probing, or a broken client | Check the audit trail (`Denied` outcomes) |

The worker publishes the outbox gauges, so if the worker is down they stop updating. In that case, alert on missing data as well.

## Following one business action

1. Find the request: by `X-Correlation-ID` (in the response and in every log line), or in the dashboard's *Traces* view.
2. The same trace contains the request's SQL and, later, the worker's `process expense.posted.v1` span with its handlers. These are linked through the stored `traceparent`, even though they run in another process seconds later.
3. Audit rows written by the worker (`reimbursement.scheduled`) carry the same correlation ID.

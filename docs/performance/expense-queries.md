# Expense and report query performance

Status: measured 2026-11-14 and re-measured after Phase 4 on SQL Server 2022 CU28 (Linux container). Reproduced on every CI run by
`tests/FinOps.IntegrationTests/QueryPlanTests.cs`, which fails if a query regresses to a table scan or loses its read reduction.

## How it is measured

- **Data:** a dedicated database with **60,000 synthetic expenses**: 6 requesters, 3 departments, dates over the last year. 85% are posted, 5% each are submitted, approved and draft. Statistics are updated with `FULLSCAN`.
- **The SQL is exactly what production sends.** The test calls the same query builders as `ExpenseReader` (`ExpenseQueries`) and the real stored procedure, then executes the SQL as EF does: through `sp_executesql` with typed parameters, so parameter sniffing matches production.
- **Metrics:**
  - **Logical reads** on `finance.ExpenseRequests` (`SET STATISTICS IO`), page 1 of 25 rows. Logical reads are deterministic, unlike wall-clock time on a shared laptop or CI runner.
  - **Plan operators** from the *actual* plan (`SET STATISTICS XML`).
- **Plan cache:** cleared before every measurement (`DBCC FREEPROCCACHE`, test database only).

## Results

| Screen | Before (Phase 3a) | After: step 1 (IDs) | After: step 2 (rows by key) | Plan after |
| --- | ---: | ---: | ---: | --- |
| Employee: my expenses | 2,667, clustered scan | **4** | ~105 | Index Seek `IX_ExpenseRequests_Requester` |
| Finance: ready to post | 2,667, clustered scan | **4** | ~105 | Index Seek `IX_ExpenseRequests_Status` |
| Manager approval queue | 2,667, clustered scan | **62** | ~105 | Index Seek `IX_ExpenseRequests_Status` |

| Report (`finance.usp_BudgetVsActual`, all departments) | Without index | With `IX_ExpenseRequests_Reporting` | Plan after |
| --- | ---: | ---: | --- |
| One month | 2,643, clustered scan | **41** | Index Seek on `ExpenseDate` |
| Whole year | 2,643, clustered scan | **891** | Index Seek (range is most of the table; the covering index is about 3× narrower than the table) |

A full list page went from 2,667 reads to about 110 (employee and finance) or 167 (manager), which is **16–25× fewer**. Without workload indexes, every query scans the whole table.

## What was wrong, and the fixes

The Phase 3a indexes existed but **no list query used them**. Every screen did a clustered scan. There were three causes:

1. **The sort direction defeated the index.** The pages sorted `CreatedAtUtc DESC, Id ASC`. A backward read of `(RequesterId, CreatedAtUtc)` yields `CreatedAtUtc DESC, Id DESC`, so SQL Server had to read and sort *every* matching row.
   **Fix:** the `Id` tie-breaker now follows the sort direction (`ExpenseQueries.Page`).
2. **Key lookups cost more than a scan.** The indexes weren't covering, and each candidate row needed a lookup into the table, about 3 reads per row. For a queue of about 1,000 submitted expenses that exceeds the 2,667-page scan, so the optimizer was right to scan.
   **Fix: two-step paging.** Step 1 selects only the 25 IDs of the page, from indexes that `INCLUDE` every column the filter and sort touch. Step 2 fetches those 25 rows by primary key (`ExpenseQueries.PageIds`, `ExpenseReader.ListAsync`).
3. **No index served status-only views.** "Ready to post" filters on `Status` alone.
   **Fix:** `IX_ExpenseRequests_Status (Status, CreatedAtUtc) INCLUDE (DepartmentId, RequesterId, SubmittedAtUtc)`.

Also changed:

- **Visibility predicate.** It was one catch-all `RequesterId = @me OR @readAll = 1 OR (@readDepartment = 1 AND DepartmentId IN (...))`. Each caller's flags are now applied in C#, so finance gets no filter, employees get `RequesterId = @me`, and managers get `RequesterId = @me OR DepartmentId IN (...)`. The cached plan no longer has to serve every kind of caller.
- **Stored procedure.** It originally referenced a `Spend` CTE twice, and SQL Server evaluates a CTE at each reference, so it aggregated the expenses twice: 5,314 reads, then 1,610 with the index. It now aggregates once into `#Spend`, already limited to the caller's departments: 2,667 reads without the index, **38 / 825** with it. Phase 4 added `Category` to the reporting index for `usp_ExpenseSummary`, which widened it slightly (now **41 / 891**). The test requires at least 10× fewer reads for one month and 2× for a whole year.

## Known limits

- **Manager queue index choice.** The queue seeks `IX_ExpenseRequests_Status` rather than `IX_ExpenseRequests_ApprovalQueue`, because "or my own expense" spans departments. At 5% submitted that costs 62 reads. If submitted expenses become a large share of history, consider splitting the query into a `UNION` of the department branch and the own-expense branch.
- **Unoptimized sorts and counts.** Sorting by `amount` or `expenseDate` isn't covered by an index; those sorts read more, which is acceptable for occasional use. The `COUNT(*)` for the paging total uses the same predicates and indexes but isn't measured separately.
- **Scale.** 60,000 rows is a portfolio-scale dataset. The relative behavior (seek versus scan) is what matters. Absolute numbers will grow with the data.

## HTTP latency targets (Phase 6)

The targets were agreed before measuring ([ADR 0006](../decisions/0006-quality-targets-and-telemetry.md)). `tests/FinOps.IntegrationTests/PerformanceTests.cs` measures them through the full HTTP stack: routing, authentication, the per-request access lookup, authorization, EF Core and SQL Server.

- **Data:** the same 60,000 expenses as above.
- **Samples:** 20 warm-up requests, then 200 measured sequential requests per endpoint; 20 postings for the outbox.
- **Enforcement:** CI enforces the targets. Local runs print them, together with the machine's own SQL costs.

### Before and after: one query for the access lookup

The first measurement showed every request spending 15–20 ms on the access lookup before doing any work. `UserAccessReader` ran four sequential queries (profile, roles, permissions, memberships). It now runs **one**, with the three collections projected into a single statement.

Both columns were measured back to back on the same machine (Windows laptop, Docker Desktop, SQL Server 2022 CU28) on 2026-11-14:

| Endpoint (p50 / p95, ms) | Before: 4 queries | After: 1 query | Target p95 |
| --- | ---: | ---: | ---: |
| Access lookup only (`GET /api/me`) | 15.9 / 22.4 | **6.6 / 8.9** | none |
| Manager approval queue | 29.3 / 38.6 | **19.6 / 26.2** | 150 |
| Employee: my expenses | 26.8 / 35.5 | **20.2 / 25.4** | 100 |
| Budget vs. actual, one month (JSON) | 93.9 / 460.1 | 75.6 / 398.8 | 300 |
| *Environment: one-row durable commit* | *66.9 / 360.1* | *65.3 / 340.3* | none |

The final full run on the same laptop gave:

- approval queue p95 15.0 ms;
- my expenses p95 14.4 ms;
- outbox delivery p50 1.19 s and p95 2.49 s (target 3 s).

### The report is bound by one commit, not by its query

- **Where the time goes:**
  - the stored procedure takes about 7 ms;
  - the Budgets panel endpoint, which runs the same procedure without writing, takes about 22 ms;
  - the report endpoint also commits its `report.exported` audit row.
- **Why that's slow here:** on this laptop a bare one-row commit has p95 250–360 ms. SQL Server's wait statistics show `WRITELOG` and `PAGEIOLATCH` waits on Docker Desktop's virtual disk. A `SELECT 1` round trip takes under 3 ms.
- **What was not done:** the audit write keeps full durability. No delayed durability and no fire-and-forget, just to meet a number.
- **Confirmed with interleaved samples (Phase 7).** The first harness measured the commit baseline in a separate phase. On this disk, commit latency swings from run to run, so the two numbers weren't comparable. The harness now times a probe commit after every request. An interleaved breakdown on 2026-11-14:
  - the no-write endpoint: p50 21.9 / p95 29.6 ms;
  - one EF audit insert: p50 59.4 / p95 273.5 ms;
  - the report: p50 77.8 / p95 263.0 ms. That is the query plus that one commit.
- **Locally the result straddles the target.** Across runs, the report's p95 has been between 263 and 411 ms; the latest was 285 ms (met).
- **What decides it:** CI. If CI misses the target as well, the owner chooses between auditing only file exports and relaxing the target.

### Outbox delivery

The real hosted worker polls every 2 s when idle. Postings are spread across the poll cycle, so the samples include the worst case (a message written just after a poll).

- Delivery latency is the poll wait plus two handler commits.
- Its p95 is therefore close to the poll interval plus the commit time.
- In production, `finops.messaging.delivery.latency` reports the same quantity (see [observability](../operations/observability.md)).

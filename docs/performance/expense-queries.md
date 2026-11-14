# Expense and report query performance

Status: measured 2026-11-14 on SQL Server 2022 CU28 (Linux container). Reproduced on every CI run by
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
| One month | 2,667, clustered scan | **38** | Index Seek on `ExpenseDate` |
| Whole year | 2,667, clustered scan | **825** | Index Seek (range is most of the table; the covering index is about 3× narrower than the table) |

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
- **Stored procedure.** It originally referenced a `Spend` CTE twice, and SQL Server evaluates a CTE at each reference, so it aggregated the expenses twice: 5,314 reads, then 1,610 with the index. It now aggregates once into `#Spend`, already limited to the caller's departments: 2,667 reads without the index, **38 / 825** with it.

## Known limits

- **Manager queue index choice.** The queue seeks `IX_ExpenseRequests_Status` rather than `IX_ExpenseRequests_ApprovalQueue`, because "or my own expense" spans departments. At 5% submitted that costs 62 reads. If submitted expenses become a large share of history, consider splitting the query into a `UNION` of the department branch and the own-expense branch.
- **Unoptimized sorts and counts.** Sorting by `amount` or `expenseDate` isn't covered by an index; those sorts read more, which is acceptable for occasional use. The `COUNT(*)` for the paging total uses the same predicates and indexes but isn't measured separately.
- **Scale.** 60,000 rows is a portfolio-scale dataset. The relative behavior (seek versus scan) is what matters. Absolute numbers will grow with the data.

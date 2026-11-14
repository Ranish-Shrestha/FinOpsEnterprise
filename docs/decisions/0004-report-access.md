# ADR 0004: Report access and rendering

Status: accepted (2026-11-14).

## Context

The spec asks for five parameterized SSRS/RDL reports with PDF and Excel export. It also asks that report access be guarded "at both the application layer and the reporting server/data source level", and warns that "filtering UI parameters is [not] enough to prevent cross-department data leaks".

There are three constraints:

- **No user sign-in.** SSRS 2022 and Power BI Report Server cannot sign users in with Microsoft Entra ID or OIDC. They authenticate Windows users, and nothing maps those to this application's users.
- **Windows only.** SSRS runs only on Windows. It cannot run in the Linux SQL Server container or on CI's Linux runners, so SSRS rendering cannot be tested automatically.
- **Report parameters are not a security boundary.** Anyone who can run an SSRS report can usually change its visible parameters.

## Decision

1. **The API is the report boundary.** `GET /api/reports/{key}` checks permissions first (`reports.read.department`, `reports.read.all`, and `audit.read` for the audit report). It then resolves the caller's department scope from their active memberships on the server, and only then calls the stored procedure. Nothing in the request can widen the scope.
2. **The stored procedures are the single source of report data** (`finance.usp_ExpenseSummary`, `usp_BudgetVsActual`, `usp_ApprovalAging`, `usp_TransactionLedger`, `audit.usp_AuditActivity`):
   - All inputs are typed parameters, and each procedure validates its own inputs (THROW 50001–50006), so it is safe to call from any client.
   - The department scope is a JSON array. NULL means all departments and is sent only for `*.read.all`. An empty array returns nothing.
3. **The API renders PDF (QuestPDF) and Excel (ClosedXML)** from the same `ReportTable` that the JSON preview returns, so all three formats show identical rows. This is fully testable in CI, including department isolation inside the exported Excel file.
4. **RDL files ship as a deployable artifact** (`reports/SSRS`), generated from one definition table and calling the same procedures:
   - Their `DepartmentIds` parameter is **hidden and defaults to `[]`**, so a user running a report directly on a report server sees nothing.
   - A real deployment must follow [reports/SSRS/README.md](../../reports/SSRS/README.md).
5. **Exports are audited** (`report.exported`, with report, format and row count) and served with `Cache-Control: no-store`.

## Consequences

- **Isolation is enforced in one place, in CI.** Department isolation for every format is tested against real SQL Server. The tests open the Excel file and assert that no other department's name appears in any cell.
- **SSRS is not exercised in CI.** The RDL files are checked for well-formed XML, and for a data contract (every dataset's procedure and parameters exist in the migrated database), but they are **not rendered or validated against Microsoft's XSD**. Microsoft's published schema URL returns 404, and no SSRS instance is available.
- **Data-source isolation** (the spec's "reporting server/data source level") is implemented by the procedures' deny-by-default scope and by giving a report server only a login that can `EXECUTE` the reporting procedures. Full SQL Server row-level security keyed on `SESSION_CONTEXT` would be the next step if users ever reach the report server directly. It is documented, not implemented.
- **Row limit.** Row-returning reports stop at 5,000 rows and ask the user to narrow the date range, rather than streaming arbitrarily large files.
- **Licensing.** QuestPDF is used under its Community License, which is free for individuals and organizations below USD 1M annual revenue. A commercial deployment above that would need a paid license or another renderer. ClosedXML is MIT.

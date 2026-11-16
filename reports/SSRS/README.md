# SSRS report definitions

Five RDL 2016 reports matching the API's reports, plus a shared data source:

| File | Procedure | Parameters |
| --- | --- | --- |
| `ExpenseSummary.rdl` | `finance.usp_ExpenseSummary` | From, To, DepartmentIds (hidden) |
| `BudgetVsActual.rdl` | `finance.usp_BudgetVsActual` | Year, Month, DepartmentIds (hidden) |
| `ApprovalAging.rdl` | `finance.usp_ApprovalAging` | AsOfUtc (hidden), DepartmentIds (hidden) |
| `TransactionLedger.rdl` | `finance.usp_TransactionLedger` | From, To, DepartmentIds (hidden), MaxRows (hidden) |
| `AuditActivity.rdl` | `audit.usp_AuditActivity` | From, To, MaxRows (hidden) |
| `FinOps.rds` | Shared data source | Connection only; credentials are set on the server |

**The application does not need SSRS.** The API renders the same reports as PDF and Excel (see [ADR 0004](../../docs/decisions/0004-report-access.md)). These files show how the same data contract serves a report server.

## How they are produced and checked

- **Generated, not hand-edited.** They come from `tools/generate_rdl.py`. After changing a report, run `python reports/SSRS/tools/generate_rdl.py` and commit the output. IDs are deterministic, so unchanged reports regenerate identically.
- **Checked by CI** (`ReportIntegrationTests.Rdl_datasets_call_procedures_that_exist_with_matching_parameters`): each dataset's stored procedure exists in the migrated database, and every query parameter is a real parameter of that procedure.
- **Not checked:** the files have not been rendered by an SSRS instance or validated against Microsoft's RDL schema. Open them in Report Builder before relying on the layout.

## Security: read before deploying

SSRS cannot authenticate this application's Entra users, so **SSRS must never be the access boundary.**

- **Scope parameter.** `DepartmentIds` is hidden and defaults to `[]`, so a report run directly returns **no rows**. Never give it a broader default, and never make it visible.
- **Service account only.** Grant report-server access to a service account, not to end users. The API (or another trusted service) authorizes the user, resolves their departments and requests the report through URL access or the REST API, passing `DepartmentIds` itself.
- **Least-privilege login.** Give the data source a SQL login that can only `EXECUTE` the five reporting procedures: no table access, no `sa`.
- **Direct user access.** If end users ever reach the report server directly, add SQL Server row-level security keyed on `SESSION_CONTEXT` before allowing it.

## Deploying locally (optional)

Step by step, together with Power BI: [docs/operations/reporting-tools.md](../../docs/operations/reporting-tools.md). In short:

1. Install **SQL Server 2022 Reporting Services** (free Developer edition) on a Windows machine with access to the database. Configure it in Report Server Configuration Manager; its catalog can live on the Docker SQL Server.
2. Create the least-privilege login with `dotnet run --project src/FinOps.Api -- grant-reporting-access`, with the password in `FINOPS_REPORTING_PASSWORD`. It can only `EXECUTE` the five procedures and read the `reporting` views.
3. In the web portal, create the `FinOps` shared data source from `FinOps.rds`, using that login.
4. Upload the five `.rdl` files and bind each to the `FinOps` data source.
5. Run a report as an administrator with `DepartmentIds:isnull=true` in the URL to check the layout. Users should only ever reach reports through the trusted service.

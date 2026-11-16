# SSRS and Power BI on the local database

How to point SQL Server Reporting Services and Power BI Desktop at the local FinOps database, at no cost. The app doesn't need either: its Reports page already renders PDF and Excel ([ADR 0004](../decisions/0004-report-access.md)). This guide is for demos, and for showing that the same data contract serves standard BI tools.

| Tool | What it uses | Cost |
| --- | --- | --- |
| SSRS 2022, Developer edition | The five RDL reports in [reports/SSRS](../../reports/SSRS), calling the reporting stored procedures | Free, Windows only |
| Power BI Desktop | The flat `reporting` views, through [reports/PowerBI/FinOps.pbids](../../reports/PowerBI/FinOps.pbids) | Free, Windows only |
| Power BI Report Server, Developer edition | Both: `.pbix` and `.rdl` on one server | Free for development |

## 0. Prepare the database (once)

From the repository root, with SQL Server running (`docker compose up -d sqlserver`):

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet ef database update --project src/FinOps.Infrastructure --startup-project src/FinOps.Api
dotnet run --project src/FinOps.Api -- seed-demo-data
dotnet run --project src/FinOps.Api -- seed-demo-expenses --months 6

# A read-only login for both tools. Choose your own strong password; it stays out of the command line.
$env:FINOPS_REPORTING_PASSWORD = "<a strong password>"
dotnet run --project src/FinOps.Api -- grant-reporting-access
Remove-Item Env:FINOPS_REPORTING_PASSWORD
```

- **`database update`** creates the `reporting` schema and its views (migration `ReportingViews`).
- **`seed-demo-expenses`** gives the reports months of history to show.
- **`grant-reporting-access`** creates the SQL login `finops_reporting`, or resets its password if it exists. Use `--login <name>` for a different name. It runs only in Development.

**What the login can do:**

- Read the four `reporting` views and run the five reporting procedures.
- Nothing else: no tables, no email addresses, no writes. A test checks this (`ReportingAccessTests`).

**What the login can't enforce:** the views are **not department-scoped**, so whoever uses this login sees every department. That's fine on your machine, but don't share it with end users. See [Security](#security) below.

### The reporting views

| View | One row per | Useful columns |
| --- | --- | --- |
| `reporting.Departments` | department | `DepartmentId`, `Department` |
| `reporting.Expenses` | expense, every status | `Department`, `Requester`, `Category`, `Amount`, `Status`, `ExpenseDate`, `ExpenseMonth`, `PostedDate`, `HoursToDecision`, `DaysSubmittedToPosted` |
| `reporting.ApprovalDecisions` | approve or reject decision | `ExpenseId`, `Approver`, `Outcome`, `Comment`, `DecidedDate` |
| `reporting.BudgetVsActual` | department and month | `MonthStart`, `Budget`, `Posted`, `Committed`, `Pending`, `Remaining`, `UtilizationPercent` |

`BudgetVsActual` uses the same rules as `finance.usp_BudgetVsActual`, and a test checks that their totals match.

## 1. SSRS

1. **Install it.**
   - Download *SQL Server 2022 Reporting Services* from Microsoft, choose the **Developer** edition, and install.
   - Microsoft has said SSRS 2022 is the last SSRS release. New on-premises reporting moves to Power BI Report Server, which uses the same RDL files, so the steps below also work there.
2. **Configure it** in *Report Server Configuration Manager*:
   - **Service account:** keep the default virtual service account.
   - **Database:** create a new report server database on `localhost,14333`, using the `sa` login from your user secrets. It only stores the report catalog. If the wizard refuses the container's self-signed certificate, see [Troubleshooting](#troubleshooting).
   - **Web Service URL** and **Web Portal URL:** apply the defaults. The portal opens at http://localhost/Reports.
3. **Create a folder** called `FinOps` in the web portal.
4. **Create the data source:** *New → Data source*, named `FinOps`:
   - Type: *Microsoft SQL Server*
   - Connection string: the one in [reports/SSRS/FinOps.rds](../../reports/SSRS/FinOps.rds)
   - Credentials: *Using the following credentials*, **Database user name**, `finops_reporting` and your password
   - Click *Test connection*.
5. **Upload the reports:** *Upload* the five `.rdl` files from `reports/SSRS`. For each one, go to *Manage → Data sources* and point it at the shared `/FinOps/FinOps` data source.
6. **Run a report.** It shows **no rows**. That's intended: the hidden `DepartmentIds` parameter defaults to an empty list, so a report opened directly on the server is denied by default. For your local demo, show every department in one of these ways:
   - **URL access** (leaves the report unchanged):
     `http://localhost/ReportServer?/FinOps/ExpenseSummary&rs:Command=Render&DepartmentIds:isnull=true`
   - **Portal:** *Manage → Parameters → DepartmentIds*, tick **Null** as the default. Undo this before anyone else can reach the server.
7. **Export.** The toolbar exports to PDF, Excel, Word and CSV.

**First run:** these RDL files have never been rendered by a real server. If a layout looks off, open the file in **Report Builder** (free), fix it, and copy the change into [the generator](../../reports/SSRS/tools/generate_rdl.py).

## 2. Power BI Desktop

1. **Install** Power BI Desktop from the Microsoft Store (free).
2. **Connect:** double-click [reports/PowerBI/FinOps.pbids](../../reports/PowerBI/FinOps.pbids). Power BI opens with the server and database already filled in.
   - When it asks for credentials, choose **Database**, then `finops_reporting` and your password.
   - If it complains about the certificate, see [Troubleshooting](#troubleshooting).
3. **Load data:** in the navigator, tick the four `reporting` views and click **Load**. Only the views appear, because that's all the login can read.
4. **Model:** in the model view, make sure these relationships exist. Power BI usually detects the first three.
   - `Departments[DepartmentId]` 1 → * `Expenses[DepartmentId]`
   - `Departments[DepartmentId]` 1 → * `BudgetVsActual[DepartmentId]`
   - `Expenses[ExpenseId]` 1 → * `ApprovalDecisions[ExpenseId]`
   - Delete any relationship from `Departments` to `ApprovalDecisions`. It would make a second path between the two tables.
5. **Measures:** on the `Expenses` table, choose *New measure* and add each line:

   ```dax
   Posted = CALCULATE(SUM(Expenses[Amount]), Expenses[Status] = "Posted")
   Awaiting approval = CALCULATE(SUM(Expenses[Amount]), Expenses[Status] = "Submitted")
   Expense count = COUNTROWS(Expenses)
   Avg hours to decision = AVERAGE(Expenses[HoursToDecision])
   Rejection rate = DIVIDE(CALCULATE(COUNTROWS(ApprovalDecisions), ApprovalDecisions[Outcome] = "Rejected"), COUNTROWS(ApprovalDecisions))
   Budget used % = DIVIDE(SUM(BudgetVsActual[Posted]) + SUM(BudgetVsActual[Committed]), SUM(BudgetVsActual[Budget]))
   ```

   Format `Rejection rate` and `Budget used %` as percentages.
6. **Suggested report page:**
   - **Cards:** *Posted*, *Awaiting approval*, *Avg hours to decision*, *Rejection rate*.
   - **Clustered column chart:** `BudgetVsActual[MonthStart]` on the axis, with `Budget` and `Posted` as values.
   - **Stacked bar chart:** `Expenses[Category]` by `Posted`, with `Department` as the legend.
   - **Table:** `Expenses` filtered to `Status = Submitted`, sorted by `ExpenseDate`, with `Requester`, `Department`, `Amount` and `Description`. This is the approval backlog.
   - **Slicers:** `Departments[Department]` and `Expenses[ExpenseMonth]`.
7. **Refresh:** *Home → Refresh* reloads from SQL Server. Run `seed-demo-expenses` again, or use the app, and refresh to see the change.
8. **Save** the report as a `.pbix`. Don't commit it: it contains a copy of the data.

**Sharing:** publishing to the Power BI service (app.powerbi.com) needs a Pro license and a gateway to reach your local SQL Server. Never use *Publish to web*: it makes the data public. To share on your own server, install **Power BI Report Server** (Developer edition, free for development), upload the `.pbix` there, and add the RDL files too.

## Security

- **The app is the access boundary, not these tools.** In the app, every report is limited to the caller's departments. SSRS and Power BI can't sign in this app's Entra or Google users, so neither can apply those rules.
- **The views show every department.** Give `finops_reporting` only to a report server's service account, or use it yourself on your machine. Never give it to end users.
- **RDL reports deny by default.** A report run directly returns no rows unless `DepartmentIds` is set. Only a trusted caller, or you locally, should set it to null.
- **Real deployment:** before giving end users direct access to reports, add SQL Server row-level security keyed on the user, or keep reports behind the API. [reports/SSRS/README.md](../../reports/SSRS/README.md) covers this.
- **No secrets in the repo:** `FinOps.rds` and `FinOps.pbids` hold only the server and database name. Credentials live in the report server or Power BI's credential store.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| *The certificate chain was issued by an authority that is not trusted* (Power BI) | The Docker SQL Server uses a self-signed certificate. When Power BI offers to connect **without encryption**, accept. That's fine on your own machine only. Or go to *File → Options → Data source settings → Edit permissions* and untick *Encrypt connections* |
| Report server database wizard fails on the certificate | Use `localhost,14333` and, in the wizard's connection options, trust the server certificate. Or install the report server catalog on a local SQL Server Express instead |
| `Login failed for user 'finops_reporting'` | Run `grant-reporting-access` again: it resets the password. Check SQL Server is running on port 14333 |
| `The SELECT permission was denied` on a table | Expected: the login reads only `reporting.*` views. Load the views, not the `finance` tables |
| SSRS report is empty | Expected without `DepartmentIds:isnull=true`; see SSRS step 6. Also check the date range: the seeded history covers the last six months |
| Power BI shows no `reporting` views | Run `dotnet ef database update`: the views come with the `ReportingViews` migration |

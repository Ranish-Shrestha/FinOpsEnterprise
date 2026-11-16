using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOps.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A <c>reporting</c> schema of flat, read-only views for BI tools such as Power BI (see
    /// docs/operations/reporting-tools.md). No model change: the app never reads them.
    /// <list type="bullet">
    /// <item>Names and amounts only: no email addresses, object IDs or audit metadata.</item>
    /// <item>Not department-scoped. A login that can read them sees every department, so they are granted only to the
    /// reporting login (<c>grant-reporting-access</c>), never to the app or to end users.</item>
    /// <item>Money is filtered to USD, like the reporting procedures.</item>
    /// </list>
    /// Changing a view later means a new migration with a new definition; never edit this one.
    /// </summary>
    public partial class ReportingViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF SCHEMA_ID(N'reporting') IS NULL EXEC (N'CREATE SCHEMA reporting AUTHORIZATION dbo;');");
            migrationBuilder.Sql(DepartmentsView);
            migrationBuilder.Sql(ExpensesView);
            migrationBuilder.Sql(ApprovalDecisionsView);
            migrationBuilder.Sql(BudgetVsActualView);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS reporting.BudgetVsActual;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS reporting.ApprovalDecisions;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS reporting.Expenses;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS reporting.Departments;");
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS reporting;");
        }

        private const string DepartmentsView = """
            CREATE OR ALTER VIEW reporting.Departments
            AS
            SELECT d.Id AS DepartmentId, d.Name AS Department
            FROM organization.Departments AS d;
            """;

        // One row per expense, every status. Durations are in hours or days so a BI tool can average them directly.
        private const string ExpensesView = """
            CREATE OR ALTER VIEW reporting.Expenses
            AS
            SELECT e.Id AS ExpenseId,
                   e.DepartmentId,
                   d.Name AS Department,
                   u.DisplayName AS Requester,
                   e.Category,
                   e.Description,
                   CAST(e.Amount AS decimal(19, 4)) AS Amount,
                   e.Currency,
                   e.Status,
                   e.ExpenseDate,
                   DATEFROMPARTS(YEAR(e.ExpenseDate), MONTH(e.ExpenseDate), 1) AS ExpenseMonth,
                   e.CreatedAtUtc,
                   e.SubmittedAtUtc,
                   e.DecidedAtUtc,
                   e.PostedAtUtc,
                   CAST(e.PostedAtUtc AS date) AS PostedDate,
                   CASE WHEN e.DecidedAtUtc IS NOT NULL AND e.SubmittedAtUtc IS NOT NULL
                        THEN DATEDIFF(hour, e.SubmittedAtUtc, e.DecidedAtUtc) END AS HoursToDecision,
                   CASE WHEN e.PostedAtUtc IS NOT NULL AND e.SubmittedAtUtc IS NOT NULL
                        THEN DATEDIFF(day, e.SubmittedAtUtc, e.PostedAtUtc) END AS DaysSubmittedToPosted
            FROM finance.ExpenseRequests AS e
            JOIN organization.Departments AS d ON d.Id = e.DepartmentId
            JOIN [identity].UserProfiles AS u ON u.Id = e.RequesterId
            WHERE e.Currency = 'USD';
            """;

        private const string ApprovalDecisionsView = """
            CREATE OR ALTER VIEW reporting.ApprovalDecisions
            AS
            SELECT a.Id AS DecisionId,
                   a.ExpenseRequestId AS ExpenseId,
                   e.DepartmentId,
                   u.DisplayName AS Approver,
                   a.Outcome,
                   a.Comment,
                   a.DecidedAtUtc,
                   CAST(a.DecidedAtUtc AS date) AS DecidedDate
            FROM finance.ApprovalDecisions AS a
            JOIN finance.ExpenseRequests AS e ON e.Id = a.ExpenseRequestId
            JOIN [identity].UserProfiles AS u ON u.Id = a.ApproverId;
            """;

        // Budget and spend per department and month (by expense date), including months that have only one of the
        // two. Same buckets as finance.usp_BudgetVsActual: Posted, Approved (committed), Submitted (pending).
        private const string BudgetVsActualView = """
            CREATE OR ALTER VIEW reporting.BudgetVsActual
            AS
            WITH Spend AS (
                SELECT e.DepartmentId,
                       YEAR(e.ExpenseDate) AS [Year],
                       MONTH(e.ExpenseDate) AS [Month],
                       SUM(CASE WHEN e.Status = 'Posted' THEN e.Amount ELSE 0 END) AS Posted,
                       SUM(CASE WHEN e.Status = 'Approved' THEN e.Amount ELSE 0 END) AS Committed,
                       SUM(CASE WHEN e.Status = 'Submitted' THEN e.Amount ELSE 0 END) AS Pending
                FROM finance.ExpenseRequests AS e
                WHERE e.Currency = 'USD' AND e.Status IN ('Submitted', 'Approved', 'Posted')
                GROUP BY e.DepartmentId, YEAR(e.ExpenseDate), MONTH(e.ExpenseDate)
            ), Budget AS (
                SELECT b.DepartmentId, b.[Year], b.[Month], b.Amount
                FROM finance.Budgets AS b
                WHERE b.Currency = 'USD'
            ), Months AS (
                SELECT COALESCE(b.DepartmentId, s.DepartmentId) AS DepartmentId,
                       COALESCE(b.[Year], s.[Year]) AS [Year],
                       COALESCE(b.[Month], s.[Month]) AS [Month],
                       b.Amount AS Budget,
                       COALESCE(s.Posted, 0) AS Posted,
                       COALESCE(s.Committed, 0) AS Committed,
                       COALESCE(s.Pending, 0) AS Pending
                FROM Budget AS b
                FULL OUTER JOIN Spend AS s
                    ON s.DepartmentId = b.DepartmentId AND s.[Year] = b.[Year] AND s.[Month] = b.[Month]
            )
            SELECT m.DepartmentId,
                   d.Name AS Department,
                   m.[Year],
                   m.[Month],
                   DATEFROMPARTS(m.[Year], m.[Month], 1) AS MonthStart,
                   CAST(m.Budget AS decimal(19, 4)) AS Budget,
                   CAST(m.Posted AS decimal(19, 4)) AS Posted,
                   CAST(m.Committed AS decimal(19, 4)) AS Committed,
                   CAST(m.Pending AS decimal(19, 4)) AS Pending,
                   CAST(m.Budget - m.Posted - m.Committed AS decimal(19, 4)) AS Remaining,
                   CAST(CASE WHEN m.Budget > 0 THEN 100.0 * (m.Posted + m.Committed) / m.Budget END AS decimal(9, 2)) AS UtilizationPercent
            FROM Months AS m
            JOIN organization.Departments AS d ON d.Id = m.DepartmentId;
            """;
    }
}

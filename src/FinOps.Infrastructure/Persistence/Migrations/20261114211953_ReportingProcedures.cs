using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace FinOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportingProcedures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_Reporting",
                schema: "finance",
                table: "ExpenseRequests");

            migrationBuilder.InsertData(
                schema: "identity",
                table: "Permissions",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { new Guid("40000000-0000-0000-0000-00000000000d"), "reports.read.department" },
                    { new Guid("40000000-0000-0000-0000-00000000000e"), "reports.read.all" }
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "RolePermissions",
                columns: new[] { "PermissionId", "RoleId" },
                values: new object[,]
                {
                    { new Guid("40000000-0000-0000-0000-00000000000d"), new Guid("30000000-0000-0000-0000-000000000002") },
                    { new Guid("40000000-0000-0000-0000-00000000000e"), new Guid("30000000-0000-0000-0000-000000000003") },
                    { new Guid("40000000-0000-0000-0000-00000000000e"), new Guid("30000000-0000-0000-0000-000000000004") }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_Ledger",
                schema: "finance",
                table: "ExpenseRequests",
                column: "PostedAtUtc",
                filter: "[Status] = 'Posted'")
                .Annotation("SqlServer:Include", new[] { "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_Reporting",
                schema: "finance",
                table: "ExpenseRequests",
                column: "ExpenseDate")
                .Annotation("SqlServer:Include", new[] { "DepartmentId", "Status", "Amount", "Currency", "Category" });

            migrationBuilder.Sql(ExpenseSummaryProcedure);
            migrationBuilder.Sql(ApprovalAgingProcedure);
            migrationBuilder.Sql(TransactionLedgerProcedure);
            migrationBuilder.Sql(AuditActivityProcedure);
        }

        // Reporting procedures (Phase 4). Shared rules, matching finance.usp_BudgetVsActual:
        // - every input is a typed parameter and is validated here too, in case the
        //   procedure is called outside the API (for example from an SSRS report). Codes 50001-50006;
        // - @DepartmentIds is a JSON array resolved by the API from the caller's memberships, or NULL for
        //   callers allowed to see every department. An empty array yields no rows (deny by default);
        // - date ranges are inclusive calendar days, at most 366 days;
        // - row-returning reports return at most @MaxRows + 1 rows so the API can detect truncation.
        // Changing a procedure later means a new migration with a new definition; never edit this one.

        private const string ExpenseSummaryProcedure = """
            CREATE OR ALTER PROCEDURE finance.usp_ExpenseSummary
                @From date,
                @To date,
                @DepartmentIds nvarchar(max) = NULL
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @From IS NULL OR @To IS NULL OR @To < @From OR DATEDIFF(day, @From, @To) > 365
                    THROW 50004, 'Provide a date range of at most 366 days with To on or after From.', 1;
                IF @DepartmentIds IS NOT NULL AND ISJSON(@DepartmentIds) = 0
                    THROW 50003, 'DepartmentIds must be a JSON array of department IDs.', 1;

                SELECT e.DepartmentId,
                       d.Name AS DepartmentName,
                       e.Category,
                       COUNT_BIG(*) AS ExpenseCountBig,
                       SUM(CASE WHEN e.Status = 'Posted' THEN e.Amount ELSE 0 END) AS Posted,
                       SUM(CASE WHEN e.Status = 'Approved' THEN e.Amount ELSE 0 END) AS Approved,
                       SUM(CASE WHEN e.Status = 'Submitted' THEN e.Amount ELSE 0 END) AS Pending
                INTO #Summary
                FROM finance.ExpenseRequests AS e
                JOIN organization.Departments AS d ON d.Id = e.DepartmentId
                WHERE e.ExpenseDate >= @From AND e.ExpenseDate <= @To
                  AND e.Status IN ('Submitted', 'Approved', 'Posted')
                  AND e.Currency = 'USD'
                  AND (@DepartmentIds IS NULL
                       OR e.DepartmentId IN (SELECT TRY_CAST(j.value AS uniqueidentifier) FROM OPENJSON(@DepartmentIds) AS j))
                GROUP BY e.DepartmentId, d.Name, e.Category;

                SELECT DepartmentId, DepartmentName, Category,
                       CAST(ExpenseCountBig AS int) AS ExpenseCount,
                       CAST(Posted AS decimal(19, 4)) AS Posted,
                       CAST(Approved AS decimal(19, 4)) AS Approved,
                       CAST(Pending AS decimal(19, 4)) AS Pending,
                       CAST(Posted + Approved + Pending AS decimal(19, 4)) AS Total
                FROM #Summary
                ORDER BY DepartmentName, Category;
            END
            """;

        private const string ApprovalAgingProcedure = """
            CREATE OR ALTER PROCEDURE finance.usp_ApprovalAging
                @AsOfUtc datetimeoffset,
                @DepartmentIds nvarchar(max) = NULL
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @AsOfUtc IS NULL THROW 50005, 'AsOfUtc is required.', 1;
                IF @DepartmentIds IS NOT NULL AND ISJSON(@DepartmentIds) = 0
                    THROW 50003, 'DepartmentIds must be a JSON array of department IDs.', 1;

                SELECT e.Id AS ExpenseId,
                       e.DepartmentId,
                       d.Name AS DepartmentName,
                       u.DisplayName AS RequesterName,
                       e.Description,
                       CAST(e.Amount AS decimal(19, 4)) AS Amount,
                       e.SubmittedAtUtc,
                       a.AgeDays,
                       CASE WHEN a.AgeDays <= 2 THEN '0-2 days'
                            WHEN a.AgeDays <= 7 THEN '3-7 days'
                            WHEN a.AgeDays <= 14 THEN '8-14 days'
                            ELSE 'Over 14 days' END AS AgeBucket
                FROM finance.ExpenseRequests AS e
                JOIN organization.Departments AS d ON d.Id = e.DepartmentId
                JOIN [identity].UserProfiles AS u ON u.Id = e.RequesterId
                CROSS APPLY (SELECT CASE WHEN e.SubmittedAtUtc > @AsOfUtc THEN 0
                                         ELSE DATEDIFF(day, e.SubmittedAtUtc, @AsOfUtc) END AS AgeDays) AS a
                WHERE e.Status = 'Submitted'
                  AND (@DepartmentIds IS NULL
                       OR e.DepartmentId IN (SELECT TRY_CAST(j.value AS uniqueidentifier) FROM OPENJSON(@DepartmentIds) AS j))
                ORDER BY e.SubmittedAtUtc, e.Id;
            END
            """;

        private const string TransactionLedgerProcedure = """
            CREATE OR ALTER PROCEDURE finance.usp_TransactionLedger
                @From date,
                @To date,
                @DepartmentIds nvarchar(max) = NULL,
                @MaxRows int = 5000
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @From IS NULL OR @To IS NULL OR @To < @From OR DATEDIFF(day, @From, @To) > 365
                    THROW 50004, 'Provide a date range of at most 366 days with To on or after From.', 1;
                IF @DepartmentIds IS NOT NULL AND ISJSON(@DepartmentIds) = 0
                    THROW 50003, 'DepartmentIds must be a JSON array of department IDs.', 1;
                IF @MaxRows IS NULL OR @MaxRows < 1 OR @MaxRows > 50000
                    THROW 50006, 'MaxRows must be between 1 and 50000.', 1;

                DECLARE @FromUtc datetimeoffset = TODATETIMEOFFSET(CAST(@From AS datetime2), 0);
                DECLARE @ToUtc datetimeoffset = TODATETIMEOFFSET(CAST(DATEADD(day, 1, @To) AS datetime2), 0);

                SELECT TOP (@MaxRows + 1)
                       e.PostedAtUtc,
                       e.Id AS ExpenseId,
                       e.DepartmentId,
                       d.Name AS DepartmentName,
                       r.DisplayName AS RequesterName,
                       e.Category,
                       e.Description,
                       CAST(e.Amount AS decimal(19, 4)) AS Amount,
                       e.Currency,
                       p.DisplayName AS PostedByName
                FROM finance.ExpenseRequests AS e
                JOIN organization.Departments AS d ON d.Id = e.DepartmentId
                JOIN [identity].UserProfiles AS r ON r.Id = e.RequesterId
                JOIN [identity].UserProfiles AS p ON p.Id = e.PostedById
                WHERE e.Status = 'Posted'
                  AND e.PostedAtUtc >= @FromUtc AND e.PostedAtUtc < @ToUtc
                  AND (@DepartmentIds IS NULL
                       OR e.DepartmentId IN (SELECT TRY_CAST(j.value AS uniqueidentifier) FROM OPENJSON(@DepartmentIds) AS j))
                ORDER BY e.PostedAtUtc, e.Id;
            END
            """;

        private const string AuditActivityProcedure = """
            CREATE OR ALTER PROCEDURE audit.usp_AuditActivity
                @From date,
                @To date,
                @MaxRows int = 5000
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @From IS NULL OR @To IS NULL OR @To < @From OR DATEDIFF(day, @From, @To) > 365
                    THROW 50004, 'Provide a date range of at most 366 days with To on or after From.', 1;
                IF @MaxRows IS NULL OR @MaxRows < 1 OR @MaxRows > 50000
                    THROW 50006, 'MaxRows must be between 1 and 50000.', 1;

                DECLARE @FromUtc datetimeoffset = TODATETIMEOFFSET(CAST(@From AS datetime2), 0);
                DECLARE @ToUtc datetimeoffset = TODATETIMEOFFSET(CAST(DATEADD(day, 1, @To) AS datetime2), 0);

                SELECT TOP (@MaxRows + 1)
                       a.OccurredAtUtc,
                       u.DisplayName AS ActorName,
                       a.Action,
                       a.ResourceType,
                       a.ResourceId,
                       a.Outcome,
                       a.CorrelationId
                FROM audit.AuditEvents AS a
                LEFT JOIN [identity].UserProfiles AS u ON u.Id = a.ActorUserId
                WHERE a.OccurredAtUtc >= @FromUtc AND a.OccurredAtUtc < @ToUtc
                ORDER BY a.OccurredAtUtc DESC, a.Id DESC;
            END
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS finance.usp_ExpenseSummary;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS finance.usp_ApprovalAging;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS finance.usp_TransactionLedger;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS audit.usp_AuditActivity;");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_Ledger",
                schema: "finance",
                table: "ExpenseRequests");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_Reporting",
                schema: "finance",
                table: "ExpenseRequests");

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("40000000-0000-0000-0000-00000000000d"), new Guid("30000000-0000-0000-0000-000000000002") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("40000000-0000-0000-0000-00000000000e"), new Guid("30000000-0000-0000-0000-000000000003") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("40000000-0000-0000-0000-00000000000e"), new Guid("30000000-0000-0000-0000-000000000004") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000d"));

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000e"));

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_Reporting",
                schema: "finance",
                table: "ExpenseRequests",
                column: "ExpenseDate")
                .Annotation("SqlServer:Include", new[] { "DepartmentId", "Status", "Amount", "Currency" });
        }
    }
}

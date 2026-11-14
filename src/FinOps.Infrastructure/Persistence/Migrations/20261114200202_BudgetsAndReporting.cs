using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace FinOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BudgetsAndReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Budgets",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Budgets", x => x.Id);
                    table.CheckConstraint("CK_Budgets_Amount_Positive", "[Amount] > 0");
                    table.CheckConstraint("CK_Budgets_Currency_Iso", "LEN([Currency]) = 3");
                    table.CheckConstraint("CK_Budgets_Month", "[Month] BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "FK_Budgets_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "organization",
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "Permissions",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { new Guid("40000000-0000-0000-0000-00000000000a"), "budgets.read.department" },
                    { new Guid("40000000-0000-0000-0000-00000000000b"), "budgets.read.all" },
                    { new Guid("40000000-0000-0000-0000-00000000000c"), "budgets.manage" }
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "RolePermissions",
                columns: new[] { "PermissionId", "RoleId" },
                values: new object[,]
                {
                    { new Guid("40000000-0000-0000-0000-00000000000a"), new Guid("30000000-0000-0000-0000-000000000002") },
                    { new Guid("40000000-0000-0000-0000-00000000000b"), new Guid("30000000-0000-0000-0000-000000000003") },
                    { new Guid("40000000-0000-0000-0000-00000000000b"), new Guid("30000000-0000-0000-0000-000000000004") },
                    { new Guid("40000000-0000-0000-0000-00000000000c"), new Guid("30000000-0000-0000-0000-000000000004") }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_Reporting",
                schema: "finance",
                table: "ExpenseRequests",
                column: "ExpenseDate")
                .Annotation("SqlServer:Include", new[] { "DepartmentId", "Status", "Amount", "Currency" });

            migrationBuilder.CreateIndex(
                name: "UX_Budgets_Department_Period",
                schema: "finance",
                table: "Budgets",
                columns: new[] { "DepartmentId", "Year", "Month" },
                unique: true);

            migrationBuilder.Sql(BudgetVsActualProcedure);
        }

        /// <summary>
        /// Budget vs. actual per department and month. All inputs are typed parameters; the department scope
        /// is a JSON array resolved by the API from the caller's memberships (NULL = all, for budgets.read.all).
        /// Actuals are by expense date: Posted is spent, Approved is committed, Submitted is pending
        /// (shown, not subtracted). Remaining = Budget - Posted - Committed.
        /// </summary>
        private const string BudgetVsActualProcedure = """
            CREATE OR ALTER PROCEDURE finance.usp_BudgetVsActual
                @Year int,
                @Month int = NULL,
                @DepartmentIds nvarchar(max) = NULL
            AS
            BEGIN
                SET NOCOUNT ON;

                IF @Year IS NULL OR @Year < 2000 OR @Year > 2100
                    THROW 50001, 'Year must be between 2000 and 2100.', 1;
                IF @Month IS NOT NULL AND (@Month < 1 OR @Month > 12)
                    THROW 50002, 'Month must be between 1 and 12.', 1;
                IF @DepartmentIds IS NOT NULL AND ISJSON(@DepartmentIds) = 0
                    THROW 50003, 'DepartmentIds must be a JSON array of department IDs.', 1;

                DECLARE @From date = DATEFROMPARTS(@Year, COALESCE(@Month, 1), 1);
                DECLARE @To date = CASE WHEN @Month IS NULL THEN DATEADD(year, 1, @From) ELSE DATEADD(month, 1, @From) END;

                CREATE TABLE #Scope (Id uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
                INSERT INTO #Scope (Id, Name)
                SELECT d.Id, d.Name
                FROM organization.Departments AS d
                WHERE @DepartmentIds IS NULL
                   OR d.Id IN (SELECT TRY_CAST(j.value AS uniqueidentifier) FROM OPENJSON(@DepartmentIds) AS j);

                -- Aggregate expenses once (a CTE referenced twice would be evaluated twice), already limited
                -- to the caller's departments. Served by IX_ExpenseRequests_Reporting (ExpenseDate, covering).
                CREATE TABLE #Spend (
                    DepartmentId uniqueidentifier NOT NULL, Month int NOT NULL,
                    Posted decimal(19, 4) NOT NULL, Committed decimal(19, 4) NOT NULL, Pending decimal(19, 4) NOT NULL,
                    PRIMARY KEY (DepartmentId, Month));
                INSERT INTO #Spend (DepartmentId, Month, Posted, Committed, Pending)
                SELECT e.DepartmentId,
                       MONTH(e.ExpenseDate),
                       SUM(CASE WHEN e.Status = 'Posted' THEN e.Amount ELSE 0 END),
                       SUM(CASE WHEN e.Status = 'Approved' THEN e.Amount ELSE 0 END),
                       SUM(CASE WHEN e.Status = 'Submitted' THEN e.Amount ELSE 0 END)
                FROM finance.ExpenseRequests AS e
                JOIN #Scope AS s ON s.Id = e.DepartmentId
                WHERE e.ExpenseDate >= @From AND e.ExpenseDate < @To
                  AND e.Status IN ('Submitted', 'Approved', 'Posted')
                  AND e.Currency = 'USD'
                GROUP BY e.DepartmentId, MONTH(e.ExpenseDate);

                WITH Budgeted AS (
                    SELECT b.DepartmentId, b.Month, b.Amount
                    FROM finance.Budgets AS b
                    WHERE b.Year = @Year AND (@Month IS NULL OR b.Month = @Month) AND b.Currency = 'USD'
                ),
                Periods AS (
                    SELECT DepartmentId, Month FROM Budgeted
                    UNION
                    SELECT DepartmentId, Month FROM #Spend
                )
                SELECT s.Id AS DepartmentId,
                       s.Name AS DepartmentName,
                       @Year AS [Year],
                       p.Month AS [Month],
                       CAST(b.Amount AS decimal(19, 4)) AS Budget,
                       CAST(COALESCE(sp.Posted, 0) AS decimal(19, 4)) AS Posted,
                       CAST(COALESCE(sp.Committed, 0) AS decimal(19, 4)) AS Committed,
                       CAST(COALESCE(sp.Pending, 0) AS decimal(19, 4)) AS Pending,
                       CAST(b.Amount - COALESCE(sp.Posted, 0) - COALESCE(sp.Committed, 0) AS decimal(19, 4)) AS Remaining,
                       CAST(CASE WHEN b.Amount > 0
                                 THEN ROUND((COALESCE(sp.Posted, 0) + COALESCE(sp.Committed, 0)) * 100.0 / b.Amount, 1)
                            END AS decimal(9, 1)) AS UtilizationPercent
                FROM Periods AS p
                JOIN #Scope AS s ON s.Id = p.DepartmentId
                LEFT JOIN Budgeted AS b ON b.DepartmentId = p.DepartmentId AND b.Month = p.Month
                LEFT JOIN #Spend AS sp ON sp.DepartmentId = p.DepartmentId AND sp.Month = p.Month
                ORDER BY s.Name, p.Month;
            END
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS finance.usp_BudgetVsActual;");

            migrationBuilder.DropTable(
                name: "Budgets",
                schema: "finance");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_Reporting",
                schema: "finance",
                table: "ExpenseRequests");

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("40000000-0000-0000-0000-00000000000a"), new Guid("30000000-0000-0000-0000-000000000002") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("40000000-0000-0000-0000-00000000000b"), new Guid("30000000-0000-0000-0000-000000000003") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("40000000-0000-0000-0000-00000000000b"), new Guid("30000000-0000-0000-0000-000000000004") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("40000000-0000-0000-0000-00000000000c"), new Guid("30000000-0000-0000-0000-000000000004") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000a"));

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000b"));

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000c"));
        }
    }
}

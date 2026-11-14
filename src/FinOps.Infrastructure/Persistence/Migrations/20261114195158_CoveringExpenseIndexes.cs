using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CoveringExpenseIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_ApprovalQueue",
                schema: "finance",
                table: "ExpenseRequests");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_Requester",
                schema: "finance",
                table: "ExpenseRequests");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_ApprovalQueue",
                schema: "finance",
                table: "ExpenseRequests",
                columns: new[] { "DepartmentId", "Status", "SubmittedAtUtc" })
                .Annotation("SqlServer:Include", new[] { "RequesterId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_Requester",
                schema: "finance",
                table: "ExpenseRequests",
                columns: new[] { "RequesterId", "CreatedAtUtc" })
                .Annotation("SqlServer:Include", new[] { "Status", "DepartmentId", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_Status",
                schema: "finance",
                table: "ExpenseRequests",
                columns: new[] { "Status", "CreatedAtUtc" })
                .Annotation("SqlServer:Include", new[] { "DepartmentId", "RequesterId", "SubmittedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_ApprovalQueue",
                schema: "finance",
                table: "ExpenseRequests");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_Requester",
                schema: "finance",
                table: "ExpenseRequests");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseRequests_Status",
                schema: "finance",
                table: "ExpenseRequests");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_ApprovalQueue",
                schema: "finance",
                table: "ExpenseRequests",
                columns: new[] { "DepartmentId", "Status", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseRequests_Requester",
                schema: "finance",
                table: "ExpenseRequests",
                columns: new[] { "RequesterId", "CreatedAtUtc" });
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MessagingOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "messaging");

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpenseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_ExpenseRequests_ExpenseRequestId",
                        column: x => x.ExpenseRequestId,
                        principalSchema: "finance",
                        principalTable: "ExpenseRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notifications_UserProfiles_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeliveryCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeadLetteredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_OutboxMessages_SingleOutcome", "[ProcessedAtUtc] IS NULL OR [DeadLetteredAtUtc] IS NULL");
                });

            migrationBuilder.CreateTable(
                name: "ProcessedMessages",
                schema: "messaging",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Handler = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedMessages", x => new { x.MessageId, x.Handler });
                });

            migrationBuilder.CreateTable(
                name: "Reimbursements",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpenseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    ScheduledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reimbursements", x => x.Id);
                    table.CheckConstraint("CK_Reimbursements_Amount_Positive", "[Amount] > 0");
                    table.CheckConstraint("CK_Reimbursements_Currency_Iso", "LEN([Currency]) = 3");
                    table.ForeignKey(
                        name: "FK_Reimbursements_ExpenseRequests_ExpenseRequestId",
                        column: x => x.ExpenseRequestId,
                        principalSchema: "finance",
                        principalTable: "ExpenseRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reimbursements_UserProfiles_PayeeId",
                        column: x => x.PayeeId,
                        principalSchema: "identity",
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "Permissions",
                columns: new[] { "Id", "Name" },
                values: new object[] { new Guid("40000000-0000-0000-0000-00000000000f"), "messaging.manage" });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "RolePermissions",
                columns: new[] { "PermissionId", "RoleId" },
                values: new object[] { new Guid("40000000-0000-0000-0000-00000000000f"), new Guid("30000000-0000-0000-0000-000000000003") });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ExpenseRequestId",
                schema: "messaging",
                table: "Notifications",
                column: "ExpenseRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Inbox",
                schema: "messaging",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAtUtc" })
                .Annotation("SqlServer:Include", new[] { "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_Notifications_Source_User",
                schema: "messaging",
                table: "Notifications",
                columns: new[] { "SourceMessageId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_DeadLettered",
                schema: "messaging",
                table: "OutboxMessages",
                column: "DeadLetteredAtUtc",
                filter: "[DeadLetteredAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                schema: "messaging",
                table: "OutboxMessages",
                column: "NextAttemptAtUtc",
                filter: "[ProcessedAtUtc] IS NULL AND [DeadLetteredAtUtc] IS NULL")
                .Annotation("SqlServer:Include", new[] { "LockedUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Reimbursements_PayeeId",
                schema: "finance",
                table: "Reimbursements",
                column: "PayeeId");

            migrationBuilder.CreateIndex(
                name: "UX_Reimbursements_Expense",
                schema: "finance",
                table: "Reimbursements",
                column: "ExpenseRequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "ProcessedMessages",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "Reimbursements",
                schema: "finance");

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "RolePermissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("40000000-0000-0000-0000-00000000000f"), new Guid("30000000-0000-0000-0000-000000000003") });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Permissions",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000f"));
        }
    }
}

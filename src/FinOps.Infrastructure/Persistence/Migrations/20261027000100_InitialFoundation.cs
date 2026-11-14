using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOps.Infrastructure.Persistence.Migrations;

public partial class InitialFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "organization");
        migrationBuilder.CreateTable(
            name: "Departments",
            schema: "organization",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Departments", x => x.Id);
                table.CheckConstraint("CK_Departments_Name_NotBlank", "LEN(LTRIM(RTRIM([Name]))) > 0");
            });
        migrationBuilder.InsertData(
            schema: "organization", table: "Departments", columns: new[] { "Id", "Name" },
            values: new object[,]
            {
                { new Guid("11111111-1111-1111-1111-111111111111"), "Demo Engineering" },
                { new Guid("22222222-2222-2222-2222-222222222222"), "Demo Finance" },
                { new Guid("33333333-3333-3333-3333-333333333333"), "Demo Operations" }
            });
        migrationBuilder.CreateIndex(
            name: "IX_Departments_Name", schema: "organization", table: "Departments", column: "Name", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "Departments", schema: "organization");
}

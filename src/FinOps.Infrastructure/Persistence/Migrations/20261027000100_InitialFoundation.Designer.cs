using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FinOpsDbContext))]
[Migration("20261027000100_InitialFoundation")]
partial class InitialFoundation
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.14")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);
        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);
        modelBuilder.Entity("FinOps.Domain.Organization.Department", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            b.Property<string>("Name").IsRequired().HasMaxLength(100).HasColumnType("nvarchar(100)");
            b.HasKey("Id");
            b.HasIndex("Name").IsUnique();
            b.ToTable("Departments", "organization", t =>
                t.HasCheckConstraint("CK_Departments_Name_NotBlank", "LEN(LTRIM(RTRIM([Name]))) > 0"));
            b.HasData(
                new { Id = new Guid("11111111-1111-1111-1111-111111111111"), Name = "Demo Engineering" },
                new { Id = new Guid("22222222-2222-2222-2222-222222222222"), Name = "Demo Finance" },
                new { Id = new Guid("33333333-3333-3333-3333-333333333333"), Name = "Demo Operations" });
        });
    }
}

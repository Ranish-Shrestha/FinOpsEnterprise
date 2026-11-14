using FinOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOps.Infrastructure.Persistence;

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("Departments", "organization", table =>
            table.HasCheckConstraint("CK_Departments_Name_NotBlank", "LEN(LTRIM(RTRIM([Name]))) > 0"));
        builder.HasKey(department => department.Id);
        builder.Property(department => department.Id).ValueGeneratedNever();
        builder.Property(department => department.Name).HasMaxLength(Department.MaximumNameLength).IsRequired();
        builder.HasIndex(department => department.Name).IsUnique();
        // Fixed IDs keep migration seed data deterministic. All names are synthetic.
        builder.HasData(
            new { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Demo Engineering" },
            new { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Demo Finance" },
            new { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "Demo Operations" });
    }
}

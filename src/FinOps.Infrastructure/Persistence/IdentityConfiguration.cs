using FinOps.Application.Identity;
using FinOps.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOps.Infrastructure.Persistence;

/// <summary>
/// Fixed IDs for authorization reference data (roles, permissions, role-permission grants). This data is
/// part of the schema contract and ships in migrations. Demo users are NOT reference data; see DemoDataSeeder.
/// </summary>
internal static class SyntheticIdentityIds
{
    internal static readonly Guid EmployeeRole = Guid.Parse("30000000-0000-0000-0000-000000000001");
    internal static readonly Guid ManagerRole = Guid.Parse("30000000-0000-0000-0000-000000000002");
    internal static readonly Guid AdminRole = Guid.Parse("30000000-0000-0000-0000-000000000003");
    internal static readonly Guid FinanceOfficerRole = Guid.Parse("30000000-0000-0000-0000-000000000004");

    /// <summary>Permission name → fixed ID. Never renumber: IDs are referenced by applied migrations.</summary>
    internal static readonly IReadOnlyDictionary<string, Guid> Permissions = new Dictionary<string, Guid>(StringComparer.Ordinal)
    {
        [ApplicationPermissions.DepartmentsRead] = Guid.Parse("40000000-0000-0000-0000-000000000001"),
        [ApplicationPermissions.DepartmentsReadAll] = Guid.Parse("40000000-0000-0000-0000-000000000002"),
        [ApplicationPermissions.UserRolesManage] = Guid.Parse("40000000-0000-0000-0000-000000000003"),
        [ApplicationPermissions.ExpensesCreate] = Guid.Parse("40000000-0000-0000-0000-000000000004"),
        [ApplicationPermissions.ExpensesReadDepartment] = Guid.Parse("40000000-0000-0000-0000-000000000005"),
        [ApplicationPermissions.ExpensesReadAll] = Guid.Parse("40000000-0000-0000-0000-000000000006"),
        [ApplicationPermissions.ExpensesApprove] = Guid.Parse("40000000-0000-0000-0000-000000000007"),
        [ApplicationPermissions.ExpensesPost] = Guid.Parse("40000000-0000-0000-0000-000000000008"),
        [ApplicationPermissions.AuditRead] = Guid.Parse("40000000-0000-0000-0000-000000000009"),
        [ApplicationPermissions.BudgetsReadDepartment] = Guid.Parse("40000000-0000-0000-0000-00000000000a"),
        [ApplicationPermissions.BudgetsReadAll] = Guid.Parse("40000000-0000-0000-0000-00000000000b"),
        [ApplicationPermissions.BudgetsManage] = Guid.Parse("40000000-0000-0000-0000-00000000000c"),
        [ApplicationPermissions.ReportsReadDepartment] = Guid.Parse("40000000-0000-0000-0000-00000000000d"),
        [ApplicationPermissions.ReportsReadAll] = Guid.Parse("40000000-0000-0000-0000-00000000000e"),
    };

    /// <summary>
    /// Role → permissions. Separation of duties: managers approve, finance posts, administrators manage
    /// access and read audit but never approve or post money.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Guid, string[]> Grants = new Dictionary<Guid, string[]>
    {
        [EmployeeRole] =
        [
            ApplicationPermissions.DepartmentsRead, ApplicationPermissions.ExpensesCreate
        ],
        [ManagerRole] =
        [
            ApplicationPermissions.DepartmentsRead, ApplicationPermissions.ExpensesCreate,
            ApplicationPermissions.ExpensesReadDepartment, ApplicationPermissions.ExpensesApprove,
            ApplicationPermissions.BudgetsReadDepartment, ApplicationPermissions.ReportsReadDepartment
        ],
        [FinanceOfficerRole] =
        [
            ApplicationPermissions.DepartmentsRead, ApplicationPermissions.DepartmentsReadAll,
            ApplicationPermissions.ExpensesCreate, ApplicationPermissions.ExpensesReadAll, ApplicationPermissions.ExpensesPost,
            ApplicationPermissions.BudgetsReadAll, ApplicationPermissions.BudgetsManage, ApplicationPermissions.ReportsReadAll
        ],
        [AdminRole] =
        [
            ApplicationPermissions.DepartmentsRead, ApplicationPermissions.DepartmentsReadAll,
            ApplicationPermissions.UserRolesManage, ApplicationPermissions.ExpensesReadAll, ApplicationPermissions.AuditRead,
            ApplicationPermissions.BudgetsReadAll, ApplicationPermissions.ReportsReadAll
        ],
    };
}

internal sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("UserProfiles", "identity");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.DisplayName).HasMaxLength(UserProfile.MaximumDisplayNameLength).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(UserProfile.MaximumEmailLength).IsRequired();
        builder.HasIndex(x => x.EntraObjectId).IsUnique();
        builder.HasIndex(x => x.Email).IsUnique();
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles", "identity");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(Role.MaximumNameLength).IsRequired();
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasData(
            new { Id = SyntheticIdentityIds.EmployeeRole, Name = "Employee" },
            new { Id = SyntheticIdentityIds.ManagerRole, Name = "Manager" },
            new { Id = SyntheticIdentityIds.AdminRole, Name = "Administrator" },
            new { Id = SyntheticIdentityIds.FinanceOfficerRole, Name = "FinanceOfficer" });
    }
}

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions", "identity");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(Permission.MaximumNameLength).IsRequired();
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasData(SyntheticIdentityIds.Permissions.Select(p => new { Id = p.Value, Name = p.Key }).ToArray());
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("UserRoles", "identity");
        builder.HasKey(x => new { x.UserId, x.RoleId });
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions", "identity");
        builder.HasKey(x => new { x.RoleId, x.PermissionId });
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasData(SyntheticIdentityIds.Grants
            .SelectMany(grant => grant.Value.Select(name => new { RoleId = grant.Key, PermissionId = SyntheticIdentityIds.Permissions[name] }))
            .ToArray());
    }
}

internal sealed class DepartmentMembershipConfiguration : IEntityTypeConfiguration<DepartmentMembership>
{
    public void Configure(EntityTypeBuilder<DepartmentMembership> builder)
    {
        builder.ToTable("DepartmentMemberships", "identity", table => table.HasCheckConstraint(
            "CK_DepartmentMemberships_Dates", "[EndsAtUtc] IS NULL OR [EndsAtUtc] > [StartsAtUtc]"));
        builder.HasKey(x => new { x.UserId, x.DepartmentId, x.StartsAtUtc });
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<FinOps.Domain.Organization.Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.StartsAtUtc, x.EndsAtUtc });
    }
}

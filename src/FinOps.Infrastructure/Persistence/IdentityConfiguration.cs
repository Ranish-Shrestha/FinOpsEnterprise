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
    internal static readonly Guid DepartmentsRead = Guid.Parse("40000000-0000-0000-0000-000000000001");
    internal static readonly Guid DepartmentsReadAll = Guid.Parse("40000000-0000-0000-0000-000000000002");
    internal static readonly Guid RolesManage = Guid.Parse("40000000-0000-0000-0000-000000000003");
}

internal sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("UserProfiles", "identity");
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.DisplayName).HasMaxLength(UserProfile.MaximumDisplayNameLength).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(UserProfile.MaximumEmailLength).IsRequired();
        builder.HasIndex(x => x.EntraObjectId).IsUnique(); builder.HasIndex(x => x.Email).IsUnique();
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles", "identity"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(Role.MaximumNameLength).IsRequired(); builder.HasIndex(x => x.Name).IsUnique();
        builder.HasData(new { Id = SyntheticIdentityIds.EmployeeRole, Name = "Employee" },
            new { Id = SyntheticIdentityIds.ManagerRole, Name = "Manager" }, new { Id = SyntheticIdentityIds.AdminRole, Name = "Administrator" });
    }
}

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions", "identity"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(Permission.MaximumNameLength).IsRequired(); builder.HasIndex(x => x.Name).IsUnique();
        builder.HasData(new { Id = SyntheticIdentityIds.DepartmentsRead, Name = "departments.read" },
            new { Id = SyntheticIdentityIds.DepartmentsReadAll, Name = "departments.read.all" },
            new { Id = SyntheticIdentityIds.RolesManage, Name = "user-roles.manage" });
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("UserRoles", "identity"); builder.HasKey(x => new { x.UserId, x.RoleId });
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions", "identity"); builder.HasKey(x => new { x.RoleId, x.PermissionId });
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasData(
            new { RoleId = SyntheticIdentityIds.EmployeeRole, PermissionId = SyntheticIdentityIds.DepartmentsRead },
            new { RoleId = SyntheticIdentityIds.ManagerRole, PermissionId = SyntheticIdentityIds.DepartmentsRead },
            new { RoleId = SyntheticIdentityIds.AdminRole, PermissionId = SyntheticIdentityIds.DepartmentsRead },
            new { RoleId = SyntheticIdentityIds.AdminRole, PermissionId = SyntheticIdentityIds.DepartmentsReadAll },
            new { RoleId = SyntheticIdentityIds.AdminRole, PermissionId = SyntheticIdentityIds.RolesManage });
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

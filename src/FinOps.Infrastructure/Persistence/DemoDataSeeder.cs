using FinOps.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Persistence;

/// <summary>A synthetic persona used by Development mock authentication and database tests.</summary>
public sealed record DemoPersona(
    Guid UserId, Guid EntraObjectId, string DisplayName, string Email, bool IsActive, string Role);

/// <summary>
/// Inserts synthetic demo users, role assignments and memberships. This is deliberately NOT migration
/// seed data: migrations run in every environment, demo identities must exist only where a developer
/// explicitly asks for them. The seeder is idempotent and only inserts missing rows.
/// </summary>
public static class DemoDataSeeder
{
    public static readonly Guid EngineeringDepartment = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid FinanceDepartment = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid OperationsDepartment = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly DemoPersona Employee = new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"), Guid.Parse("20000000-0000-0000-0000-000000000001"),
        "Demo Employee", "employee@example.invalid", true, "Employee");

    public static readonly DemoPersona Manager = new(
        Guid.Parse("10000000-0000-0000-0000-000000000002"), Guid.Parse("20000000-0000-0000-0000-000000000002"),
        "Demo Manager", "manager@example.invalid", true, "Manager");

    public static readonly DemoPersona Administrator = new(
        Guid.Parse("10000000-0000-0000-0000-000000000003"), Guid.Parse("20000000-0000-0000-0000-000000000003"),
        "Demo Administrator", "admin@example.invalid", true, "Administrator");

    public static readonly DemoPersona FormerEmployee = new(
        Guid.Parse("10000000-0000-0000-0000-000000000004"), Guid.Parse("20000000-0000-0000-0000-000000000004"),
        "Demo Former Employee", "former@example.invalid", false, "Employee");

    public static IReadOnlyList<DemoPersona> Personas { get; } = [Employee, Manager, Administrator, FormerEmployee];

    private static readonly DateTimeOffset DemoStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, Guid> RoleIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
    {
        ["Employee"] = SyntheticIdentityIds.EmployeeRole,
        ["Manager"] = SyntheticIdentityIds.ManagerRole,
        ["Administrator"] = SyntheticIdentityIds.AdminRole
    };

    private static IEnumerable<DepartmentMembership> Memberships() =>
    [
        DepartmentMembership.Create(Employee.UserId, EngineeringDepartment, DemoStart),
        DepartmentMembership.Create(Manager.UserId, FinanceDepartment, DemoStart),
        // Expired: proves that ended memberships grant no access.
        DepartmentMembership.Create(Manager.UserId, OperationsDepartment, DemoStart, DemoStart.AddMonths(2)),
        // Active membership, but the user is inactive: proves deactivation wins.
        DepartmentMembership.Create(FormerEmployee.UserId, EngineeringDepartment, DemoStart)
    ];

    /// <returns>The number of rows inserted.</returns>
    public static async Task<int> SeedAsync(FinOpsDbContext db, CancellationToken cancellationToken = default)
    {
        foreach (var persona in Personas)
        {
            if (!await db.UserProfiles.AnyAsync(user => user.Id == persona.UserId, cancellationToken))
            {
                db.UserProfiles.Add(UserProfile.Provision(
                    persona.UserId, persona.EntraObjectId, persona.DisplayName, persona.Email, persona.IsActive));
            }

            var roleId = RoleIds[persona.Role];
            if (!await db.UserRoles.AnyAsync(x => x.UserId == persona.UserId && x.RoleId == roleId, cancellationToken))
            {
                db.UserRoles.Add(UserRole.Assign(persona.UserId, roleId));
            }
        }

        foreach (var membership in Memberships())
        {
            var exists = await db.DepartmentMemberships.AnyAsync(x => x.UserId == membership.UserId
                && x.DepartmentId == membership.DepartmentId && x.StartsAtUtc == membership.StartsAtUtc, cancellationToken);
            if (!exists) db.DepartmentMemberships.Add(membership);
        }

        return await db.SaveChangesAsync(cancellationToken);
    }
}

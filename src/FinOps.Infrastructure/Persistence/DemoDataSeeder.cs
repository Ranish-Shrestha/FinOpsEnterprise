using FinOps.Domain.Expenses;
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

    public static readonly DemoPersona FinanceOfficer = new(
        Guid.Parse("10000000-0000-0000-0000-000000000005"), Guid.Parse("20000000-0000-0000-0000-000000000005"),
        "Demo Finance Officer", "finance@example.invalid", true, "FinanceOfficer");

    public static readonly DemoPersona EngineeringManager = new(
        Guid.Parse("10000000-0000-0000-0000-000000000006"), Guid.Parse("20000000-0000-0000-0000-000000000006"),
        "Demo Engineering Manager", "eng-manager@example.invalid", true, "Manager");

    public static IReadOnlyList<DemoPersona> Personas { get; } =
        [Employee, Manager, Administrator, FormerEmployee, FinanceOfficer, EngineeringManager];

    /// <summary>Fixed IDs of the sample expenses, one per interesting workflow state.</summary>
    public static readonly Guid DraftExpense = Guid.Parse("50000000-0000-0000-0000-000000000001");
    public static readonly Guid SubmittedExpense = Guid.Parse("50000000-0000-0000-0000-000000000002");
    public static readonly Guid ApprovedExpense = Guid.Parse("50000000-0000-0000-0000-000000000003");
    public static readonly Guid ManagersOwnExpense = Guid.Parse("50000000-0000-0000-0000-000000000004");

    private static readonly DateTimeOffset DemoStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, Guid> RoleIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
    {
        ["Employee"] = SyntheticIdentityIds.EmployeeRole,
        ["Manager"] = SyntheticIdentityIds.ManagerRole,
        ["Administrator"] = SyntheticIdentityIds.AdminRole,
        ["FinanceOfficer"] = SyntheticIdentityIds.FinanceOfficerRole
    };

    private static IEnumerable<DepartmentMembership> Memberships() =>
    [
        DepartmentMembership.Create(Employee.UserId, EngineeringDepartment, DemoStart),
        DepartmentMembership.Create(Manager.UserId, FinanceDepartment, DemoStart),
        // Expired: proves that ended memberships grant no access.
        DepartmentMembership.Create(Manager.UserId, OperationsDepartment, DemoStart, DemoStart.AddMonths(2)),
        // Active membership, but the user is inactive: proves deactivation wins.
        DepartmentMembership.Create(FormerEmployee.UserId, EngineeringDepartment, DemoStart),
        DepartmentMembership.Create(FinanceOfficer.UserId, FinanceDepartment, DemoStart),
        DepartmentMembership.Create(EngineeringManager.UserId, EngineeringDepartment, DemoStart)
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

        var inserted = await db.SaveChangesAsync(cancellationToken);
        return inserted + await SeedExpensesAsync(db, cancellationToken);
    }

    /// <summary>
    /// Sample expenses created through the domain methods, so they obey the same rules as real ones.
    /// Dates are relative to today because expenses older than a year cannot be claimed.
    /// </summary>
    private static async Task<int> SeedExpensesAsync(FinOpsDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        async Task AddIfMissing(Guid id, Func<ExpenseRequest> create, Func<ExpenseRequest, ApprovalDecision?>? advance = null)
        {
            if (await db.ExpenseRequests.AnyAsync(x => x.Id == id, cancellationToken)) return;
            var expense = create();
            db.ExpenseRequests.Add(expense);
            if (advance?.Invoke(expense) is { } decision) db.ApprovalDecisions.Add(decision);
        }

        await AddIfMissing(DraftExpense, () => ExpenseRequest.CreateDraft(DraftExpense, Employee.UserId, EngineeringDepartment,
            245.80m, "USD", ExpenseCategory.Travel, "Train tickets to the client workshop", today.AddDays(-6), now));

        await AddIfMissing(SubmittedExpense, () => ExpenseRequest.CreateDraft(SubmittedExpense, Employee.UserId, EngineeringDepartment,
            49.00m, "USD", ExpenseCategory.Software, "Diagramming tool subscription (one month)", today.AddDays(-3), now),
            e => { e.Submit(now); return null; });

        await AddIfMissing(ApprovedExpense, () => ExpenseRequest.CreateDraft(ApprovedExpense, Employee.UserId, EngineeringDepartment,
            320.00m, "USD", ExpenseCategory.Training, "Online secure coding course", today.AddDays(-14), now),
            e => { e.Submit(now); return e.Approve(EngineeringManager.UserId, "Approved: relevant to the team's security goals.", now); });

        await AddIfMissing(ManagersOwnExpense, () => ExpenseRequest.CreateDraft(ManagersOwnExpense, EngineeringManager.UserId, EngineeringDepartment,
            86.40m, "USD", ExpenseCategory.Meals, "Team lunch after the release", today.AddDays(-2), now),
            e => { e.Submit(now); return null; });

        return await db.SaveChangesAsync(cancellationToken);
    }
}

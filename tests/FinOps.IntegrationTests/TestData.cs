using FinOps.Domain.Budgets;
using FinOps.Domain.Expenses;
using FinOps.Domain.Organization;
using FinOps.Infrastructure.Persistence;

namespace FinOps.IntegrationTests;

/// <summary>
/// Known datasets for report and budget tests. Each call creates a new department, so assertions on it
/// are exact no matter what other tests have written to the shared database.
/// </summary>
internal static class TestData
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    internal static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    /// <remarks>
    /// Today: budget 400; posted 100, approved 50, submitted 25 (all Supplies, requested by Demo Employee);
    /// plus a draft (999) and a cancelled expense (888) that every report must ignore.
    /// Last month: posted 10.
    /// </remarks>
    /// <summary>Creates an isolated department with a known budget and one expense in each relevant state.</summary>
    internal static async Task<Guid> IsolatedDepartmentWithSpendAsync(SqlServerFixture fixture, decimal budget = 400m)
    {
        return await fixture.WithDbAsync(async db =>
        {
            var department = Department.Create($"Isolated {Guid.NewGuid():N}"[..30]);
            db.Departments.Add(department);
            db.Budgets.Add(Budget.Create(Guid.NewGuid(), department.Id, Today.Year, Today.Month, budget, "USD", Now));

            ExpenseRequest Expense(decimal amount, DateOnly date)
            {
                var e = ExpenseRequest.CreateDraft(Guid.NewGuid(), DemoDataSeeder.Employee.UserId, department.Id, amount,
                    "USD", ExpenseCategory.Supplies, "Budget test", date, Now);
                db.ExpenseRequests.Add(e);
                return e;
            }

            var posted = Expense(100m, Today);
            posted.Submit(Now); db.ApprovalDecisions.Add(posted.Approve(DemoDataSeeder.Manager.UserId, null, Now));
            posted.Post(DemoDataSeeder.FinanceOfficer.UserId, Now);

            var approved = Expense(50m, Today);
            approved.Submit(Now); db.ApprovalDecisions.Add(approved.Approve(DemoDataSeeder.Manager.UserId, null, Now));

            Expense(25m, Today).Submit(Now);          // pending: shown, not subtracted
            Expense(999m, Today);                     // draft: excluded
            Expense(888m, Today).Cancel(Now);         // cancelled: excluded

            var lastMonth = Today.AddMonths(-1);       // other period: excluded from this month
            var old = Expense(10m, lastMonth);
            old.Submit(Now); db.ApprovalDecisions.Add(old.Approve(DemoDataSeeder.Manager.UserId, null, Now));
            old.Post(DemoDataSeeder.FinanceOfficer.UserId, Now);

            await db.SaveChangesAsync();
            return department.Id;
        });
    }

}

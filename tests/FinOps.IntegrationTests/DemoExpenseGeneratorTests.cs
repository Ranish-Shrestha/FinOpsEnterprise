using FinOps.Domain.Expenses;
using FinOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinOps.IntegrationTests;

/// <summary><c>seed-demo-expenses</c>: realistic, rule-abiding history, safe to run twice. Uses its own database.</summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class DemoExpenseGeneratorTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Generates_every_workflow_state_with_its_side_effects_and_is_idempotent()
    {
        var options = new DbContextOptionsBuilder<FinOpsDbContext>().UseSqlServer(fixture.ConnectionStringFor("FinOpsDemoExpenses")).Options;
        await using (var setup = new FinOpsDbContext(options))
        {
            await setup.Database.EnsureDeletedAsync();
            await setup.Database.MigrateAsync();
        }

        var realUser = Guid.NewGuid();
        await using (var db = new FinOpsDbContext(options))
        {
            await DatabaseAdministration.ProvisionUserAsync(db, realUser, "Real Person", "real@example.invalid", "Manager", "Demo Engineering");
        }

        DemoExpenseResult first;
        await using (var db = new FinOpsDbContext(options))
        {
            first = await DemoExpenseGenerator.GenerateAsync(db, months: 6, realUser, Now);
        }

        Assert.True(first.Expenses > 100, $"only {first.Expenses} expenses");
        foreach (var status in new[] { ExpenseStatus.Draft, ExpenseStatus.Submitted, ExpenseStatus.Approved, ExpenseStatus.Rejected,
                     ExpenseStatus.Posted, ExpenseStatus.Cancelled })
            Assert.True(first.ByStatus[status] > 0, $"no {status} expenses");
        Assert.True(first.ByStatus[ExpenseStatus.Posted] > first.Expenses / 2, "most history should be posted");

        await using (var db = new FinOpsDbContext(options))
        {
            // The four fixed sample expenses use the real clock; check only the generated history.
            Guid[] samples = [DemoDataSeeder.DraftExpense, DemoDataSeeder.SubmittedExpense, DemoDataSeeder.ApprovedExpense, DemoDataSeeder.ManagersOwnExpense];
            var expenses = await db.ExpenseRequests.AsNoTracking().Where(e => !samples.Contains(e.Id)).ToListAsync();
            Assert.All(expenses, e => Assert.True(e.UpdatedAtUtc <= Now && e.ExpenseDate <= DateOnly.FromDateTime(Now.UtcDateTime)));
            Assert.All(expenses.Where(e => e.Status == ExpenseStatus.Posted), e => Assert.NotEqual(e.RequesterId, e.PostedById));
            Assert.Equal(3, expenses.Select(e => e.DepartmentId).Distinct().Count());

            var decisions = await db.ApprovalDecisions.AsNoTracking().Where(d => !samples.Contains(d.ExpenseRequestId)).ToListAsync();
            var requesterOf = expenses.ToDictionary(e => e.Id, e => e.RequesterId);
            Assert.All(decisions, d => Assert.NotEqual(requesterOf[d.ExpenseRequestId], d.ApproverId)); // separation of duties

            var posted = expenses.Count(e => e.Status == ExpenseStatus.Posted);
            Assert.Equal(posted, await db.Reimbursements.CountAsync());
            Assert.Equal(posted, await db.AuditEvents.CountAsync(a => a.Action == "expense.posted"));
            Assert.Equal(first.Notifications, await db.Notifications.CountAsync());

            var realUserId = await db.UserProfiles.Where(u => u.EntraObjectId == realUser).Select(u => u.Id).SingleAsync();
            Assert.Contains(expenses, e => e.RequesterId == realUserId);
            Assert.True(await db.Budgets.CountAsync(b => b.Year == 2026 && b.Month == 6) == 3, "a budget per department per month");
        }

        await using (var db = new FinOpsDbContext(options))
        {
            var second = await DemoExpenseGenerator.GenerateAsync(db, months: 6, realUser, Now);
            Assert.Equal(0, second.Expenses);
            Assert.Equal(first.Expenses, second.AlreadyPresent);
            Assert.Equal(first.Expenses + 4, await db.ExpenseRequests.CountAsync()); // plus the four sample expenses
        }

        // Raising the real user's monthly count adds only their extra slots: 6 months x (5 - 3).
        await using (var db = new FinOpsDbContext(options))
        {
            var realUserId = await db.UserProfiles.Where(u => u.EntraObjectId == realUser).Select(u => u.Id).SingleAsync();
            var before = await db.ExpenseRequests.CountAsync(e => e.RequesterId == realUserId);
            var third = await DemoExpenseGenerator.GenerateAsync(db, months: 6, realUser, Now, extraPerMonth: 5);
            Assert.Equal(12, third.Expenses);
            Assert.Equal(before + 12, await db.ExpenseRequests.CountAsync(e => e.RequesterId == realUserId));
            Assert.Equal(first.Expenses + 12 + 4, await db.ExpenseRequests.CountAsync());
        }
    }

    [Fact]
    public async Task Refuses_an_unprovisioned_extra_user_and_out_of_range_months()
    {
        await fixture.WithDbAsync(async db =>
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DemoExpenseGenerator.GenerateAsync(db, 12));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoExpenseGenerator.GenerateAsync(db, 1, Guid.NewGuid()));
            Assert.Contains("provision-user", error.Message);
            return 0;
        });
    }
}

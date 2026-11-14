using FinOps.Application.Budgets;
using FinOps.Application.Identity;
using FinOps.Domain.Budgets;

namespace FinOps.UnitTests;

public sealed class BudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static Budget Create(int year = 2026, int month = 11, decimal amount = 1_000m, string currency = "USD") =>
        Budget.Create(Guid.NewGuid(), Guid.NewGuid(), year, month, amount, currency, Now);

    [Fact]
    public void Valid_budgets_are_created_and_normalized()
    {
        var budget = Budget.Create(Guid.NewGuid(), Guid.NewGuid(), 2026, 1, 2_500.50m, "usd", Now);
        Assert.Equal("USD", budget.Currency);
        Assert.Equal(2_500.50m, budget.Amount);
        Assert.Equal(Now, budget.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(1999, 1)]
    [InlineData(2101, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    public void Periods_outside_range_are_rejected(int year, int month) =>
        Assert.Throws<ArgumentException>(() => Create(year, month));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.001)]
    [InlineData(100_000_000.01)]
    public void Invalid_amounts_are_rejected(decimal amount) =>
        Assert.Throws<ArgumentException>(() => Create(amount: amount));

    [Fact]
    public void Only_supported_currencies_are_accepted() =>
        Assert.Throws<ArgumentException>(() => Create(currency: "EUR"));

    [Fact]
    public void Amount_changes_are_validated_and_timestamped()
    {
        var budget = Create();
        var later = Now.AddDays(1);
        budget.ChangeAmount(1_200m, later);
        Assert.Equal(1_200m, budget.Amount);
        Assert.Equal(later, budget.UpdatedAtUtc);
        Assert.Throws<ArgumentException>(() => budget.ChangeAmount(0m, later));
        Assert.Equal(1_200m, budget.Amount);
    }

    [Fact]
    public void Budget_scope_follows_permissions()
    {
        var departments = new[] { Guid.NewGuid() };
        CurrentUserDto User(params string[] permissions) => new(Guid.NewGuid(), "T", "t@example.invalid", [], permissions, departments);

        Assert.True(BudgetService.ScopeFor(User(ApplicationPermissions.BudgetsReadAll))!.All);
        var manager = BudgetService.ScopeFor(User(ApplicationPermissions.BudgetsReadDepartment))!;
        Assert.False(manager.All);
        Assert.Equal(departments, manager.DepartmentIds);
        Assert.Null(BudgetService.ScopeFor(User(ApplicationPermissions.ExpensesCreate)));
    }
}

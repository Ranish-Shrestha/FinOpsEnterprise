using FinOps.Application.Expenses;
using FinOps.Application.Identity;
using FinOps.Domain.Expenses;

namespace FinOps.UnitTests;

public sealed class ExpenseAccessPolicyTests
{
    private static readonly Guid Engineering = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();

    private static CurrentUserDto User(Guid[] departments, params string[] permissions) =>
        new(Guid.NewGuid(), "Test", "t@example.invalid", [], permissions, departments);

    private static readonly CurrentUserDto Employee = User([Engineering], ApplicationPermissions.ExpensesCreate);
    private static readonly CurrentUserDto EngineeringManager = User([Engineering], ApplicationPermissions.ExpensesCreate,
        ApplicationPermissions.ExpensesReadDepartment, ApplicationPermissions.ExpensesApprove);
    private static readonly CurrentUserDto FinanceManager = User([Finance], ApplicationPermissions.ExpensesCreate,
        ApplicationPermissions.ExpensesReadDepartment, ApplicationPermissions.ExpensesApprove);
    private static readonly CurrentUserDto FinanceOfficer = User([Finance], ApplicationPermissions.ExpensesCreate,
        ApplicationPermissions.ExpensesReadAll, ApplicationPermissions.ExpensesPost);
    private static readonly CurrentUserDto Administrator = User([], ApplicationPermissions.ExpensesReadAll);

    [Fact]
    public void Visibility_follows_ownership_department_and_global_read()
    {
        var other = Guid.NewGuid();
        Assert.True(ExpenseAccessPolicy.CanView(Employee, Employee.Id, Engineering));
        Assert.False(ExpenseAccessPolicy.CanView(Employee, other, Engineering));          // IDOR: a colleague's expense
        Assert.True(ExpenseAccessPolicy.CanView(EngineeringManager, other, Engineering));
        Assert.False(ExpenseAccessPolicy.CanView(FinanceManager, other, Engineering));    // wrong department
        Assert.True(ExpenseAccessPolicy.CanView(FinanceOfficer, other, Engineering));
        Assert.True(ExpenseAccessPolicy.CanView(Administrator, other, Engineering));
    }

    [Fact]
    public void Creating_requires_permission_and_active_membership()
    {
        Assert.True(ExpenseAccessPolicy.CanCreateIn(Employee, Engineering));
        Assert.False(ExpenseAccessPolicy.CanCreateIn(Employee, Finance));
        Assert.False(ExpenseAccessPolicy.CanCreateIn(Administrator, Engineering));
    }

    [Fact]
    public void Owner_sees_owner_actions_by_state()
    {
        IReadOnlyList<string> For(ExpenseStatus s) => ExpenseAccessPolicy.AllowedActions(Employee, Employee.Id, Engineering, s);
        Assert.Equal([ExpenseActions.Edit, ExpenseActions.Submit, ExpenseActions.Cancel], For(ExpenseStatus.Draft));
        Assert.Equal([ExpenseActions.Withdraw], For(ExpenseStatus.Submitted));
        Assert.Equal([ExpenseActions.Revise], For(ExpenseStatus.Rejected));
        Assert.Empty(For(ExpenseStatus.Approved));
        Assert.Empty(For(ExpenseStatus.Posted));
        Assert.Empty(For(ExpenseStatus.Cancelled));
    }

    [Fact]
    public void Only_a_manager_of_the_department_who_is_not_the_requester_may_decide()
    {
        var requester = Guid.NewGuid();
        Assert.Equal([ExpenseActions.Approve, ExpenseActions.Reject],
            ExpenseAccessPolicy.AllowedActions(EngineeringManager, requester, Engineering, ExpenseStatus.Submitted));
        Assert.Empty(ExpenseAccessPolicy.AllowedActions(FinanceManager, requester, Engineering, ExpenseStatus.Submitted));
        Assert.Empty(ExpenseAccessPolicy.AllowedActions(Administrator, requester, Engineering, ExpenseStatus.Submitted));

        // A manager's own submitted expense: may withdraw, never approve.
        Assert.Equal([ExpenseActions.Withdraw],
            ExpenseAccessPolicy.AllowedActions(EngineeringManager, EngineeringManager.Id, Engineering, ExpenseStatus.Submitted));
    }

    [Fact]
    public void Only_finance_posts_and_never_their_own_expense()
    {
        var requester = Guid.NewGuid();
        Assert.Equal([ExpenseActions.Post], ExpenseAccessPolicy.AllowedActions(FinanceOfficer, requester, Engineering, ExpenseStatus.Approved));
        Assert.Empty(ExpenseAccessPolicy.AllowedActions(FinanceOfficer, FinanceOfficer.Id, Finance, ExpenseStatus.Approved));
        Assert.Empty(ExpenseAccessPolicy.AllowedActions(Administrator, requester, Engineering, ExpenseStatus.Approved));
    }
}

public sealed class ExpenseVersionAndSortTests
{
    [Fact]
    public void Etag_round_trips_through_if_match()
    {
        byte[] version = [0, 0, 0, 0, 0, 0, 0x1F, 0xA2];
        var etag = ExpenseVersion.ToETag(version);
        Assert.True(ExpenseVersion.TryParseIfMatch(etag, out var parsed));
        Assert.Equal(version, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("AAAAAAAAH6I=")]          // unquoted
    [InlineData("W/\"AAAAAAAAH6I=\"")]    // weak
    [InlineData("\"not base64!\"")]
    [InlineData("\"AAAA\"")]              // wrong length
    public void Malformed_if_match_values_are_rejected(string? header) =>
        Assert.False(ExpenseVersion.TryParseIfMatch(header, out _));

    [Theory]
    [InlineData("amount", ExpenseSortField.Amount, false)]
    [InlineData("-submittedAt", ExpenseSortField.SubmittedAt, true)]
    [InlineData("EXPENSEDATE", ExpenseSortField.ExpenseDate, false)]
    public void Allow_listed_sort_fields_parse(string value, ExpenseSortField field, bool descending)
    {
        Assert.True(ExpenseSort.TryParse(value, out var sort));
        Assert.Equal(new ExpenseSort(field, descending), sort);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("1")]
    [InlineData("-")]
    public void Other_sort_fields_are_rejected(string value) =>
        Assert.False(ExpenseSort.TryParse(value, out _));
}

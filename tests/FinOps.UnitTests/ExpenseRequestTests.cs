using FinOps.Domain.Expenses;

namespace FinOps.UnitTests;

public sealed class ExpenseRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();

    private static ExpenseRequest Draft(decimal amount = 120.50m, string currency = "USD", DateOnly? date = null) =>
        ExpenseRequest.CreateDraft(Guid.NewGuid(), Requester, Guid.NewGuid(), amount, currency,
            ExpenseCategory.Travel, "  Taxi to the airport  ", date ?? Today.AddDays(-1), Now);

    private static ExpenseRequest InState(ExpenseStatus status)
    {
        var expense = Draft();
        switch (status)
        {
            case ExpenseStatus.Draft: break;
            case ExpenseStatus.Submitted: expense.Submit(Now); break;
            case ExpenseStatus.Approved: expense.Submit(Now); expense.Approve(Manager, null, Now); break;
            case ExpenseStatus.Rejected: expense.Submit(Now); expense.Reject(Manager, "Missing receipt", Now); break;
            case ExpenseStatus.Posted: expense.Submit(Now); expense.Approve(Manager, null, Now); expense.Post(Finance, Now); break;
            case ExpenseStatus.Cancelled: expense.Cancel(Now); break;
        }
        return expense;
    }

    private static readonly Dictionary<string, (Action<ExpenseRequest> Act, ExpenseStatus From, ExpenseStatus To)> Transitions = new()
    {
        ["submit"] = (e => e.Submit(Now), ExpenseStatus.Draft, ExpenseStatus.Submitted),
        ["withdraw"] = (e => e.Withdraw(Now), ExpenseStatus.Submitted, ExpenseStatus.Draft),
        ["cancel"] = (e => e.Cancel(Now), ExpenseStatus.Draft, ExpenseStatus.Cancelled),
        ["revise"] = (e => e.Revise(Now), ExpenseStatus.Rejected, ExpenseStatus.Draft),
        ["approve"] = (e => e.Approve(Manager, null, Now), ExpenseStatus.Submitted, ExpenseStatus.Approved),
        ["reject"] = (e => e.Reject(Manager, "No receipt", Now), ExpenseStatus.Submitted, ExpenseStatus.Rejected),
        ["post"] = (e => e.Post(Finance, Now), ExpenseStatus.Approved, ExpenseStatus.Posted),
        ["edit"] = (e => e.UpdateDraft(10m, "USD", ExpenseCategory.Meals, "Lunch", Today, Now), ExpenseStatus.Draft, ExpenseStatus.Draft),
    };

    public static TheoryData<string, ExpenseStatus> EveryTransitionFromEveryState()
    {
        var data = new TheoryData<string, ExpenseStatus>();
        foreach (var action in Transitions.Keys)
            foreach (var status in Enum.GetValues<ExpenseStatus>())
                data.Add(action, status);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTransitionFromEveryState))]
    public void Each_transition_succeeds_only_from_its_source_state(string action, ExpenseStatus from)
    {
        var (act, validFrom, to) = Transitions[action];
        var expense = InState(from);

        if (from == validFrom)
        {
            act(expense);
            Assert.Equal(to, expense.Status);
        }
        else
        {
            Assert.Throws<InvalidExpenseStateException>(() => act(expense));
            Assert.Equal(from, expense.Status);
        }
    }

    [Fact]
    public void New_drafts_are_trimmed_and_normalized()
    {
        var expense = ExpenseRequest.CreateDraft(Guid.NewGuid(), Requester, Guid.NewGuid(), 10m, "usd",
            ExpenseCategory.Meals, "  Lunch  ", Today, Now);
        Assert.Equal(ExpenseStatus.Draft, expense.Status);
        Assert.Equal("USD", expense.Currency);
        Assert.Equal("Lunch", expense.Description);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.005)]
    [InlineData(1_000_000.01)]
    public void Invalid_amounts_are_rejected(decimal amount) =>
        Assert.Throws<ArgumentException>(() => Draft(amount));

    [Theory]
    [InlineData("EUR")]
    [InlineData("US")]
    [InlineData("")]
    public void Only_supported_currencies_are_accepted(string currency) =>
        Assert.ThrowsAny<ArgumentException>(() => Draft(currency: currency));

    [Fact]
    public void Future_and_stale_expense_dates_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Draft(date: Today.AddDays(1)));
        Assert.Throws<ArgumentException>(() => Draft(date: Today.AddYears(-1).AddDays(-1)));
        Assert.Equal(Today, Draft(date: Today).ExpenseDate);
    }

    [Fact]
    public void Requester_cannot_approve_reject_or_post_their_own_expense()
    {
        var submitted = InState(ExpenseStatus.Submitted);
        Assert.Throws<SeparationOfDutiesException>(() => submitted.Approve(Requester, null, Now));
        Assert.Throws<SeparationOfDutiesException>(() => submitted.Reject(Requester, "no", Now));
        Assert.Equal(ExpenseStatus.Submitted, submitted.Status);

        var approved = InState(ExpenseStatus.Approved);
        Assert.Throws<SeparationOfDutiesException>(() => approved.Post(Requester, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejection_requires_a_comment(string comment) =>
        Assert.Throws<ArgumentException>(() => InState(ExpenseStatus.Submitted).Reject(Manager, comment, Now));

    [Fact]
    public void Decisions_record_who_decided_and_when()
    {
        var expense = InState(ExpenseStatus.Submitted);
        var decision = expense.Approve(Manager, "  Looks good ", Now);
        Assert.Equal(expense.Id, decision.ExpenseRequestId);
        Assert.Equal(Manager, decision.ApproverId);
        Assert.Equal(ApprovalOutcome.Approved, decision.Outcome);
        Assert.Equal("Looks good", decision.Comment);
        Assert.Equal(Now, expense.DecidedAtUtc);
    }

    [Fact]
    public void Posting_records_the_finance_user()
    {
        var expense = InState(ExpenseStatus.Posted);
        Assert.Equal(Finance, expense.PostedById);
        Assert.Equal(Now, expense.PostedAtUtc);
    }

    [Fact]
    public void Withdraw_and_revise_clear_review_timestamps()
    {
        var withdrawn = InState(ExpenseStatus.Submitted);
        withdrawn.Withdraw(Now);
        Assert.Null(withdrawn.SubmittedAtUtc);

        var revised = InState(ExpenseStatus.Rejected);
        revised.Revise(Now);
        Assert.Null(revised.SubmittedAtUtc);
        Assert.Null(revised.DecidedAtUtc);
    }

    [Fact]
    public void Failed_edit_leaves_the_draft_unchanged()
    {
        var expense = Draft();
        Assert.Throws<ArgumentException>(() => expense.UpdateDraft(5m, "EUR", ExpenseCategory.Meals, "Lunch", Today, Now));
        Assert.Equal(120.50m, expense.Amount);
        Assert.Equal("Taxi to the airport", expense.Description);
    }
}

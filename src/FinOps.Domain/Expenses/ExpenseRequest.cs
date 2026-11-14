namespace FinOps.Domain.Expenses;

public enum ExpenseStatus { Draft, Submitted, Approved, Rejected, Posted, Cancelled }

public enum ExpenseCategory { Travel, Lodging, Meals, Supplies, Software, Training, Other }

public enum ApprovalOutcome { Approved, Rejected }

/// <summary>
/// An employee's reimbursement request. All state changes go through methods that enforce the workflow:
/// <code>
/// Draft ─submit→ Submitted ─approve→ Approved ─post→ Posted
///   │  ↖─withdraw──┘   └─reject→ Rejected ─revise→ Draft
///   └─cancel→ Cancelled
/// </code>
/// Who may call each method (permissions, department membership) is decided by the application layer;
/// rules that hold for everyone (separation of duties, valid states, money rules) are enforced here.
/// </summary>
public sealed class ExpenseRequest
{
    public const int MaximumDescriptionLength = 500;
    public const int MaximumCommentLength = 1000;
    public const decimal MaximumAmount = 1_000_000m;

    private ExpenseRequest() { }

    public Guid Id { get; private set; }
    public Guid RequesterId { get; private set; }
    public Guid DepartmentId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public ExpenseCategory Category { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public DateOnly ExpenseDate { get; private set; }
    public ExpenseStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public DateTimeOffset? PostedAtUtc { get; private set; }
    public Guid? PostedById { get; private set; }

    /// <summary>SQL Server rowversion; the optimistic concurrency token exposed to clients as an ETag.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public static ExpenseRequest CreateDraft(Guid id, Guid requesterId, Guid departmentId, decimal amount,
        string currency, ExpenseCategory category, string description, DateOnly expenseDate, DateTimeOffset nowUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("An expense ID is required.", nameof(id));
        if (requesterId == Guid.Empty) throw new ArgumentException("A requester is required.", nameof(requesterId));
        if (departmentId == Guid.Empty) throw new ArgumentException("A department is required.", nameof(departmentId));
        RequireUtc(nowUtc);

        var expense = new ExpenseRequest
        {
            Id = id,
            RequesterId = requesterId,
            DepartmentId = departmentId,
            Status = ExpenseStatus.Draft,
            CreatedAtUtc = nowUtc
        };
        expense.SetDetails(amount, currency, category, description, expenseDate, nowUtc);
        return expense;
    }

    /// <summary>Edits a draft. Only drafts are editable so that reviewers always see what was submitted.</summary>
    public void UpdateDraft(decimal amount, string currency, ExpenseCategory category, string description,
        DateOnly expenseDate, DateTimeOffset nowUtc)
    {
        RequireStatus(ExpenseStatus.Draft, "edit");
        SetDetails(amount, currency, category, description, expenseDate, nowUtc);
    }

    public void Submit(DateTimeOffset nowUtc)
    {
        RequireStatus(ExpenseStatus.Draft, "submit");
        Transition(ExpenseStatus.Submitted, nowUtc);
        SubmittedAtUtc = nowUtc;
    }

    public void Withdraw(DateTimeOffset nowUtc)
    {
        RequireStatus(ExpenseStatus.Submitted, "withdraw");
        Transition(ExpenseStatus.Draft, nowUtc);
        SubmittedAtUtc = null;
    }

    public void Cancel(DateTimeOffset nowUtc)
    {
        RequireStatus(ExpenseStatus.Draft, "cancel");
        Transition(ExpenseStatus.Cancelled, nowUtc);
    }

    public void Revise(DateTimeOffset nowUtc)
    {
        RequireStatus(ExpenseStatus.Rejected, "revise");
        Transition(ExpenseStatus.Draft, nowUtc);
        SubmittedAtUtc = null;
        DecidedAtUtc = null;
    }

    public ApprovalDecision Approve(Guid approverId, string? comment, DateTimeOffset nowUtc) =>
        Decide(approverId, ApprovalOutcome.Approved, comment, nowUtc);

    public ApprovalDecision Reject(Guid approverId, string comment, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(comment))
            throw new ArgumentException("A rejection needs a comment explaining what to change.", nameof(comment));
        return Decide(approverId, ApprovalOutcome.Rejected, comment, nowUtc);
    }

    public void Post(Guid postedById, DateTimeOffset nowUtc)
    {
        if (postedById == Guid.Empty) throw new ArgumentException("A finance user is required.", nameof(postedById));
        if (postedById == RequesterId) throw new SeparationOfDutiesException("You cannot post your own expense.");
        RequireStatus(ExpenseStatus.Approved, "post");
        Transition(ExpenseStatus.Posted, nowUtc);
        PostedAtUtc = nowUtc;
        PostedById = postedById;
    }

    private ApprovalDecision Decide(Guid approverId, ApprovalOutcome outcome, string? comment, DateTimeOffset nowUtc)
    {
        if (approverId == Guid.Empty) throw new ArgumentException("An approver is required.", nameof(approverId));
        if (approverId == RequesterId) throw new SeparationOfDutiesException("You cannot approve or reject your own expense.");
        RequireStatus(ExpenseStatus.Submitted, outcome == ApprovalOutcome.Approved ? "approve" : "reject");
        Transition(outcome == ApprovalOutcome.Approved ? ExpenseStatus.Approved : ExpenseStatus.Rejected, nowUtc);
        DecidedAtUtc = nowUtc;
        return ApprovalDecision.Record(Id, approverId, outcome, comment, nowUtc);
    }

    private void SetDetails(decimal amount, string currency, ExpenseCategory category, string description,
        DateOnly expenseDate, DateTimeOffset nowUtc)
    {
        RequireUtc(nowUtc);
        Money.Validate(amount, currency);
        if (!Enum.IsDefined(category)) throw new ArgumentException("Unknown expense category.", nameof(category));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var trimmed = description.Trim();
        if (trimmed.Length > MaximumDescriptionLength)
            throw new ArgumentException($"Descriptions must be at most {MaximumDescriptionLength} characters.", nameof(description));
        if (expenseDate > DateOnly.FromDateTime(nowUtc.UtcDateTime))
            throw new ArgumentException("The expense date cannot be in the future.", nameof(expenseDate));
        if (expenseDate < DateOnly.FromDateTime(nowUtc.UtcDateTime).AddYears(-1))
            throw new ArgumentException("Expenses older than one year cannot be claimed.", nameof(expenseDate));

        Amount = amount;
        Currency = currency.ToUpperInvariant();
        Category = category;
        Description = trimmed;
        ExpenseDate = expenseDate;
        UpdatedAtUtc = nowUtc;
    }

    private void RequireStatus(ExpenseStatus required, string action)
    {
        if (Status != required)
            throw new InvalidExpenseStateException($"Cannot {action} an expense that is {Status}; it must be {required}.");
    }

    private void Transition(ExpenseStatus next, DateTimeOffset nowUtc)
    {
        RequireUtc(nowUtc);
        Status = next;
        UpdatedAtUtc = nowUtc;
    }

    private static void RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamps must be UTC.");
    }
}

/// <summary>Immutable record of a manager's decision. Kept even after a rejected expense is revised.</summary>
public sealed class ApprovalDecision
{
    private ApprovalDecision() { }

    public Guid Id { get; private set; }
    public Guid ExpenseRequestId { get; private set; }
    public Guid ApproverId { get; private set; }
    public ApprovalOutcome Outcome { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset DecidedAtUtc { get; private set; }

    internal static ApprovalDecision Record(Guid expenseId, Guid approverId, ApprovalOutcome outcome,
        string? comment, DateTimeOffset nowUtc)
    {
        var trimmed = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (trimmed?.Length > ExpenseRequest.MaximumCommentLength)
            throw new ArgumentException($"Comments must be at most {ExpenseRequest.MaximumCommentLength} characters.", nameof(comment));
        return new ApprovalDecision
        {
            Id = Guid.NewGuid(),
            ExpenseRequestId = expenseId,
            ApproverId = approverId,
            Outcome = outcome,
            Comment = trimmed,
            DecidedAtUtc = nowUtc
        };
    }
}

public static class Money
{
    /// <summary>Supported ISO 4217 codes. USD only for now; the column exists so this can grow without a migration.</summary>
    public static readonly IReadOnlySet<string> SupportedCurrencies = new HashSet<string>(StringComparer.Ordinal) { "USD" };

    public static void Validate(decimal amount, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (!SupportedCurrencies.Contains(currency.ToUpperInvariant()))
            throw new ArgumentException($"Currency must be one of: {string.Join(", ", SupportedCurrencies)}.", nameof(currency));
        if (amount <= 0) throw new ArgumentException("The amount must be greater than zero.", nameof(amount));
        if (amount > ExpenseRequest.MaximumAmount)
            throw new ArgumentException($"The amount cannot exceed {ExpenseRequest.MaximumAmount:N0}.", nameof(amount));
        if (decimal.Round(amount, 2) != amount)
            throw new ArgumentException("The amount can have at most two decimal places.", nameof(amount));
    }
}

/// <summary>The requested transition is not valid from the expense's current state (HTTP 409).</summary>
public sealed class InvalidExpenseStateException(string message) : InvalidOperationException(message);

/// <summary>The actor is not allowed to act on their own expense (HTTP 403).</summary>
public sealed class SeparationOfDutiesException(string message) : InvalidOperationException(message);

using System.Text.Json;
using FinOps.Application.Common;
using FinOps.Application.Identity;
using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;

namespace FinOps.Application.Expenses;

/// <summary>The authenticated, provisioned caller plus the request's correlation ID for auditing.</summary>
public sealed record Actor(CurrentUserDto User, string? CorrelationId);

/// <summary>
/// Expense use cases. Every mutation follows the same order, which fixes the HTTP semantics:
/// invisible → 404, visible but not allowed → 403, stale version → 412, wrong state → 409, bad input → 400.
/// State change and audit row are saved in one transaction; denials are audited on their own.
/// </summary>
public sealed class ExpenseWorkflow(IExpenseStore store, IExpenseReader reader, TimeProvider clock)
{
    private const string ResourceType = "expense";

    public async Task<Result<PagedResult<ExpenseSummaryDto>>> ListAsync(Actor actor, ExpenseListQuery query,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        Paging.Validate(query.Page, query.PageSize, errors);
        ExpenseStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (TryParseEnum<ExpenseStatus>(query.Status, out var parsed)) status = parsed;
            else errors["status"] = [$"Status must be one of: {string.Join(", ", Enum.GetNames<ExpenseStatus>())}."];
        }

        if (!ExpenseSort.TryParse(query.Sort, out var sort))
            errors["sort"] = [$"Sort must be one of: {string.Join(", ", Enum.GetNames<ExpenseSortField>())}, optionally prefixed with '-'."];
        if (errors.Count > 0) return Result<PagedResult<ExpenseSummaryDto>>.Invalid(errors);

        var page = await reader.ListAsync(ExpenseAccessPolicy.VisibilityFor(actor.User), status, query.DepartmentId,
            query.Mine, query.Page, query.PageSize, sort, cancellationToken);
        return Result<PagedResult<ExpenseSummaryDto>>.Ok(page);
    }

    public async Task<Result<ExpenseDetailDto>> GetAsync(Actor actor, Guid id, CancellationToken cancellationToken)
    {
        var detail = await DetailForAsync(actor.User, id, cancellationToken);
        return detail is null ? Result<ExpenseDetailDto>.NotFound() : Result<ExpenseDetailDto>.Ok(detail);
    }

    public async Task<Result<ExpenseDetailDto>> CreateAsync(Actor actor, CreateExpenseInput input,
        CancellationToken cancellationToken)
    {
        if (!ExpenseAccessPolicy.CanCreateIn(actor.User, input.DepartmentId))
        {
            await RecordDenialAsync(actor, "expense.create", null, "not-a-member", cancellationToken);
            return Result<ExpenseDetailDto>.Forbidden("You can only create expenses in a department you are an active member of.");
        }

        if (!TryParseEnum<ExpenseCategory>(input.Category, out var category))
            return Result<ExpenseDetailDto>.Invalid("category", CategoryMessage());

        ExpenseRequest expense;
        try
        {
            expense = ExpenseRequest.CreateDraft(Guid.NewGuid(), actor.User.Id, input.DepartmentId, input.Amount,
                input.Currency, category, input.Description, input.ExpenseDate, clock.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            return Result<ExpenseDetailDto>.Invalid(FieldFor(ex), CleanMessage(ex));
        }

        store.Add(expense);
        store.Add(Audit(actor, "expense.created", expense.Id, AuditOutcome.Succeeded, new { to = expense.Status.ToString() }));
        await store.SaveChangesAsync(null, null, cancellationToken);
        return Result<ExpenseDetailDto>.Created((await DetailForAsync(actor.User, expense.Id, cancellationToken))!);
    }

    public Task<Result<ExpenseDetailDto>> UpdateAsync(Actor actor, Guid id, string? ifMatch, ExpenseInput input,
        CancellationToken cancellationToken)
    {
        if (!TryParseEnum<ExpenseCategory>(input.Category, out var category))
            return Task.FromResult(Result<ExpenseDetailDto>.Invalid("category", CategoryMessage()));

        return MutateAsync(actor, id, ifMatch, ExpenseActions.Edit, "expense.updated",
            expense => ExpenseAccessPolicy.IsOwnerWithCreate(actor.User, expense.RequesterId)
                ? null : "Only the requester can edit this expense.",
            (expense, now) =>
            {
                expense.UpdateDraft(input.Amount, input.Currency, category, input.Description, input.ExpenseDate, now);
                return null;
            },
            cancellationToken);
    }

    public Task<Result<ExpenseDetailDto>> TransitionAsync(Actor actor, Guid id, string action, string? ifMatch,
        string? comment, CancellationToken cancellationToken)
    {
        var user = actor.User;
        string? OwnerOnly(ExpenseRequest e) =>
            ExpenseAccessPolicy.IsOwnerWithCreate(user, e.RequesterId) ? null : $"Only the requester can {action} this expense.";
        string? ManagerOnly(ExpenseRequest e) =>
            ExpenseAccessPolicy.CanDecideIn(user, e.DepartmentId) ? null : "Only a manager of this expense's department can approve or reject it.";
        string? FinanceOnly(ExpenseRequest _) =>
            ExpenseAccessPolicy.CanPost(user) ? null : "Only finance staff can post expenses.";

        return action switch
        {
            ExpenseActions.Submit => MutateAsync(actor, id, ifMatch, action, "expense.submitted", OwnerOnly,
                (e, now) => { e.Submit(now); return null; }, cancellationToken),
            ExpenseActions.Withdraw => MutateAsync(actor, id, ifMatch, action, "expense.withdrawn", OwnerOnly,
                (e, now) => { e.Withdraw(now); return null; }, cancellationToken),
            ExpenseActions.Cancel => MutateAsync(actor, id, ifMatch, action, "expense.cancelled", OwnerOnly,
                (e, now) => { e.Cancel(now); return null; }, cancellationToken),
            ExpenseActions.Revise => MutateAsync(actor, id, ifMatch, action, "expense.revised", OwnerOnly,
                (e, now) => { e.Revise(now); return null; }, cancellationToken),
            ExpenseActions.Approve => MutateAsync(actor, id, ifMatch, action, "expense.approved", ManagerOnly,
                (e, now) => e.Approve(user.Id, comment, now), cancellationToken),
            ExpenseActions.Reject => MutateAsync(actor, id, ifMatch, action, "expense.rejected", ManagerOnly,
                (e, now) => e.Reject(user.Id, comment ?? string.Empty, now), cancellationToken),
            ExpenseActions.Post => MutateAsync(actor, id, ifMatch, action, "expense.posted", FinanceOnly,
                (e, now) => { e.Post(user.Id, now); return null; }, cancellationToken),
            _ => Task.FromResult(Result<ExpenseDetailDto>.NotFound())
        };
    }

    private async Task<Result<ExpenseDetailDto>> MutateAsync(Actor actor, Guid id, string? ifMatch, string action,
        string auditAction, Func<ExpenseRequest, string?> authorize,
        Func<ExpenseRequest, DateTimeOffset, ApprovalDecision?> apply, CancellationToken cancellationToken)
    {
        var expense = await store.FindForUpdateAsync(id, cancellationToken);
        if (expense is null || !ExpenseAccessPolicy.CanView(actor.User, expense.RequesterId, expense.DepartmentId))
        {
            // Same response whether the expense is missing or merely invisible, so IDs cannot be probed.
            await RecordDenialAsync(actor, $"expense.{action}", id, "not-visible", cancellationToken);
            return Result<ExpenseDetailDto>.NotFound();
        }

        if (authorize(expense) is { } reason)
        {
            await RecordDenialAsync(actor, $"expense.{action}", id, "not-permitted", cancellationToken);
            return Result<ExpenseDetailDto>.Forbidden(reason);
        }

        if (!ExpenseVersion.TryParseIfMatch(ifMatch, out var expectedVersion)
            || !expense.RowVersion.AsSpan().SequenceEqual(expectedVersion))
        {
            return Result<ExpenseDetailDto>.PreconditionFailed(
                "The expense has changed since you loaded it. Reload it and try again.");
        }

        var from = expense.Status;
        ApprovalDecision? decision;
        try
        {
            decision = apply(expense, clock.GetUtcNow());
        }
        catch (SeparationOfDutiesException ex)
        {
            await RecordDenialAsync(actor, $"expense.{action}", id, "separation-of-duties", cancellationToken);
            return Result<ExpenseDetailDto>.Forbidden(ex.Message);
        }
        catch (InvalidExpenseStateException ex)
        {
            store.DiscardChanges();
            return Result<ExpenseDetailDto>.Conflict(ex.Message);
        }
        catch (ArgumentException ex)
        {
            store.DiscardChanges();
            return Result<ExpenseDetailDto>.Invalid(FieldFor(ex), CleanMessage(ex));
        }

        if (decision is not null) store.Add(decision);
        store.Add(Audit(actor, auditAction, expense.Id, AuditOutcome.Succeeded,
            new { from = from.ToString(), to = expense.Status.ToString() }));

        try
        {
            await store.SaveChangesAsync(expense, expectedVersion, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            store.DiscardChanges();
            return Result<ExpenseDetailDto>.PreconditionFailed(
                "Someone else changed the expense at the same time. Reload it and try again.");
        }

        return Result<ExpenseDetailDto>.Ok((await DetailForAsync(actor.User, id, cancellationToken))!);
    }

    private async Task<ExpenseDetailDto?> DetailForAsync(CurrentUserDto user, Guid id, CancellationToken cancellationToken)
    {
        var detail = await reader.GetAsync(id, cancellationToken);
        if (detail is null || !ExpenseAccessPolicy.CanView(user, detail.RequesterId, detail.DepartmentId)) return null;
        var status = Enum.Parse<ExpenseStatus>(detail.Status);
        return detail with { AllowedActions = ExpenseAccessPolicy.AllowedActions(user, detail.RequesterId, detail.DepartmentId, status) };
    }

    private async Task RecordDenialAsync(Actor actor, string action, Guid? resourceId, string reason,
        CancellationToken cancellationToken)
    {
        store.DiscardChanges();
        store.Add(Audit(actor, action, resourceId, AuditOutcome.Denied, new { reason }));
        await store.SaveChangesAsync(null, null, cancellationToken);
    }

    private AuditEvent Audit(Actor actor, string action, Guid? resourceId, AuditOutcome outcome, object metadata) =>
        AuditEvent.Record(clock.GetUtcNow(), actor.User.Id, action, ResourceType, resourceId, outcome,
            actor.CorrelationId, JsonSerializer.Serialize(metadata));

    private static bool TryParseEnum<T>(string? value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        // Reject numeric strings: Enum.TryParse would otherwise accept "7" as a value.
        return !string.IsNullOrWhiteSpace(value) && !value.Trim().All(char.IsAsciiDigit)
            && Enum.TryParse(value.Trim(), ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    private static string CategoryMessage() =>
        $"Category must be one of: {string.Join(", ", Enum.GetNames<ExpenseCategory>())}.";

    private static string FieldFor(ArgumentException ex) =>
        string.IsNullOrEmpty(ex.ParamName) ? "request" : char.ToLowerInvariant(ex.ParamName[0]) + ex.ParamName[1..];

    /// <summary>ArgumentException appends " (Parameter 'x')"; the field name is already in the error key.</summary>
    private static string CleanMessage(ArgumentException ex) =>
        ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal);
}

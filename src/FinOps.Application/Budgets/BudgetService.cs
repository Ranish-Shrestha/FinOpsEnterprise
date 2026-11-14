using System.Text.Json;
using FinOps.Application.Common;
using FinOps.Application.Expenses;
using FinOps.Application.Identity;
using FinOps.Domain.Audit;
using FinOps.Domain.Budgets;

namespace FinOps.Application.Budgets;

public sealed record BudgetDto(Guid Id, Guid DepartmentId, string DepartmentName, int Year, int Month, decimal Amount,
    string Currency, DateTimeOffset UpdatedAtUtc, string Version);

public sealed record CreateBudgetInput(Guid DepartmentId, int Year, int Month, decimal Amount, string? Currency = "USD");

public sealed record UpdateBudgetInput(decimal Amount);

/// <summary>One row of the budget-vs-actual report (one department, one month).</summary>
public sealed record BudgetVsActualRow(Guid DepartmentId, string DepartmentName, int Year, int Month,
    decimal? Budget, decimal Posted, decimal Committed, decimal Pending, decimal? Remaining, decimal? UtilizationPercent);

/// <summary>
/// Departments whose budgets the caller may see, resolved on the server. <c>All</c> means no filter;
/// otherwise only <c>DepartmentIds</c> (possibly empty, which yields nothing).
/// </summary>
public sealed record DepartmentScope(bool All, IReadOnlyCollection<Guid> DepartmentIds);

public interface IBudgetReader
{
    Task<IReadOnlyList<BudgetDto>> ListAsync(int year, DepartmentScope scope, CancellationToken cancellationToken);
    Task<BudgetDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Runs <c>finance.usp_BudgetVsActual</c>. Month null = whole year.</summary>
    Task<IReadOnlyList<BudgetVsActualRow>> BudgetVsActualAsync(int year, int? month, DepartmentScope scope,
        CancellationToken cancellationToken);
}

public interface IBudgetStore
{
    Task<Budget?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(Guid departmentId, int year, int month, CancellationToken cancellationToken);
    Task<bool> DepartmentExistsAsync(Guid departmentId, CancellationToken cancellationToken);
    void Add(Budget budget);
    void Add(AuditEvent auditEvent);

    /// <summary>Saves; throws <see cref="ConcurrencyConflictException"/> on a version mismatch and
    /// <see cref="DuplicateBudgetException"/> if another request created the same department/month first.</summary>
    Task SaveChangesAsync(Budget? budget, byte[]? expectedVersion, CancellationToken cancellationToken);
}

public sealed class DuplicateBudgetException() : Exception("A budget for this department and month already exists.");

/// <summary>
/// Budget use cases. Finance (budgets.manage) sets budgets; managers see their departments' budgets and
/// actuals; finance and administrators see all. Every change is audited in the same transaction.
/// </summary>
public sealed class BudgetService(IBudgetStore store, IBudgetReader reader, TimeProvider clock)
{
    public static DepartmentScope? ScopeFor(CurrentUserDto user) =>
        user.Has(ApplicationPermissions.BudgetsReadAll) ? new DepartmentScope(true, [])
        : user.Has(ApplicationPermissions.BudgetsReadDepartment) ? new DepartmentScope(false, user.DepartmentIds)
        : null;

    public async Task<Result<IReadOnlyList<BudgetDto>>> ListAsync(Actor actor, int year, CancellationToken cancellationToken)
    {
        if (ScopeFor(actor.User) is not { } scope)
            return Result<IReadOnlyList<BudgetDto>>.Forbidden("You do not have access to budgets.");
        if (year is < Budget.MinimumYear or > Budget.MaximumYear)
            return Result<IReadOnlyList<BudgetDto>>.Invalid("year", $"Year must be between {Budget.MinimumYear} and {Budget.MaximumYear}.");
        return Result<IReadOnlyList<BudgetDto>>.Ok(await reader.ListAsync(year, scope, cancellationToken));
    }

    public async Task<Result<IReadOnlyList<BudgetVsActualRow>>> BudgetVsActualAsync(Actor actor, int year, int? month,
        CancellationToken cancellationToken)
    {
        if (ScopeFor(actor.User) is not { } scope)
            return Result<IReadOnlyList<BudgetVsActualRow>>.Forbidden("You do not have access to budget reports.");
        var errors = new Dictionary<string, string[]>();
        if (year is < Budget.MinimumYear or > Budget.MaximumYear) errors["year"] = [$"Year must be between {Budget.MinimumYear} and {Budget.MaximumYear}."];
        if (month is < 1 or > 12) errors["month"] = ["Month must be between 1 and 12."];
        if (errors.Count > 0) return Result<IReadOnlyList<BudgetVsActualRow>>.Invalid(errors);
        return Result<IReadOnlyList<BudgetVsActualRow>>.Ok(await reader.BudgetVsActualAsync(year, month, scope, cancellationToken));
    }

    public async Task<Result<BudgetDto>> CreateAsync(Actor actor, CreateBudgetInput input, CancellationToken cancellationToken)
    {
        if (!await store.DepartmentExistsAsync(input.DepartmentId, cancellationToken))
            return Result<BudgetDto>.Invalid("departmentId", "Unknown department.");

        Budget budget;
        try
        {
            budget = Budget.Create(Guid.NewGuid(), input.DepartmentId, input.Year, input.Month, input.Amount,
                input.Currency ?? "USD", clock.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            return Result<BudgetDto>.Invalid(FieldFor(ex), CleanMessage(ex));
        }

        if (await store.ExistsAsync(input.DepartmentId, input.Year, input.Month, cancellationToken))
            return Result<BudgetDto>.Conflict("A budget for this department and month already exists. Update it instead.");

        store.Add(budget);
        store.Add(Audit(actor, "budget.created", budget.Id, new { budget.Year, budget.Month, amount = budget.Amount }));
        try
        {
            await store.SaveChangesAsync(null, null, cancellationToken);
        }
        catch (DuplicateBudgetException ex)
        {
            return Result<BudgetDto>.Conflict(ex.Message);
        }

        return Result<BudgetDto>.Created((await reader.GetAsync(budget.Id, cancellationToken))!);
    }

    public async Task<Result<BudgetDto>> UpdateAsync(Actor actor, Guid id, string? ifMatch, UpdateBudgetInput input,
        CancellationToken cancellationToken)
    {
        var budget = await store.FindForUpdateAsync(id, cancellationToken);
        if (budget is null) return Result<BudgetDto>.NotFound();

        if (!ExpenseVersion.TryParseIfMatch(ifMatch, out var expected) || !budget.RowVersion.AsSpan().SequenceEqual(expected))
            return Result<BudgetDto>.PreconditionFailed("The budget has changed since you loaded it. Reload it and try again.");

        var previous = budget.Amount;
        try
        {
            budget.ChangeAmount(input.Amount, clock.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            return Result<BudgetDto>.Invalid(FieldFor(ex), CleanMessage(ex));
        }

        store.Add(Audit(actor, "budget.updated", budget.Id, new { from = previous, to = budget.Amount }));
        try
        {
            await store.SaveChangesAsync(budget, expected, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Result<BudgetDto>.PreconditionFailed("Someone else changed the budget at the same time. Reload it and try again.");
        }

        return Result<BudgetDto>.Ok((await reader.GetAsync(id, cancellationToken))!);
    }

    private AuditEvent Audit(Actor actor, string action, Guid id, object metadata) =>
        AuditEvent.Record(clock.GetUtcNow(), actor.User.Id, action, "budget", id, AuditOutcome.Succeeded,
            actor.CorrelationId, JsonSerializer.Serialize(metadata));

    private static string FieldFor(ArgumentException ex) =>
        string.IsNullOrEmpty(ex.ParamName) ? "request" : char.ToLowerInvariant(ex.ParamName[0]) + ex.ParamName[1..];

    private static string CleanMessage(ArgumentException ex) =>
        ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal);
}

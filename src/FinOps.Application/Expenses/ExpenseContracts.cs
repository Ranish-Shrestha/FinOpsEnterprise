using FinOps.Application.Common;
using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;

namespace FinOps.Application.Expenses;

public sealed record ExpenseInput(decimal Amount, string Currency, string Category, string Description, DateOnly ExpenseDate);

public sealed record CreateExpenseInput(Guid DepartmentId, decimal Amount, string Currency, string Category,
    string Description, DateOnly ExpenseDate);

public sealed record DecisionInput(string? Comment);

public sealed record ExpenseSummaryDto(Guid Id, Guid RequesterId, string RequesterName, Guid DepartmentId,
    string DepartmentName, decimal Amount, string Currency, string Category, string Description, DateOnly ExpenseDate,
    string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? SubmittedAtUtc);

public sealed record ApprovalDecisionDto(Guid ApproverId, string ApproverName, string Outcome, string? Comment,
    DateTimeOffset DecidedAtUtc);

public sealed record ExpenseDetailDto(Guid Id, Guid RequesterId, string RequesterName, Guid DepartmentId,
    string DepartmentName, decimal Amount, string Currency, string Category, string Description, DateOnly ExpenseDate,
    string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? DecidedAtUtc, DateTimeOffset? PostedAtUtc, IReadOnlyList<ApprovalDecisionDto> Decisions,
    IReadOnlyList<string> AllowedActions, string Version);

public sealed record ExpenseListQuery(string? Status = null, Guid? DepartmentId = null, bool Mine = false,
    int Page = 1, int PageSize = Paging.DefaultPageSize, string? Sort = null);

public sealed record AuditEventDto(long Id, DateTimeOffset OccurredAtUtc, Guid? ActorUserId, string? ActorName,
    string Action, string ResourceType, Guid? ResourceId, string Outcome, string? CorrelationId, string? Metadata);

/// <summary>Server-resolved caller context for list filtering. Never built from client input.</summary>
public sealed record ExpenseVisibility(Guid UserId, bool ReadAll, bool ReadDepartment, IReadOnlyCollection<Guid> DepartmentIds);

/// <summary>Sort fields clients may request. Anything else is a 400.</summary>
public enum ExpenseSortField { CreatedAt, SubmittedAt, ExpenseDate, Amount }

public sealed record ExpenseSort(ExpenseSortField Field, bool Descending)
{
    public static readonly ExpenseSort Default = new(ExpenseSortField.CreatedAt, true);

    public static bool TryParse(string? value, out ExpenseSort sort)
    {
        sort = Default;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var descending = value.StartsWith('-');
        var name = descending ? value[1..] : value;
        if (!Enum.TryParse<ExpenseSortField>(name, ignoreCase: true, out var field) || int.TryParse(name, out _)) return false;
        sort = new ExpenseSort(field, descending);
        return true;
    }
}

public interface IExpenseReader
{
    Task<PagedResult<ExpenseSummaryDto>> ListAsync(ExpenseVisibility visibility, ExpenseStatus? status,
        Guid? departmentId, bool mineOnly, int page, int pageSize, ExpenseSort sort, CancellationToken cancellationToken);

    /// <summary>Unfiltered read; callers apply <see cref="ExpenseAccessPolicy"/> before returning it.</summary>
    Task<ExpenseDetailDto?> GetAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Write-side persistence for the expense workflow. One SaveChanges per command keeps state and audit atomic.</summary>
public interface IExpenseStore
{
    Task<ExpenseRequest?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken);
    void Add(ExpenseRequest expense);
    void Add(ApprovalDecision decision);
    void Add(AuditEvent auditEvent);

    /// <summary>
    /// Saves pending changes. When <paramref name="expectedVersion"/> is given, the update succeeds only if the
    /// row still has that version; otherwise throws <see cref="ConcurrencyConflictException"/>.
    /// </summary>
    Task SaveChangesAsync(ExpenseRequest? expense, byte[]? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Discards pending entity changes (used before recording a denial on its own).</summary>
    void DiscardChanges();
}

public interface IAuditReader
{
    Task<PagedResult<AuditEventDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken);
}

public sealed class ConcurrencyConflictException() : Exception("The expense was changed by someone else.");

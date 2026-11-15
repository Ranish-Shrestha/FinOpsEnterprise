using FinOps.Application.Common;
using FinOps.Application.Expenses;
using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;
using FinOps.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Persistence;

internal sealed class ExpenseStore(FinOpsDbContext db) : IExpenseStore
{
    public Task<ExpenseRequest?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        db.ExpenseRequests.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public void Add(ExpenseRequest expense) => db.ExpenseRequests.Add(expense);
    public void Add(ApprovalDecision decision) => db.ApprovalDecisions.Add(decision);
    public void Add(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);
    public void Add(OutboxMessage message) => db.OutboxMessages.Add(message);

    public async Task SaveChangesAsync(ExpenseRequest? expense, byte[]? expectedVersion, CancellationToken cancellationToken)
    {
        if (expense is not null && expectedVersion is not null)
        {
            // The UPDATE becomes "... WHERE Id = @id AND RowVersion = @expected". Zero rows → concurrency conflict.
            db.Entry(expense).Property(x => x.RowVersion).OriginalValue = expectedVersion;
        }

        try
        {
            // SaveChanges wraps all pending inserts/updates (state, decision, audit) in one transaction.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException();
        }
    }

    public void DiscardChanges() => db.ChangeTracker.Clear();
}

/// <summary>
/// Query construction for expense lists, shared by the reader and the query-plan tests so the tests
/// measure exactly the SQL production sends.
/// </summary>
internal static class ExpenseQueries
{
    /// <summary>
    /// Applies the visibility rule (mirrors ExpenseAccessPolicy.CanView). The caller's flags are evaluated
    /// here in C#, so each kind of caller gets a simple, index-friendly WHERE clause instead of one
    /// catch-all predicate with parameterized OR branches (see docs/performance/expense-queries.md).
    /// </summary>
    internal static IQueryable<ExpenseRequest> VisibleTo(this IQueryable<ExpenseRequest> query, ExpenseVisibility visibility)
    {
        var userId = visibility.UserId;
        if (visibility.ReadAll) return query;
        if (visibility.ReadDepartment && visibility.DepartmentIds.Count > 0)
        {
            var departmentIds = visibility.DepartmentIds.ToList();
            return query.Where(e => e.RequesterId == userId || departmentIds.Contains(e.DepartmentId));
        }

        return query.Where(e => e.RequesterId == userId);
    }

    internal static IQueryable<ExpenseRequest> Filtered(FinOpsDbContext db, ExpenseVisibility visibility,
        ExpenseStatus? status, Guid? departmentId, bool mineOnly)
    {
        // Authorization first; user filters can only narrow it.
        var query = db.ExpenseRequests.AsNoTracking().VisibleTo(visibility);
        if (mineOnly) query = query.Where(e => e.RequesterId == visibility.UserId);
        if (status is { } s) query = query.Where(e => e.Status == s);
        if (departmentId is { } d) query = query.Where(e => e.DepartmentId == d);
        return query;
    }

    /// <summary>
    /// Orders and pages. The Id tie-breaker follows the sort direction so a backward index range read
    /// satisfies the ORDER BY without a sort (mixed directions forced SQL Server to sort every match).
    /// </summary>
    internal static IQueryable<ExpenseRequest> Page(IQueryable<ExpenseRequest> query, ExpenseSort sort, int page, int pageSize)
    {
        IOrderedQueryable<ExpenseRequest> ordered = (sort.Field, sort.Descending) switch
        {
            (ExpenseSortField.Amount, false) => query.OrderBy(e => e.Amount),
            (ExpenseSortField.Amount, true) => query.OrderByDescending(e => e.Amount),
            (ExpenseSortField.ExpenseDate, false) => query.OrderBy(e => e.ExpenseDate),
            (ExpenseSortField.ExpenseDate, true) => query.OrderByDescending(e => e.ExpenseDate),
            (ExpenseSortField.SubmittedAt, false) => query.OrderBy(e => e.SubmittedAtUtc),
            (ExpenseSortField.SubmittedAt, true) => query.OrderByDescending(e => e.SubmittedAtUtc),
            (ExpenseSortField.CreatedAt, false) => query.OrderBy(e => e.CreatedAtUtc),
            _ => query.OrderByDescending(e => e.CreatedAtUtc),
        };
        ordered = sort.Descending ? ordered.ThenByDescending(e => e.Id) : ordered.ThenBy(e => e.Id);
        return ordered.Skip((page - 1) * pageSize).Take(pageSize);
    }

    /// <summary>
    /// Step 1 of two-step paging: only the IDs of the requested page. With the covering indexes this reads
    /// the narrow nonclustered index alone; step 2 then fetches just those rows by primary key.
    /// </summary>
    internal static IQueryable<Guid> PageIds(IQueryable<ExpenseRequest> query, ExpenseSort sort, int page, int pageSize) =>
        Page(query, sort, page, pageSize).Select(e => e.Id);
}

internal sealed class ExpenseReader(FinOpsDbContext db) : IExpenseReader
{
    public async Task<PagedResult<ExpenseSummaryDto>> ListAsync(ExpenseVisibility visibility, ExpenseStatus? status,
        Guid? departmentId, bool mineOnly, int page, int pageSize, ExpenseSort sort, CancellationToken cancellationToken)
    {
        var query = ExpenseQueries.Filtered(db, visibility, status, departmentId, mineOnly);
        var total = await query.CountAsync(cancellationToken);

        var ids = await ExpenseQueries.PageIds(query, sort, page, pageSize).ToListAsync(cancellationToken);
        var rows = await (
                from e in db.ExpenseRequests.AsNoTracking()
                where ids.Contains(e.Id)
                join u in db.UserProfiles on e.RequesterId equals u.Id
                join dep in db.Departments on e.DepartmentId equals dep.Id
                select new { e, RequesterName = u.DisplayName, DepartmentName = dep.Name })
            .ToListAsync(cancellationToken);

        var position = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var items = rows.OrderBy(r => position[r.e.Id]).Select(r => new ExpenseSummaryDto(r.e.Id, r.e.RequesterId, r.RequesterName, r.e.DepartmentId,
            r.DepartmentName, r.e.Amount, r.e.Currency, r.e.Category.ToString(), r.e.Description, r.e.ExpenseDate,
            r.e.Status.ToString(), r.e.CreatedAtUtc, r.e.SubmittedAtUtc)).ToList();

        return new PagedResult<ExpenseSummaryDto>(items, page, pageSize, total);
    }

    public async Task<ExpenseDetailDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await (
                from e in db.ExpenseRequests.AsNoTracking()
                where e.Id == id
                join u in db.UserProfiles on e.RequesterId equals u.Id
                join dep in db.Departments on e.DepartmentId equals dep.Id
                select new { e, RequesterName = u.DisplayName, DepartmentName = dep.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        var decisions = await (
                from d in db.ApprovalDecisions.AsNoTracking()
                where d.ExpenseRequestId == id
                join u in db.UserProfiles on d.ApproverId equals u.Id
                orderby d.DecidedAtUtc
                select new { d, ApproverName = u.DisplayName })
            .ToListAsync(cancellationToken);

        var reimbursedAt = await db.Reimbursements.AsNoTracking().Where(r => r.ExpenseRequestId == id)
            .Select(r => (DateTimeOffset?)r.ScheduledAtUtc).SingleOrDefaultAsync(cancellationToken);

        var x = row.e;
        return new ExpenseDetailDto(x.Id, x.RequesterId, row.RequesterName, x.DepartmentId, row.DepartmentName,
            x.Amount, x.Currency, x.Category.ToString(), x.Description, x.ExpenseDate, x.Status.ToString(),
            x.CreatedAtUtc, x.UpdatedAtUtc, x.SubmittedAtUtc, x.DecidedAtUtc, x.PostedAtUtc,
            decisions.Select(r => new ApprovalDecisionDto(r.d.ApproverId, r.ApproverName, r.d.Outcome.ToString(),
                r.d.Comment, r.d.DecidedAtUtc)).ToList(),
            AllowedActions: [],
            Version: ExpenseVersion.ToETag(x.RowVersion),
            ReimbursementScheduledAtUtc: reimbursedAt);
    }
}

internal sealed class AuditReader(FinOpsDbContext db) : IAuditReader
{
    public async Task<PagedResult<AuditEventDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var total = await db.AuditEvents.CountAsync(cancellationToken);
        var rows = await (
                from a in db.AuditEvents.AsNoTracking().OrderByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)
                join u in db.UserProfiles on a.ActorUserId equals u.Id into actors
                from actor in actors.DefaultIfEmpty()
                select new { a, ActorName = actor == null ? null : actor.DisplayName })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new AuditEventDto(r.a.Id, r.a.OccurredAtUtc, r.a.ActorUserId, r.ActorName,
            r.a.Action, r.a.ResourceType, r.a.ResourceId, r.a.Outcome.ToString(), r.a.CorrelationId, r.a.Metadata)).ToList();
        return new PagedResult<AuditEventDto>(items, page, pageSize, total);
    }
}

using FinOps.Application.Common;
using FinOps.Application.Expenses;
using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Persistence;

internal sealed class ExpenseStore(FinOpsDbContext db) : IExpenseStore
{
    public Task<ExpenseRequest?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        db.ExpenseRequests.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public void Add(ExpenseRequest expense) => db.ExpenseRequests.Add(expense);
    public void Add(ApprovalDecision decision) => db.ApprovalDecisions.Add(decision);
    public void Add(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);

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

internal sealed class ExpenseReader(FinOpsDbContext db) : IExpenseReader
{
    public async Task<PagedResult<ExpenseSummaryDto>> ListAsync(ExpenseVisibility visibility, ExpenseStatus? status,
        Guid? departmentId, bool mineOnly, int page, int pageSize, ExpenseSort sort, CancellationToken cancellationToken)
    {
        var userId = visibility.UserId;
        var readAll = visibility.ReadAll;
        var readDepartment = visibility.ReadDepartment;
        var departmentIds = visibility.DepartmentIds.ToList();

        // Authorization first (mirrors ExpenseAccessPolicy.CanView); user filters can only narrow it.
        var query = db.ExpenseRequests.AsNoTracking().Where(e =>
            e.RequesterId == userId || readAll || (readDepartment && departmentIds.Contains(e.DepartmentId)));

        if (mineOnly) query = query.Where(e => e.RequesterId == userId);
        if (status is { } s) query = query.Where(e => e.Status == s);
        if (departmentId is { } d) query = query.Where(e => e.DepartmentId == d);

        var total = await query.CountAsync(cancellationToken);

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

        var rows = await (
                from e in ordered.ThenBy(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize)
                join u in db.UserProfiles on e.RequesterId equals u.Id
                join dep in db.Departments on e.DepartmentId equals dep.Id
                select new { e, RequesterName = u.DisplayName, DepartmentName = dep.Name })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new ExpenseSummaryDto(r.e.Id, r.e.RequesterId, r.RequesterName, r.e.DepartmentId,
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

        var x = row.e;
        return new ExpenseDetailDto(x.Id, x.RequesterId, row.RequesterName, x.DepartmentId, row.DepartmentName,
            x.Amount, x.Currency, x.Category.ToString(), x.Description, x.ExpenseDate, x.Status.ToString(),
            x.CreatedAtUtc, x.UpdatedAtUtc, x.SubmittedAtUtc, x.DecidedAtUtc, x.PostedAtUtc,
            decisions.Select(r => new ApprovalDecisionDto(r.d.ApproverId, r.ApproverName, r.d.Outcome.ToString(),
                r.d.Comment, r.d.DecidedAtUtc)).ToList(),
            AllowedActions: [],
            Version: ExpenseVersion.ToETag(x.RowVersion));
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

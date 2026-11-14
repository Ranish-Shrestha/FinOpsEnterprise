using System.Data;
using System.Text.Json;
using FinOps.Application.Budgets;
using FinOps.Application.Reports;
using FinOps.Domain.Audit;
using FinOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Reporting;

/// <summary>
/// Calls the reporting stored procedures. Every input is a typed parameter; the department scope is the
/// server-resolved JSON array (NULL only for callers allowed to see all departments).
/// </summary>
internal sealed class ReportReader(FinOpsDbContext db) : IReportReader
{
    public async Task<IReadOnlyList<ExpenseSummaryRow>> ExpenseSummaryAsync(DateOnly from, DateOnly to, DepartmentScope scope,
        CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<SummaryRecord>(
                "EXEC finance.usp_ExpenseSummary @From, @To, @DepartmentIds",
                Date("@From", from), Date("@To", to), Scope(scope))
            .ToListAsync(ct);
        return rows.Select(r => new ExpenseSummaryRow(r.DepartmentId, r.DepartmentName, r.Category, r.ExpenseCount,
            r.Posted, r.Approved, r.Pending, r.Total)).ToList();
    }

    public async Task<IReadOnlyList<ApprovalAgingRow>> ApprovalAgingAsync(DateTimeOffset asOfUtc, DepartmentScope scope,
        CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<AgingRecord>(
                "EXEC finance.usp_ApprovalAging @AsOfUtc, @DepartmentIds",
                new SqlParameter("@AsOfUtc", SqlDbType.DateTimeOffset) { Value = asOfUtc }, Scope(scope))
            .ToListAsync(ct);
        return rows.Select(r => new ApprovalAgingRow(r.ExpenseId, r.DepartmentId, r.DepartmentName, r.RequesterName,
            r.Description, r.Amount, r.SubmittedAtUtc, r.AgeDays, r.AgeBucket)).ToList();
    }

    public async Task<IReadOnlyList<LedgerRow>> TransactionLedgerAsync(DateOnly from, DateOnly to, DepartmentScope scope,
        int maxRows, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<LedgerRecord>(
                "EXEC finance.usp_TransactionLedger @From, @To, @DepartmentIds, @MaxRows",
                Date("@From", from), Date("@To", to), Scope(scope), Int("@MaxRows", maxRows))
            .ToListAsync(ct);
        return rows.Select(r => new LedgerRow(r.PostedAtUtc, r.ExpenseId, r.DepartmentId, r.DepartmentName, r.RequesterName,
            r.Category, r.Description, r.Amount, r.Currency, r.PostedByName)).ToList();
    }

    public async Task<IReadOnlyList<AuditActivityRow>> AuditActivityAsync(DateOnly from, DateOnly to, int maxRows,
        CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<AuditRecord>(
                "EXEC audit.usp_AuditActivity @From, @To, @MaxRows",
                Date("@From", from), Date("@To", to), Int("@MaxRows", maxRows))
            .ToListAsync(ct);
        return rows.Select(r => new AuditActivityRow(r.OccurredAtUtc, r.ActorName, r.Action, r.ResourceType, r.ResourceId,
            r.Outcome, r.CorrelationId)).ToList();
    }

    private static SqlParameter Date(string name, DateOnly value) =>
        new(name, SqlDbType.Date) { Value = value.ToDateTime(TimeOnly.MinValue) };

    private static SqlParameter Int(string name, int value) => new(name, SqlDbType.Int) { Value = value };

    private static SqlParameter Scope(DepartmentScope scope) =>
        new("@DepartmentIds", SqlDbType.NVarChar, -1)
        {
            Value = scope.All ? DBNull.Value : JsonSerializer.Serialize(scope.DepartmentIds)
        };

    // Column shapes of the procedures (SqlQueryRaw maps by property name).
    internal sealed class SummaryRecord
    {
        public Guid DepartmentId { get; set; }
        public string DepartmentName { get; set; } = "";
        public string Category { get; set; } = "";
        public int ExpenseCount { get; set; }
        public decimal Posted { get; set; }
        public decimal Approved { get; set; }
        public decimal Pending { get; set; }
        public decimal Total { get; set; }
    }

    internal sealed class AgingRecord
    {
        public Guid ExpenseId { get; set; }
        public Guid DepartmentId { get; set; }
        public string DepartmentName { get; set; } = "";
        public string RequesterName { get; set; } = "";
        public string Description { get; set; } = "";
        public decimal Amount { get; set; }
        public DateTimeOffset SubmittedAtUtc { get; set; }
        public int AgeDays { get; set; }
        public string AgeBucket { get; set; } = "";
    }

    internal sealed class LedgerRecord
    {
        public DateTimeOffset PostedAtUtc { get; set; }
        public Guid ExpenseId { get; set; }
        public Guid DepartmentId { get; set; }
        public string DepartmentName { get; set; } = "";
        public string RequesterName { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "";
        public string PostedByName { get; set; } = "";
    }

    internal sealed class AuditRecord
    {
        public DateTimeOffset OccurredAtUtc { get; set; }
        public string? ActorName { get; set; }
        public string Action { get; set; } = "";
        public string ResourceType { get; set; } = "";
        public Guid? ResourceId { get; set; }
        public string Outcome { get; set; } = "";
        public string? CorrelationId { get; set; }
    }
}

internal sealed class AuditWriter(FinOpsDbContext db) : IAuditWriter
{
    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(cancellationToken);
    }
}

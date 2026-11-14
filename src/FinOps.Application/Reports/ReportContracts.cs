using FinOps.Application.Budgets;
using FinOps.Domain.Audit;

namespace FinOps.Application.Reports;

public enum ColumnKind { Text, Date, DateTime, Money, Integer, Percent }

/// <summary>A report column. <c>Total</c> columns get a totals row in PDF and Excel.</summary>
public sealed record ReportColumn(string Key, string Header, ColumnKind Kind, bool Total = false);

public sealed record ReportParameterValue(string Label, string Value);

/// <summary>
/// A rendered-format-independent report: the same object is returned as JSON (preview) and rendered to
/// PDF and Excel, so every format shows exactly the same rows.
/// </summary>
public sealed record ReportTable(
    string Key,
    string Title,
    string Description,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlyList<ReportParameterValue> Parameters,
    string Scope,
    string GeneratedBy,
    DateTimeOffset GeneratedAtUtc);

public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

public sealed record ReportParameterInfo(string Name, string Kind, bool Required, string Description);

public sealed record ReportCatalogEntry(string Key, string Title, string Description,
    IReadOnlyList<ReportParameterInfo> Parameters, IReadOnlyList<string> Formats);

/// <summary>Raw query parameters as received; validated by <see cref="ReportService"/>.</summary>
public sealed record ReportRequest(string Key, string? Format, DateOnly? From, DateOnly? To, int? Year, int? Month);

public sealed record ExpenseSummaryRow(Guid DepartmentId, string DepartmentName, string Category, int ExpenseCount,
    decimal Posted, decimal Approved, decimal Pending, decimal Total);

public sealed record ApprovalAgingRow(Guid ExpenseId, Guid DepartmentId, string DepartmentName, string RequesterName,
    string Description, decimal Amount, DateTimeOffset SubmittedAtUtc, int AgeDays, string AgeBucket);

public sealed record LedgerRow(DateTimeOffset PostedAtUtc, Guid ExpenseId, Guid DepartmentId, string DepartmentName,
    string RequesterName, string Category, string Description, decimal Amount, string Currency, string PostedByName);

public sealed record AuditActivityRow(DateTimeOffset OccurredAtUtc, string? ActorName, string Action, string ResourceType,
    Guid? ResourceId, string Outcome, string? CorrelationId);

/// <summary>Report data from the reporting stored procedures. Scope is always resolved by the server.</summary>
public interface IReportReader
{
    Task<IReadOnlyList<ExpenseSummaryRow>> ExpenseSummaryAsync(DateOnly from, DateOnly to, DepartmentScope scope, CancellationToken ct);
    Task<IReadOnlyList<ApprovalAgingRow>> ApprovalAgingAsync(DateTimeOffset asOfUtc, DepartmentScope scope, CancellationToken ct);

    /// <summary>Returns at most <paramref name="maxRows"/> + 1 rows, so the caller can detect truncation.</summary>
    Task<IReadOnlyList<LedgerRow>> TransactionLedgerAsync(DateOnly from, DateOnly to, DepartmentScope scope, int maxRows, CancellationToken ct);

    /// <summary>Returns at most <paramref name="maxRows"/> + 1 rows, so the caller can detect truncation.</summary>
    Task<IReadOnlyList<AuditActivityRow>> AuditActivityAsync(DateOnly from, DateOnly to, int maxRows, CancellationToken ct);
}

public interface IReportRenderer
{
    string Format { get; }
    string ContentType { get; }
    string Extension { get; }
    byte[] Render(ReportTable report);
}

public interface IAuditWriter
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

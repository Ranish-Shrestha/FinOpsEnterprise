using System.Globalization;
using System.Text.Json;
using FinOps.Application.Budgets;
using FinOps.Application.Common;
using FinOps.Application.Expenses;
using FinOps.Application.Identity;
using FinOps.Domain.Audit;

namespace FinOps.Application.Reports;

public static class ReportKeys
{
    public const string ExpenseSummary = "expense-summary";
    public const string BudgetVsActual = "budget-vs-actual";
    public const string ApprovalAging = "approval-aging";
    public const string TransactionLedger = "transaction-ledger";
    public const string AuditActivity = "audit-activity";
}

/// <summary>
/// Runs the five reports. Authorization happens here, before any data is read: the caller's permission
/// decides which reports they may run, and their server-resolved department scope is passed to the stored
/// procedure. Nothing in the request can widen the scope. Every successful export is audited.
/// </summary>
public sealed class ReportService(IReportReader reader, IBudgetReader budgets, IEnumerable<IReportRenderer> renderers,
    IAuditWriter audit, TimeProvider clock)
{
    public const int MaximumRows = 5_000;
    public const int MaximumRangeDays = 366;
    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames;

    private sealed record Definition(string Key, string Title, string Description, string? Permission,
        IReadOnlyList<ReportParameterInfo> Parameters);

    private static readonly ReportParameterInfo FromParameter = new("from", "date", false, "First day (inclusive). Default: first day of the current month.");
    private static readonly ReportParameterInfo ToParameter = new("to", "date", false, "Last day (inclusive). Default: today.");

    private static readonly IReadOnlyList<Definition> Definitions =
    [
        new(ReportKeys.ExpenseSummary, "Department expense summary",
            "Submitted, approved and posted spend by department and category, by expense date.", null,
            [FromParameter, ToParameter]),
        new(ReportKeys.BudgetVsActual, "Budget vs. actual",
            "Monthly budget against posted, committed and pending spend.", null,
            [new("year", "year", false, "Default: current year."), new("month", "month", false, "1-12. Omit for the whole year.")]),
        new(ReportKeys.ApprovalAging, "Pending approval aging",
            "Submitted expenses awaiting a decision, oldest first, bucketed by age.", null, []),
        new(ReportKeys.TransactionLedger, "Transaction ledger",
            "Posted expenses by posting date.", null, [FromParameter, ToParameter]),
        new(ReportKeys.AuditActivity, "Audit activity",
            "Workflow actions and denied attempts. Restricted to audit readers.", ApplicationPermissions.AuditRead,
            [FromParameter, ToParameter]),
    ];

    public IReadOnlyList<string> Formats => ["json", .. renderers.Select(r => r.Format)];

    /// <summary>Department scope for reports; null if the caller may not run reports at all.</summary>
    public static DepartmentScope? ScopeFor(CurrentUserDto user) =>
        user.Has(ApplicationPermissions.ReportsReadAll) ? new DepartmentScope(true, [])
        : user.Has(ApplicationPermissions.ReportsReadDepartment) ? new DepartmentScope(false, user.DepartmentIds)
        : null;

    public IReadOnlyList<ReportCatalogEntry> Catalog(CurrentUserDto user) =>
        Definitions.Where(d => CanRun(user, d))
            .Select(d => new ReportCatalogEntry(d.Key, d.Title, d.Description, d.Parameters, Formats))
            .ToList();

    /// <summary>Returns a <see cref="ReportTable"/> for JSON, or a <see cref="ReportFile"/> for PDF/Excel.</summary>
    public async Task<Result<object>> RunAsync(Actor actor, ReportRequest request, CancellationToken ct)
    {
        var definition = Definitions.FirstOrDefault(d => d.Key == request.Key);
        if (definition is null) return Result<object>.NotFound();
        if (!CanRun(actor.User, definition))
            return Result<object>.Forbidden("You do not have access to this report.");

        var format = (request.Format ?? "json").ToLowerInvariant();
        var renderer = renderers.FirstOrDefault(r => r.Format == format);
        if (format != "json" && renderer is null)
            return Result<object>.Invalid("format", $"Format must be one of: {string.Join(", ", Formats)}.");

        var scope = ScopeFor(actor.User) ?? new DepartmentScope(false, []);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var errors = new Dictionary<string, string[]>();

        ReportTable? table = definition.Key switch
        {
            ReportKeys.ExpenseSummary when ValidRange(request, today, errors) is var (from, to) && errors.Count == 0 =>
                await ExpenseSummaryAsync(definition, actor, scope, from, to, ct),
            ReportKeys.BudgetVsActual when ValidPeriod(request, today, errors) is var (year, month) && errors.Count == 0 =>
                await BudgetVsActualAsync(definition, actor, scope, year, month, ct),
            ReportKeys.ApprovalAging => await ApprovalAgingAsync(definition, actor, scope, ct),
            ReportKeys.TransactionLedger when ValidRange(request, today, errors) is var (from, to) && errors.Count == 0 =>
                await LedgerAsync(definition, actor, scope, from, to, errors, ct),
            ReportKeys.AuditActivity when ValidRange(request, today, errors) is var (from, to) && errors.Count == 0 =>
                await AuditAsync(definition, actor, from, to, errors, ct),
            _ => null
        };

        if (errors.Count > 0 || table is null) return Result<object>.Invalid(errors);

        await audit.RecordAsync(AuditEvent.Record(clock.GetUtcNow(), actor.User.Id, "report.exported", "report", null,
            AuditOutcome.Succeeded, actor.CorrelationId,
            JsonSerializer.Serialize(new { report = table.Key, format, rows = table.Rows.Count })), ct);

        if (renderer is null) return Result<object>.Ok(table);
        var fileName = $"{table.Key}-{table.GeneratedAtUtc:yyyyMMdd-HHmm}.{renderer.Extension}";
        return Result<object>.Ok(new ReportFile(renderer.Render(table), renderer.ContentType, fileName));
    }

    private static bool CanRun(CurrentUserDto user, Definition definition) =>
        definition.Permission is { } permission ? user.Has(permission) : ScopeFor(user) is not null;

    private async Task<ReportTable> ExpenseSummaryAsync(Definition d, Actor actor, DepartmentScope scope,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await reader.ExpenseSummaryAsync(from, to, scope, ct);
        return Table(d, actor, scope,
            [Col("department", "Department"), Col("category", "Category"), new("count", "Expenses", ColumnKind.Integer, true),
             Money("posted", "Posted"), Money("approved", "Approved"), Money("pending", "Pending"), Money("total", "Total")],
            rows.Select(r => Row(("department", r.DepartmentName), ("category", r.Category), ("count", r.ExpenseCount),
                ("posted", r.Posted), ("approved", r.Approved), ("pending", r.Pending), ("total", r.Total))),
            [new("From", Iso(from)), new("To", Iso(to))]);
    }

    private async Task<ReportTable> BudgetVsActualAsync(Definition d, Actor actor, DepartmentScope scope, int year,
        int? month, CancellationToken ct)
    {
        var rows = await budgets.BudgetVsActualAsync(year, month, scope, ct);
        return Table(d, actor, scope,
            [Col("department", "Department"), Col("month", "Month"), Money("budget", "Budget"), Money("posted", "Spent"),
             Money("committed", "Committed"), Money("pending", "Pending"), Money("remaining", "Remaining"),
             new("utilization", "Used %", ColumnKind.Percent)],
            rows.Select(r => Row(("department", r.DepartmentName), ("month", MonthNames[r.Month - 1]), ("budget", r.Budget),
                ("posted", r.Posted), ("committed", r.Committed), ("pending", r.Pending), ("remaining", r.Remaining),
                ("utilization", r.UtilizationPercent))),
            [new("Year", year.ToString(CultureInfo.InvariantCulture)), new("Month", month is { } m ? MonthNames[m - 1] : "All")]);
    }

    private async Task<ReportTable> ApprovalAgingAsync(Definition d, Actor actor, DepartmentScope scope, CancellationToken ct)
    {
        var asOf = clock.GetUtcNow();
        var rows = await reader.ApprovalAgingAsync(asOf, scope, ct);
        return Table(d, actor, scope,
            [Col("department", "Department"), Col("requester", "Requester"), Col("description", "Description"),
             Money("amount", "Amount"), new("submitted", "Submitted", ColumnKind.DateTime),
             new("ageDays", "Age (days)", ColumnKind.Integer), Col("bucket", "Age band")],
            rows.Select(r => Row(("department", r.DepartmentName), ("requester", r.RequesterName), ("description", r.Description),
                ("amount", r.Amount), ("submitted", r.SubmittedAtUtc), ("ageDays", r.AgeDays), ("bucket", r.AgeBucket))),
            [new("As of", asOf.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))]);
    }

    private async Task<ReportTable?> LedgerAsync(Definition d, Actor actor, DepartmentScope scope, DateOnly from, DateOnly to,
        Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var rows = await reader.TransactionLedgerAsync(from, to, scope, MaximumRows, ct);
        if (TooMany(rows.Count, errors)) return null;
        return Table(d, actor, scope,
            [new("posted", "Posted", ColumnKind.DateTime), Col("department", "Department"), Col("requester", "Requester"),
             Col("category", "Category"), Col("description", "Description"), Money("amount", "Amount"), Col("postedBy", "Posted by")],
            rows.Select(r => Row(("posted", r.PostedAtUtc), ("department", r.DepartmentName), ("requester", r.RequesterName),
                ("category", r.Category), ("description", r.Description), ("amount", r.Amount), ("postedBy", r.PostedByName))),
            [new("Posted from", Iso(from)), new("Posted to", Iso(to))]);
    }

    private async Task<ReportTable?> AuditAsync(Definition d, Actor actor, DateOnly from, DateOnly to,
        Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var rows = await reader.AuditActivityAsync(from, to, MaximumRows, ct);
        if (TooMany(rows.Count, errors)) return null;
        return Table(d, actor, new DepartmentScope(true, []),
            [new("time", "Time (UTC)", ColumnKind.DateTime), Col("actor", "Actor"), Col("action", "Action"),
             Col("resource", "Resource"), Col("outcome", "Outcome"), Col("correlationId", "Correlation ID")],
            rows.Select(r => Row(("time", r.OccurredAtUtc), ("actor", r.ActorName ?? "(system)"), ("action", r.Action),
                ("resource", r.ResourceId is { } id ? $"{r.ResourceType} {id}" : r.ResourceType), ("outcome", r.Outcome),
                ("correlationId", r.CorrelationId))),
            [new("From", Iso(from)), new("To", Iso(to))]);
    }

    private static bool TooMany(int count, Dictionary<string, string[]> errors)
    {
        if (count <= MaximumRows) return false;
        errors["to"] = [$"The report has more than {MaximumRows:N0} rows. Narrow the date range."];
        return true;
    }

    private static (DateOnly From, DateOnly To) ValidRange(ReportRequest request, DateOnly today, Dictionary<string, string[]> errors)
    {
        var from = request.From ?? new DateOnly(today.Year, today.Month, 1);
        var to = request.To ?? today;
        if (from.Year < 2000 || to.Year > 2100) errors["from"] = ["Dates must be between 2000 and 2100."];
        else if (to < from) errors["to"] = ["'to' must be on or after 'from'."];
        else if (to.DayNumber - from.DayNumber + 1 > MaximumRangeDays) errors["to"] = [$"The range can be at most {MaximumRangeDays} days."];
        return (from, to);
    }

    private static (int Year, int? Month) ValidPeriod(ReportRequest request, DateOnly today, Dictionary<string, string[]> errors)
    {
        var year = request.Year ?? today.Year;
        if (year is < 2000 or > 2100) errors["year"] = ["Year must be between 2000 and 2100."];
        if (request.Month is < 1 or > 12) errors["month"] = ["Month must be between 1 and 12."];
        return (year, request.Month);
    }

    private ReportTable Table(Definition d, Actor actor, DepartmentScope scope, IReadOnlyList<ReportColumn> columns,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<ReportParameterValue> parameters) =>
        new(d.Key, d.Title, d.Description, columns, rows.ToList(), parameters,
            scope.All ? "All departments" : $"{scope.DepartmentIds.Count} department(s) you are a member of",
            actor.User.DisplayName, clock.GetUtcNow());

    private static ReportColumn Col(string key, string header) => new(key, header, ColumnKind.Text);
    private static ReportColumn Money(string key, string header) => new(key, header, ColumnKind.Money, true);
    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value);
}

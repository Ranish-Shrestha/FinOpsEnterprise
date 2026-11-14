using FinOps.Application.Budgets;
using FinOps.Application.Common;
using FinOps.Application.Expenses;
using FinOps.Application.Identity;
using FinOps.Application.Reports;
using FinOps.Domain.Audit;

namespace FinOps.UnitTests;

public sealed class ReportServiceTests
{
    private static readonly Guid Engineering = Guid.NewGuid();

    private sealed class FakeReader : IReportReader
    {
        public DepartmentScope? LastScope;
        public int LedgerRows;

        public Task<IReadOnlyList<ExpenseSummaryRow>> ExpenseSummaryAsync(DateOnly from, DateOnly to, DepartmentScope scope, CancellationToken ct)
        {
            LastScope = scope;
            return Task.FromResult<IReadOnlyList<ExpenseSummaryRow>>([new(Engineering, "Demo Engineering", "Travel", 2, 10m, 5m, 1m, 16m)]);
        }

        public Task<IReadOnlyList<ApprovalAgingRow>> ApprovalAgingAsync(DateTimeOffset asOfUtc, DepartmentScope scope, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ApprovalAgingRow>>([]);

        public Task<IReadOnlyList<LedgerRow>> TransactionLedgerAsync(DateOnly from, DateOnly to, DepartmentScope scope, int maxRows, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LedgerRow>>(Enumerable.Range(0, LedgerRows).Select(_ =>
                new LedgerRow(DateTimeOffset.UtcNow, Guid.NewGuid(), Engineering, "E", "R", "Travel", "D", 1m, "USD", "F")).ToList());

        public Task<IReadOnlyList<AuditActivityRow>> AuditActivityAsync(DateOnly from, DateOnly to, int maxRows, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AuditActivityRow>>([]);
    }

    private sealed class FakeBudgets : IBudgetReader
    {
        public Task<IReadOnlyList<BudgetDto>> ListAsync(int year, DepartmentScope scope, CancellationToken ct) => throw new NotSupportedException();
        public Task<BudgetDto?> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BudgetVsActualRow>> BudgetVsActualAsync(int year, int? month, DepartmentScope scope, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BudgetVsActualRow>>([]);
    }

    private sealed class FakeRenderer : IReportRenderer
    {
        public string Format => "pdf";
        public string ContentType => "application/pdf";
        public string Extension => "pdf";
        public byte[] Render(ReportTable report) => [1, 2, 3];
    }

    private sealed class FakeAudit : IAuditWriter
    {
        public readonly List<AuditEvent> Events = [];
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken ct) { Events.Add(auditEvent); return Task.CompletedTask; }
    }

    private static (ReportService Service, FakeReader Reader, FakeAudit Audit) Create()
    {
        var reader = new FakeReader();
        var audit = new FakeAudit();
        return (new ReportService(reader, new FakeBudgets(), [new FakeRenderer()], audit, TimeProvider.System), reader, audit);
    }

    private static Actor ActorWith(params string[] permissions) =>
        new(new CurrentUserDto(Guid.NewGuid(), "Tester", "t@example.invalid", [], permissions, [Engineering]), "corr-1");

    private static ReportRequest Request(string key, string? format = null, DateOnly? from = null, DateOnly? to = null) =>
        new(key, format, from, to, null, null);

    [Fact]
    public void Catalog_depends_on_permissions()
    {
        var (service, _, _) = Create();
        Assert.Empty(service.Catalog(ActorWith(ApplicationPermissions.ExpensesCreate).User));
        Assert.Equal(4, service.Catalog(ActorWith(ApplicationPermissions.ReportsReadDepartment).User).Count);
        Assert.Equal(5, service.Catalog(ActorWith(ApplicationPermissions.ReportsReadAll, ApplicationPermissions.AuditRead).User).Count);
        Assert.Equal(["json", "pdf"], service.Formats);
    }

    [Fact]
    public async Task Department_readers_are_scoped_to_their_memberships()
    {
        var (service, reader, _) = Create();
        await service.RunAsync(ActorWith(ApplicationPermissions.ReportsReadDepartment), Request(ReportKeys.ExpenseSummary), default);
        Assert.False(reader.LastScope!.All);
        Assert.Equal([Engineering], reader.LastScope.DepartmentIds);

        await service.RunAsync(ActorWith(ApplicationPermissions.ReportsReadAll), Request(ReportKeys.ExpenseSummary), default);
        Assert.True(reader.LastScope!.All);
    }

    [Fact]
    public async Task Callers_without_access_are_refused_before_any_data_is_read()
    {
        var (service, reader, audit) = Create();
        var result = await service.RunAsync(ActorWith(ApplicationPermissions.ExpensesCreate), Request(ReportKeys.ExpenseSummary), default);
        Assert.Equal(ResultKind.Forbidden, result.Kind);
        Assert.Null(reader.LastScope);
        Assert.Empty(audit.Events);

        var auditReport = await service.RunAsync(ActorWith(ApplicationPermissions.ReportsReadAll), Request(ReportKeys.AuditActivity), default);
        Assert.Equal(ResultKind.Forbidden, auditReport.Kind);
    }

    [Fact]
    public async Task Json_returns_the_table_and_files_are_rendered_and_audited()
    {
        var (service, _, audit) = Create();
        var actor = ActorWith(ApplicationPermissions.ReportsReadAll);

        var json = await service.RunAsync(actor, Request(ReportKeys.ExpenseSummary), default);
        var table = Assert.IsType<ReportTable>(json.Value);
        Assert.Equal(16m, table.Rows.Single()["total"]);
        Assert.Equal("All departments", table.Scope);

        var pdf = await service.RunAsync(actor, Request(ReportKeys.ExpenseSummary, "PDF"), default);
        var file = Assert.IsType<ReportFile>(pdf.Value);
        Assert.EndsWith(".pdf", file.FileName);

        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, e => Assert.Equal("report.exported", e.Action));
        Assert.Contains("\"format\":\"pdf\"", audit.Events[1].Metadata);
        Assert.Equal("corr-1", audit.Events[1].CorrelationId);
    }

    [Fact]
    public async Task Invalid_parameters_and_formats_are_rejected()
    {
        var (service, _, audit) = Create();
        var actor = ActorWith(ApplicationPermissions.ReportsReadAll);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.Equal(ResultKind.Invalid, (await service.RunAsync(actor, Request(ReportKeys.ExpenseSummary, "docx"), default)).Kind);
        Assert.Equal(ResultKind.Invalid, (await service.RunAsync(actor, Request(ReportKeys.ExpenseSummary, from: today, to: today.AddDays(-1)), default)).Kind);
        Assert.Equal(ResultKind.Invalid, (await service.RunAsync(actor, Request(ReportKeys.ExpenseSummary, from: today.AddDays(-400), to: today), default)).Kind);
        Assert.Equal(ResultKind.Invalid, (await service.RunAsync(actor, new ReportRequest(ReportKeys.BudgetVsActual, null, null, null, 2026, 13), default)).Kind);
        Assert.Equal(ResultKind.NotFound, (await service.RunAsync(actor, Request("salaries"), default)).Kind);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Ledger_over_the_row_limit_asks_for_a_narrower_range()
    {
        var (service, reader, _) = Create();
        reader.LedgerRows = ReportService.MaximumRows + 1;
        var result = await service.RunAsync(ActorWith(ApplicationPermissions.ReportsReadAll), Request(ReportKeys.TransactionLedger), default);
        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Contains("Narrow the date range", result.Errors!["to"][0]);
    }
}

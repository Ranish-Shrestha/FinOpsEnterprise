using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using FinOps.Application.Reports;
using FinOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOps.IntegrationTests;

/// <summary>
/// Phase 4 reports against real SQL Server: exact figures on an isolated department, department isolation
/// in the JSON preview <em>and inside the exported Excel file</em>, restrictions, validation and auditing.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class ReportIntegrationTests(SqlServerFixture fixture)
{
    private static readonly string Today = TestData.Today.ToString("yyyy-MM-dd");

    private HttpClient As(DemoPersona persona) =>
        ApiFactory.CreateHttpsClient(fixture.Factory, persona.EntraObjectId.ToString());

    private static async Task<JsonElement[]> RowsAsync(HttpClient client, string url)
    {
        var report = await client.GetFromJsonAsync<JsonElement>(url);
        return report.GetProperty("rows").EnumerateArray().ToArray();
    }

    private static string Text(JsonElement row, string key) => row.GetProperty(key).GetString()!;

    [Fact]
    public async Task Catalog_lists_only_reports_the_caller_may_run()
    {
        async Task<string[]> Keys(DemoPersona persona)
        {
            using var client = As(persona);
            return (await client.GetFromJsonAsync<ReportCatalogEntry[]>("/api/reports"))!.Select(r => r.Key).ToArray();
        }

        string[] financial = [ReportKeys.ExpenseSummary, ReportKeys.BudgetVsActual, ReportKeys.ApprovalAging, ReportKeys.TransactionLedger];
        Assert.Empty(await Keys(DemoDataSeeder.Employee));
        Assert.Equal(financial, await Keys(DemoDataSeeder.Manager));
        Assert.Equal(financial, await Keys(DemoDataSeeder.FinanceOfficer));
        var adminKeys = await Keys(DemoDataSeeder.Administrator);
        Assert.Equal(financial.Append(ReportKeys.AuditActivity), adminKeys);
    }

    [Fact]
    public async Task Expense_summary_is_exact_and_ignores_drafts_and_cancelled()
    {
        var name = await NameOfAsync(await TestData.IsolatedDepartmentWithSpendAsync(fixture));
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        var rows = await RowsAsync(finance, $"/api/reports/expense-summary?from={Today}&to={Today}");

        var row = Assert.Single(rows, r => Text(r, "department") == name);
        Assert.Equal("Supplies", Text(row, "category"));
        Assert.Equal(3, row.GetProperty("count").GetInt32());          // posted + approved + submitted
        Assert.Equal(100m, row.GetProperty("posted").GetDecimal());
        Assert.Equal(50m, row.GetProperty("approved").GetDecimal());
        Assert.Equal(25m, row.GetProperty("pending").GetDecimal());
        Assert.Equal(175m, row.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Aging_and_ledger_show_the_expected_rows()
    {
        var name = await NameOfAsync(await TestData.IsolatedDepartmentWithSpendAsync(fixture));
        using var finance = As(DemoDataSeeder.FinanceOfficer);

        var aging = Assert.Single(await RowsAsync(finance, "/api/reports/approval-aging"), r => Text(r, "department") == name);
        Assert.Equal(25m, aging.GetProperty("amount").GetDecimal());
        Assert.Equal("0-2 days", Text(aging, "bucket"));
        Assert.Equal("Demo Employee", Text(aging, "requester"));

        // The ledger is by POSTING date: both postings happened today, including the one whose expense
        // date is last month. Approved, submitted, draft and cancelled expenses never appear.
        var ledger = (await RowsAsync(finance, $"/api/reports/transaction-ledger?from={Today}&to={Today}"))
            .Where(r => Text(r, "department") == name).ToArray();
        Assert.Equal([10m, 100m], ledger.Select(r => r.GetProperty("amount").GetDecimal()).Order());
        Assert.All(ledger, r => Assert.Equal("Demo Finance Officer", Text(r, "postedBy")));
    }

    [Theory]
    [InlineData(ReportKeys.ExpenseSummary)]
    [InlineData(ReportKeys.ApprovalAging)]
    [InlineData(ReportKeys.TransactionLedger)]
    [InlineData(ReportKeys.BudgetVsActual)]
    public async Task Manager_reports_contain_only_their_department_even_in_excel(string key)
    {
        await TestData.IsolatedDepartmentWithSpendAsync(fixture); // other departments definitely have data
        using var manager = As(DemoDataSeeder.Manager);            // Finance department only
        var range = key is ReportKeys.ExpenseSummary or ReportKeys.TransactionLedger
            ? $"from={TestData.Today.AddDays(-365):yyyy-MM-dd}&to={Today}" : "";

        Assert.All(await RowsAsync(manager, $"/api/reports/{key}?{range}"),
            r => Assert.Equal("Demo Finance", Text(r, "department")));

        var response = await manager.GetAsync($"/api/reports/{key}?format=xlsx&{range}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync());
        var cells = workbook.Worksheet(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.DoesNotContain(cells, v => v.Contains("Demo Engineering") || v.Contains("Demo Operations") || v.StartsWith("Isolated"));
    }

    [Fact]
    public async Task Exports_are_files_that_are_never_cached_and_are_audited()
    {
        using var finance = As(DemoDataSeeder.FinanceOfficer);

        var pdf = await finance.GetAsync($"/api/reports/expense-summary?format=pdf&from={Today}&to={Today}");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("expense-summary-", pdf.Content.Headers.ContentDisposition?.FileNameStar ?? pdf.Content.Headers.ContentDisposition?.FileName);
        Assert.True(pdf.Headers.CacheControl?.NoStore);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        var xlsx = await finance.GetAsync("/api/reports/budget-vs-actual?format=xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType?.MediaType);

        var exports = await fixture.WithDbAsync(db => db.AuditEvents
            .Where(a => a.Action == "report.exported" && a.ActorUserId == DemoDataSeeder.FinanceOfficer.UserId)
            .Select(a => a.Metadata!).ToListAsync());
        Assert.Contains(exports, m => m.Contains("\"format\":\"pdf\"") && m.Contains("expense-summary"));
        Assert.Contains(exports, m => m.Contains("\"format\":\"xlsx\"") && m.Contains("budget-vs-actual"));
    }

    [Fact]
    public async Task Audit_activity_is_restricted_to_audit_readers()
    {
        using var manager = As(DemoDataSeeder.Manager);
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        using var employee = As(DemoDataSeeder.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/reports/audit-activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.GetAsync("/api/reports/audit-activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/reports/expense-summary")).StatusCode);

        await finance.GetAsync("/api/reports/approval-aging"); // creates a report.exported event today
        using var admin = As(DemoDataSeeder.Administrator);
        var rows = await RowsAsync(admin, $"/api/reports/audit-activity?from={Today}&to={Today}");
        Assert.Contains(rows, r => Text(r, "action") == "report.exported");
    }

    [Theory]
    [InlineData("/api/reports/expense-summary?from=2026-05-02&to=2026-05-01", "to")]
    [InlineData("/api/reports/expense-summary?from=2024-01-01&to=2026-01-01", "to")]
    [InlineData("/api/reports/budget-vs-actual?month=13", "month")]
    [InlineData("/api/reports/budget-vs-actual?format=docx", "format")]
    public async Task Invalid_parameters_are_400(string url, string field)
    {
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        var response = await finance.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Unknown_reports_are_404()
    {
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        Assert.Equal(HttpStatusCode.NotFound, (await finance.GetAsync("/api/reports/salaries")).StatusCode);
    }

    [Fact]
    public async Task Procedures_validate_their_own_parameters()
    {
        await fixture.WithDbAsync(async db =>
        {
            var range = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "EXEC finance.usp_TransactionLedger @From = '2026-02-01', @To = '2026-01-01'"));
            Assert.Equal(50004, range.Number);
            var rows = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "EXEC audit.usp_AuditActivity @From = '2026-01-01', @To = '2026-01-02', @MaxRows = 0"));
            Assert.Equal(50006, rows.Number);
            var json = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "EXEC finance.usp_ExpenseSummary @From = '2026-01-01', @To = '2026-01-02', @DepartmentIds = N'not json'"));
            Assert.Equal(50003, json.Number);
            return 0;
        });
    }

    [Fact]
    public async Task Rdl_datasets_call_procedures_that_exist_with_matching_parameters()
    {
        // The SSRS .rdl files are not rendered in CI (SSRS runs only on Windows), but their data contract is
        // checked here: every dataset's procedure exists and every query parameter is a real procedure parameter.
        var datasets = RdlFiles.Datasets().ToList();
        Assert.Equal(5, datasets.Select(d => d.File).Distinct().Count());

        var parameters = await fixture.WithDbAsync(db => db.Database.SqlQueryRaw<ProcedureParameter>("""
            SELECT SCHEMA_NAME(p.schema_id) + '.' + p.name AS ProcedureName, pr.name AS ParameterName
            FROM sys.procedures AS p JOIN sys.parameters AS pr ON pr.object_id = p.object_id
            """).ToListAsync());

        foreach (var dataset in datasets)
        {
            var known = parameters.Where(p => p.ProcedureName == dataset.Procedure).Select(p => p.ParameterName).ToHashSet();
            Assert.True(known.Count > 0, $"{dataset.File}: procedure {dataset.Procedure} does not exist.");
            foreach (var parameter in dataset.Parameters)
                Assert.Contains(parameter, known);
        }
    }

    private Task<string> NameOfAsync(Guid departmentId) => fixture.WithDbAsync(db =>
        db.Departments.Where(d => d.Id == departmentId).Select(d => d.Name).SingleAsync());

    internal sealed class ProcedureParameter
    {
        public string ProcedureName { get; set; } = "";
        public string ParameterName { get; set; } = "";
    }
}

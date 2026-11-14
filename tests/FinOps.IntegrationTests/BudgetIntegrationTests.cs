using System.Net;
using System.Net.Http.Json;
using FinOps.Application.Budgets;
using FinOps.Domain.Budgets;
using FinOps.Domain.Expenses;
using FinOps.Domain.Organization;
using FinOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOps.IntegrationTests;

/// <summary>
/// Budgets and the budget-vs-actual stored procedure against real SQL Server. Arithmetic is checked on a
/// department created only for this test, so other tests' data cannot change the expected totals.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class BudgetIntegrationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private HttpClient As(DemoPersona persona) =>
        ApiFactory.CreateHttpsClient(fixture.Factory, persona.EntraObjectId.ToString());

    /// <summary>Creates an isolated department with a known budget and one expense in each relevant state.</summary>
    private async Task<Guid> IsolatedDepartmentWithSpendAsync(decimal budget = 400m)
    {
        return await fixture.WithDbAsync(async db =>
        {
            var department = Department.Create($"Budget test {Guid.NewGuid():N}"[..30]);
            db.Departments.Add(department);
            db.Budgets.Add(Budget.Create(Guid.NewGuid(), department.Id, Today.Year, Today.Month, budget, "USD", Now));

            ExpenseRequest Expense(decimal amount, DateOnly date)
            {
                var e = ExpenseRequest.CreateDraft(Guid.NewGuid(), DemoDataSeeder.Employee.UserId, department.Id, amount,
                    "USD", ExpenseCategory.Supplies, "Budget test", date, Now);
                db.ExpenseRequests.Add(e);
                return e;
            }

            var posted = Expense(100m, Today);
            posted.Submit(Now); db.ApprovalDecisions.Add(posted.Approve(DemoDataSeeder.Manager.UserId, null, Now));
            posted.Post(DemoDataSeeder.FinanceOfficer.UserId, Now);

            var approved = Expense(50m, Today);
            approved.Submit(Now); db.ApprovalDecisions.Add(approved.Approve(DemoDataSeeder.Manager.UserId, null, Now));

            Expense(25m, Today).Submit(Now);          // pending: shown, not subtracted
            Expense(999m, Today);                     // draft: excluded
            Expense(888m, Today).Cancel(Now);         // cancelled: excluded

            var lastMonth = Today.AddMonths(-1);       // other period: excluded from this month
            var old = Expense(10m, lastMonth);
            old.Submit(Now); db.ApprovalDecisions.Add(old.Approve(DemoDataSeeder.Manager.UserId, null, Now));
            old.Post(DemoDataSeeder.FinanceOfficer.UserId, Now);

            await db.SaveChangesAsync();
            return department.Id;
        });
    }

    [Fact]
    public async Task Stored_procedure_computes_budget_vs_actual_exactly()
    {
        var departmentId = await IsolatedDepartmentWithSpendAsync();
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        var rows = await finance.GetFromJsonAsync<BudgetVsActualRow[]>(
            $"/api/reports/budget-vs-actual?year={Today.Year}&month={Today.Month}");

        var row = Assert.Single(rows!, r => r.DepartmentId == departmentId);
        Assert.Equal(Today.Month, row.Month);
        Assert.Equal(400m, row.Budget);
        Assert.Equal(100m, row.Posted);
        Assert.Equal(50m, row.Committed);
        Assert.Equal(25m, row.Pending);
        Assert.Equal(250m, row.Remaining);           // 400 - 100 - 50; pending is not subtracted
        Assert.Equal(37.5m, row.UtilizationPercent); // (100 + 50) / 400
    }

    [Fact]
    public async Task Unbudgeted_spend_is_reported_with_no_budget()
    {
        var departmentId = await IsolatedDepartmentWithSpendAsync();
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        var lastMonth = Today.AddMonths(-1);
        var rows = await finance.GetFromJsonAsync<BudgetVsActualRow[]>(
            $"/api/reports/budget-vs-actual?year={lastMonth.Year}&month={lastMonth.Month}");

        var row = Assert.Single(rows!, r => r.DepartmentId == departmentId);
        Assert.Null(row.Budget);
        Assert.Null(row.Remaining);
        Assert.Null(row.UtilizationPercent);
        Assert.Equal(10m, row.Posted);
    }

    [Fact]
    public async Task Report_scope_is_resolved_on_the_server()
    {
        var isolated = await IsolatedDepartmentWithSpendAsync();
        var url = $"/api/reports/budget-vs-actual?year={Today.Year}";

        using var financeManager = As(DemoDataSeeder.Manager);   // budgets.read.department, member of Finance only
        var managerRows = await financeManager.GetFromJsonAsync<BudgetVsActualRow[]>(url);
        Assert.NotEmpty(managerRows!);
        Assert.All(managerRows!, r => Assert.Equal(DemoDataSeeder.FinanceDepartment, r.DepartmentId));

        using var admin = As(DemoDataSeeder.Administrator);      // budgets.read.all
        var adminRows = await admin.GetFromJsonAsync<BudgetVsActualRow[]>(url);
        Assert.Contains(adminRows!, r => r.DepartmentId == isolated);
        Assert.Contains(adminRows!, r => r.DepartmentId == DemoDataSeeder.EngineeringDepartment);

        using var employee = As(DemoDataSeeder.Employee);        // no budget permission
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/budgets")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/reports/budget-vs-actual?year={Today.Year}&month=13")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/reports/budget-vs-actual?year=1999")).StatusCode);
    }

    [Fact]
    public async Task Stored_procedure_validates_its_own_parameters()
    {
        // Defense in depth: the procedure rejects bad input even if called outside the API.
        await fixture.WithDbAsync(async db =>
        {
            var badJson = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "EXEC finance.usp_BudgetVsActual @Year = 2026, @Month = NULL, @DepartmentIds = N'[''; DROP TABLE finance.Budgets; --'"));
            Assert.Equal(50003, badJson.Number);
            var badMonth = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "EXEC finance.usp_BudgetVsActual @Year = 2026, @Month = 13"));
            Assert.Equal(50002, badMonth.Number);
            Assert.True(await db.Budgets.AnyAsync()); // still there
            return 0;
        });
    }

    [Fact]
    public async Task Finance_manages_budgets_with_versions_and_audit()
    {
        var departmentId = await fixture.WithDbAsync(async db =>
        {
            var d = Department.Create($"Budget CRUD {Guid.NewGuid():N}"[..30]);
            db.Departments.Add(d);
            await db.SaveChangesAsync();
            return d.Id;
        });
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        var create = new { departmentId, year = 2099, month = 6, amount = 1234.56m };

        var created = await finance.PostAsJsonAsync("/api/budgets", create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var budget = (await created.Content.ReadFromJsonAsync<BudgetDto>())!;
        Assert.Equal($"/api/budgets/{budget.Id}", created.Headers.Location?.OriginalString);
        Assert.Equal(budget.Version, created.Headers.ETag?.Tag);

        Assert.Equal(HttpStatusCode.Conflict, (await finance.PostAsJsonAsync("/api/budgets", create)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await finance.PostAsJsonAsync("/api/budgets", create with { month = 13 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await finance.PostAsJsonAsync("/api/budgets", create with { departmentId = Guid.NewGuid() })).StatusCode);

        Assert.Equal((HttpStatusCode)428, (await finance.PutAsJsonAsync($"/api/budgets/{budget.Id}", new { amount = 2000m })).StatusCode);

        async Task<HttpResponseMessage> Put(string version, decimal amount)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/budgets/{budget.Id}") { Content = JsonContent.Create(new { amount }) };
            request.Headers.TryAddWithoutValidation("If-Match", version);
            return await finance.SendAsync(request);
        }

        var updated = await Put(budget.Version, 2000m);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2000m, (await updated.Content.ReadFromJsonAsync<BudgetDto>())!.Amount);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Put(budget.Version, 3000m)).StatusCode);

        var audit = await fixture.WithDbAsync(db => db.AuditEvents.Where(a => a.ResourceId == budget.Id)
            .OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
        Assert.Equal(["budget.created", "budget.updated"], audit);
    }

    [Fact]
    public async Task Only_finance_can_change_budgets_and_managers_see_their_departments()
    {
        using var manager = As(DemoDataSeeder.Manager);
        var create = new { departmentId = DemoDataSeeder.FinanceDepartment, year = 2098, month = 1, amount = 10m };
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/budgets", create)).StatusCode);
        using var admin = As(DemoDataSeeder.Administrator);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/budgets", create)).StatusCode);

        var visible = await manager.GetFromJsonAsync<BudgetDto[]>($"/api/budgets?year={Today.Year}");
        Assert.Equal(12, visible!.Length); // the seeded Finance budgets, one per month
        Assert.All(visible, b => Assert.Equal(DemoDataSeeder.FinanceDepartment, b.DepartmentId));
    }
}

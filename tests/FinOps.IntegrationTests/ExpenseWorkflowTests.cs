using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinOps.Application.Common;
using FinOps.Application.Expenses;
using FinOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOps.IntegrationTests;

/// <summary>
/// End-to-end expense workflow through HTTP against real SQL Server. Each test creates its own expenses,
/// so tests are independent of order and of the shared database's other contents.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class ExpenseWorkflowTests(SqlServerFixture fixture)
{
    private static readonly DateOnly Yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

    private HttpClient As(DemoPersona persona) =>
        ApiFactory.CreateHttpsClient(fixture.Factory, persona.EntraObjectId.ToString());

    private static object NewExpense(Guid departmentId, decimal amount = 42.50m, string currency = "USD",
        string category = "Travel", DateOnly? date = null, string description = "Synthetic test expense") =>
        new { departmentId, amount, currency, category, description, expenseDate = date ?? Yesterday };

    private async Task<ExpenseDetailDto> CreateAsync(DemoPersona persona, Guid? departmentId = null)
    {
        using var client = As(persona);
        var response = await client.PostAsJsonAsync("/api/expenses",
            NewExpense(departmentId ?? DemoDataSeeder.EngineeringDepartment));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
    }

    private async Task<HttpResponseMessage> ActAsync(DemoPersona persona, Guid id, string action, string? etag,
        string? comment = null)
    {
        using var client = As(persona);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/expenses/{id}/{action}");
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (comment is not null) request.Content = JsonContent.Create(new { comment });
        return await client.SendAsync(request);
    }

    /// <summary>Performs an action that must succeed and returns the updated detail (with the new ETag).</summary>
    private async Task<ExpenseDetailDto> MustAsync(DemoPersona persona, ExpenseDetailDto expense, string action, string? comment = null)
    {
        var response = await ActAsync(persona, expense.Id, action, expense.Version, comment);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var detail = (await response.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
        Assert.Equal(detail.Version, response.Headers.ETag?.Tag);
        return detail;
    }

    private async Task<ExpenseDetailDto> SubmittedByEmployeeAsync() =>
        await MustAsync(DemoDataSeeder.Employee, await CreateAsync(DemoDataSeeder.Employee), ExpenseActions.Submit);

    private Task<List<string>> AuditActionsAsync(Guid expenseId) => fixture.WithDbAsync(db => db.AuditEvents
        .Where(a => a.ResourceId == expenseId).OrderBy(a => a.Id)
        .Select(a => a.Action + ":" + a.Outcome.ToString()).ToListAsync());

    [Fact]
    public async Task Full_lifecycle_employee_to_manager_to_finance()
    {
        var created = await CreateAsync(DemoDataSeeder.Employee);
        Assert.Equal("Draft", created.Status);
        Assert.Equal([ExpenseActions.Edit, ExpenseActions.Submit, ExpenseActions.Cancel], created.AllowedActions);

        var submitted = await MustAsync(DemoDataSeeder.Employee, created, ExpenseActions.Submit);

        // The manager sees it in their department's approval queue.
        using (var manager = As(DemoDataSeeder.EngineeringManager))
        {
            var queue = await manager.GetFromJsonAsync<PagedResult<ExpenseSummaryDto>>("/api/expenses?status=Submitted&pageSize=100");
            Assert.Contains(queue!.Items, e => e.Id == created.Id);
            var managerView = await manager.GetFromJsonAsync<ExpenseDetailDto>($"/api/expenses/{created.Id}");
            Assert.Equal([ExpenseActions.Approve, ExpenseActions.Reject], managerView!.AllowedActions);
        }

        var approved = await MustAsync(DemoDataSeeder.EngineeringManager, submitted, ExpenseActions.Approve, "Within policy.");
        Assert.Equal("Approved", approved.Status);
        var decision = Assert.Single(approved.Decisions);
        Assert.Equal("Demo Engineering Manager", decision.ApproverName);

        var posted = await MustAsync(DemoDataSeeder.FinanceOfficer, approved, ExpenseActions.Post);
        Assert.Equal("Posted", posted.Status);
        Assert.NotNull(posted.PostedAtUtc);
        Assert.Empty(posted.AllowedActions);

        Assert.Equal(["expense.created:Succeeded", "expense.submitted:Succeeded", "expense.approved:Succeeded", "expense.posted:Succeeded"],
            await AuditActionsAsync(created.Id));
    }

    [Fact]
    public async Task Manager_cannot_approve_their_own_expense()
    {
        var own = await MustAsync(DemoDataSeeder.EngineeringManager,
            await CreateAsync(DemoDataSeeder.EngineeringManager), ExpenseActions.Submit);
        Assert.DoesNotContain(ExpenseActions.Approve, own.AllowedActions);

        var response = await ActAsync(DemoDataSeeder.EngineeringManager, own.Id, ExpenseActions.Approve, own.Version);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("own expense", await response.Content.ReadAsStringAsync());
        Assert.Contains("expense.approve:Denied", await AuditActionsAsync(own.Id));
    }

    [Fact]
    public async Task Finance_cannot_post_their_own_expense()
    {
        var own = await MustAsync(DemoDataSeeder.FinanceOfficer,
            await CreateAsync(DemoDataSeeder.FinanceOfficer, DemoDataSeeder.FinanceDepartment), ExpenseActions.Submit);
        var approved = await MustAsync(DemoDataSeeder.Manager, own, ExpenseActions.Approve); // Finance department's manager
        var response = await ActAsync(DemoDataSeeder.FinanceOfficer, approved.Id, ExpenseActions.Post, approved.Version);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Manager_of_another_department_gets_404_not_403()
    {
        var submitted = await SubmittedByEmployeeAsync(); // Engineering
        using var financeManager = As(DemoDataSeeder.Manager);
        Assert.Equal(HttpStatusCode.NotFound, (await financeManager.GetAsync($"/api/expenses/{submitted.Id}")).StatusCode);
        var approve = await ActAsync(DemoDataSeeder.Manager, submitted.Id, ExpenseActions.Approve, submitted.Version);
        Assert.Equal(HttpStatusCode.NotFound, approve.StatusCode);
        Assert.Contains("expense.approve:Denied", await AuditActionsAsync(submitted.Id));
    }

    [Fact]
    public async Task Employees_cannot_read_or_change_a_colleagues_expense()
    {
        var managersExpense = await CreateAsync(DemoDataSeeder.EngineeringManager); // same department as the employee
        using var employee = As(DemoDataSeeder.Employee);
        Assert.Equal(HttpStatusCode.NotFound, (await employee.GetAsync($"/api/expenses/{managersExpense.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ActAsync(DemoDataSeeder.Employee, managersExpense.Id, ExpenseActions.Submit, managersExpense.Version)).StatusCode);

        // Tampered identifier: a random ID behaves exactly like an invisible one.
        Assert.Equal(HttpStatusCode.NotFound, (await employee.GetAsync($"/api/expenses/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Permissions_gate_actions_before_resource_rules()
    {
        var submitted = await SubmittedByEmployeeAsync();
        // Employee lacks expenses.approve; Administrator can read everything but neither approves nor posts.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(DemoDataSeeder.Employee, submitted.Id, ExpenseActions.Approve, submitted.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(DemoDataSeeder.Administrator, submitted.Id, ExpenseActions.Approve, submitted.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(DemoDataSeeder.Administrator, submitted.Id, ExpenseActions.Post, submitted.Version)).StatusCode);

        using var admin = As(DemoDataSeeder.Administrator);
        var adminView = await admin.GetFromJsonAsync<ExpenseDetailDto>($"/api/expenses/{submitted.Id}");
        Assert.Empty(adminView!.AllowedActions);

        using var former = As(DemoDataSeeder.FormerEmployee);
        Assert.Equal(HttpStatusCode.Forbidden, (await former.GetAsync("/api/expenses")).StatusCode);
    }

    [Fact]
    public async Task Invalid_transition_is_409()
    {
        var draft = await CreateAsync(DemoDataSeeder.Employee);
        var response = await ActAsync(DemoDataSeeder.EngineeringManager, draft.Id, ExpenseActions.Approve, draft.Version);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Draft", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Writes_require_if_match_and_reject_stale_versions()
    {
        var draft = await CreateAsync(DemoDataSeeder.Employee);
        using var client = As(DemoDataSeeder.Employee);

        var update = new { amount = 50m, currency = "USD", category = "Meals", description = "Updated", expenseDate = Yesterday };
        Assert.Equal((HttpStatusCode)428, (await client.PutAsJsonAsync($"/api/expenses/{draft.Id}", update)).StatusCode);

        using var first = new HttpRequestMessage(HttpMethod.Put, $"/api/expenses/{draft.Id}") { Content = JsonContent.Create(update) };
        first.Headers.TryAddWithoutValidation("If-Match", draft.Version);
        var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(50m, (await firstResponse.Content.ReadFromJsonAsync<ExpenseDetailDto>())!.Amount);

        // Reusing the old ETag is a lost update: refused.
        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/expenses/{draft.Id}") { Content = JsonContent.Create(update) };
        stale.Headers.TryAddWithoutValidation("If-Match", draft.Version);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_approvals_produce_exactly_one_decision()
    {
        var submitted = await SubmittedByEmployeeAsync();

        // Two approval attempts race with the same version. SQL Server's rowversion check lets exactly one win.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            ActAsync(DemoDataSeeder.EngineeringManager, submitted.Id, ExpenseActions.Approve, submitted.Version)));

        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.ApprovalDecisions.CountAsync(d => d.ExpenseRequestId == submitted.Id)));
    }

    [Fact]
    public async Task Rowversion_guard_rejects_the_second_writer_at_the_database()
    {
        // Forces the race the HTTP test may not hit: both writers pass the in-memory version check,
        // so only SQL Server's "WHERE RowVersion = @expected" can stop the second one.
        var submitted = await SubmittedByEmployeeAsync();
        ExpenseVersion.TryParseIfMatch(submitted.Version, out var version);

        await using var scopeA = fixture.Factory.Services.CreateAsyncScope();
        await using var scopeB = fixture.Factory.Services.CreateAsyncScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IExpenseStore>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<IExpenseStore>();
        var a = (await storeA.FindForUpdateAsync(submitted.Id, default))!;
        var b = (await storeB.FindForUpdateAsync(submitted.Id, default))!;

        storeA.Add(a.Approve(DemoDataSeeder.EngineeringManager.UserId, null, DateTimeOffset.UtcNow));
        await storeA.SaveChangesAsync(a, version, default);

        storeB.Add(b.Reject(DemoDataSeeder.EngineeringManager.UserId, "Too late", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => storeB.SaveChangesAsync(b, version, default));

        // The losing transaction rolled back completely: no second decision row.
        var decisions = await fixture.WithDbAsync(db => db.ApprovalDecisions
            .Where(d => d.ExpenseRequestId == submitted.Id).Select(d => d.Outcome.ToString()).ToListAsync());
        Assert.Equal(["Approved"], decisions);
    }

    [Fact]
    public async Task Rejection_needs_a_comment_and_can_be_revised_and_resubmitted()
    {
        var submitted = await SubmittedByEmployeeAsync();
        var noComment = await ActAsync(DemoDataSeeder.EngineeringManager, submitted.Id, ExpenseActions.Reject, submitted.Version);
        Assert.Equal(HttpStatusCode.BadRequest, noComment.StatusCode);
        Assert.Contains("comment", await noComment.Content.ReadAsStringAsync());

        var rejected = await MustAsync(DemoDataSeeder.EngineeringManager, submitted, ExpenseActions.Reject, "Attach the receipt.");
        Assert.Equal("Rejected", rejected.Status);
        var revised = await MustAsync(DemoDataSeeder.Employee, rejected, ExpenseActions.Revise);
        Assert.Equal("Draft", revised.Status);
        var resubmitted = await MustAsync(DemoDataSeeder.Employee, revised, ExpenseActions.Submit);
        Assert.Equal("Submitted", resubmitted.Status);
        Assert.Equal("Rejected", Assert.Single(resubmitted.Decisions).Outcome); // history is kept
    }

    [Theory]
    [InlineData(0, "USD", "Travel", 0, "amount")]
    [InlineData(10.123, "USD", "Travel", 0, "amount")]
    [InlineData(10, "EUR", "Travel", 0, "currency")]
    [InlineData(10, "USD", "Bribes", 0, "category")]
    [InlineData(10, "USD", "7", 0, "category")]
    [InlineData(10, "USD", "Travel", 2, "expenseDate")]
    public async Task Invalid_input_is_400_with_the_field_named(double amount, string currency, string category,
        int daysFromToday, string field)
    {
        using var client = As(DemoDataSeeder.Employee);
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(daysFromToday == 0 ? -1 : daysFromToday);
        var response = await client.PostAsJsonAsync("/api/expenses",
            NewExpense(DemoDataSeeder.EngineeringDepartment, (decimal)amount, currency, category, date));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out _), $"Expected an error for '{field}': {errors}");
    }

    [Fact]
    public async Task Creating_in_a_department_you_do_not_belong_to_is_403()
    {
        using var client = As(DemoDataSeeder.Employee);
        var response = await client.PostAsJsonAsync("/api/expenses", NewExpense(DemoDataSeeder.FinanceDepartment));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Lists_are_filtered_by_visibility_and_validate_paging()
    {
        var engineeringExpense = await SubmittedByEmployeeAsync();
        var financeExpense = await CreateAsync(DemoDataSeeder.FinanceOfficer, DemoDataSeeder.FinanceDepartment);

        async Task<IReadOnlyList<ExpenseSummaryDto>> ListAs(DemoPersona persona, string query)
        {
            using var client = As(persona);
            var page = await client.GetFromJsonAsync<PagedResult<ExpenseSummaryDto>>($"/api/expenses?pageSize=100&{query}");
            Assert.True(page!.TotalCount >= page.Items.Count);
            return page.Items;
        }

        var employeeSees = await ListAs(DemoDataSeeder.Employee, "");
        Assert.All(employeeSees, e => Assert.Equal(DemoDataSeeder.Employee.UserId, e.RequesterId));

        var financeManagerSees = await ListAs(DemoDataSeeder.Manager, "");
        Assert.All(financeManagerSees, e => Assert.True(
            e.DepartmentId == DemoDataSeeder.FinanceDepartment || e.RequesterId == DemoDataSeeder.Manager.UserId));
        Assert.DoesNotContain(financeManagerSees, e => e.Id == engineeringExpense.Id);

        // A department filter can only narrow visibility, never widen it.
        Assert.Empty(await ListAs(DemoDataSeeder.Manager, $"departmentId={DemoDataSeeder.EngineeringDepartment}"));

        var financeSees = await ListAs(DemoDataSeeder.FinanceOfficer, "sort=-amount");
        Assert.Contains(financeSees, e => e.Id == engineeringExpense.Id);
        Assert.Contains(financeSees, e => e.Id == financeExpense.Id);
        Assert.Equal(financeSees.OrderByDescending(e => e.Amount).Select(e => e.Amount), financeSees.Select(e => e.Amount));

        using var client = As(DemoDataSeeder.Employee);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/expenses?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/expenses?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/expenses?sort=description")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/expenses?status=Paid")).StatusCode);

        var firstPage = await client.GetFromJsonAsync<PagedResult<ExpenseSummaryDto>>("/api/expenses?pageSize=1");
        Assert.Single(firstPage!.Items);
        Assert.True(firstPage.TotalCount >= 1);
    }

    [Fact]
    public async Task Audit_trail_is_restricted_to_audit_readers()
    {
        await SubmittedByEmployeeAsync();
        using var employee = As(DemoDataSeeder.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/audit-events")).StatusCode);

        using var admin = As(DemoDataSeeder.Administrator);
        var page = await admin.GetFromJsonAsync<PagedResult<AuditEventDto>>("/api/audit-events?pageSize=10");
        Assert.NotEmpty(page!.Items);
        Assert.All(page.Items, a => Assert.False(string.IsNullOrEmpty(a.CorrelationId)));
        Assert.DoesNotContain(page.Items, a => a.Metadata?.Contains("Synthetic test expense") == true); // no free text
    }

    [Fact]
    public async Task Demo_expenses_cover_each_workflow_state()
    {
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        async Task<string> StatusOf(Guid id) => (await finance.GetFromJsonAsync<ExpenseDetailDto>($"/api/expenses/{id}"))!.Status;
        Assert.Equal("Draft", await StatusOf(DemoDataSeeder.DraftExpense));
        Assert.Equal("Approved", await StatusOf(DemoDataSeeder.ApprovedExpense));
        Assert.Equal("Submitted", await StatusOf(DemoDataSeeder.ManagersOwnExpense));
    }
}

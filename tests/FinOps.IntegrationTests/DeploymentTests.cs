using System.Net;
using System.Net.Http.Json;
using FinOps.Application.Expenses;
using FinOps.Application.Messaging;
using FinOps.Infrastructure;
using FinOps.Infrastructure.Messaging;
using FinOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinOps.IntegrationTests;

/// <summary>
/// The deployed app connects as a least-privilege identity (ADR 0007). These tests create a SQL login, apply exactly the
/// grants the deploy pipeline applies (<see cref="DatabaseAdministration.GrantRuntimeAccessAsync"/>), then run the whole
/// business flow and the worker as that login. A permission the app needs but the grant lacks fails here, not in Azure.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class DeploymentTests(SqlServerFixture fixture)
{
    private const string RuntimeUser = "finops-runtime-test";

    [Fact]
    public async Task The_app_runs_end_to_end_with_only_the_runtime_grants()
    {
        var connectionString = await RuntimeConnectionStringAsync();
        // Server-side errors (for example a missing permission) are captured so a failure says which statement broke.
        var errors = new ErrorCapture();
        await using var host = new SqlServerFixture.DatabaseApiFactory(connectionString);
        await using var api = host.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(errors)));
        HttpClient As(DemoPersona p) => ApiFactory.CreateHttpsClient(api, p.EntraObjectId.ToString());

        using (var anonymous = ApiFactory.CreateHttpsClient(api))
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/ready")).StatusCode);

        // Create, submit, approve, post: inserts and updates in finance, audit and messaging.
        using var employee = As(DemoDataSeeder.Employee);
        var created = await employee.PostAsJsonAsync("/api/expenses", new
        {
            departmentId = DemoDataSeeder.EngineeringDepartment, amount = 19.99m, currency = "USD", category = "Supplies",
            description = "Runtime-permission check", expenseDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"create: {(int)created.StatusCode}. {errors}");
        var expense = (await created.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
        expense = await ActAsync(employee, expense, ExpenseActions.Submit, errors);
        using (var manager = As(DemoDataSeeder.EngineeringManager)) expense = await ActAsync(manager, expense, ExpenseActions.Approve, errors);
        using var finance = As(DemoDataSeeder.FinanceOfficer);
        await ActAsync(finance, expense, ExpenseActions.Post, errors);

        // Stored procedures (EXECUTE) and the audited export (INSERT into audit).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var admin = As(DemoDataSeeder.Administrator);
        foreach (var (client, url) in new[]
                 {
                     (finance, $"/api/reports/budget-vs-actual?year={today.Year}&month={today.Month}"),
                     (finance, "/api/reports/transaction-ledger?format=xlsx"),
                     (admin, "/api/reports/audit-activity"),
                     (admin, "/api/messaging/outbox"),
                 })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}. {errors}");
        }

        // The worker, as the same identity: claim (UPDATE … OUTPUT), handlers, completion.
        await using var worker = WorkerProvider(connectionString);
        var processor = worker.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0) { }

        Assert.Equal(1, await fixture.WithDbAsync(db => db.Reimbursements.CountAsync(r => r.ExpenseRequestId == expense.Id)));
        var inbox = await employee.GetFromJsonAsync<NotificationInboxDto>("/api/notifications");
        var notification = inbox!.Items.First(n => n.ExpenseId == expense.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await employee.PostAsync($"/api/notifications/{notification.Id}/read", null)).StatusCode);
    }

    [Theory]
    [InlineData("DELETE FROM finance.ExpenseRequests WHERE 1 = 0", 229)]
    [InlineData("UPDATE audit.AuditEvents SET Action = Action WHERE 1 = 0", 229)]
    [InlineData("DELETE FROM audit.AuditEvents WHERE 1 = 0", 229)]
    [InlineData("UPDATE [identity].UserRoles SET RoleId = RoleId WHERE 1 = 0", 229)]
    [InlineData("INSERT INTO [identity].UserProfiles (Id) SELECT TOP 0 Id FROM [identity].UserProfiles", 229)]
    [InlineData("CREATE TABLE dbo.Intruder (Id int)", 262)]
    [InlineData("DROP PROCEDURE finance.usp_BudgetVsActual", 3701)]
    public async Task The_runtime_identity_cannot_delete_rewrite_audit_or_change_the_schema(string sql, int expectedError)
    {
        await using var connection = new SqlConnection(await RuntimeConnectionStringAsync());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(expectedError, error.Number);
    }

    [Fact]
    public async Task Granting_validates_its_input_and_needs_an_existing_user_without_a_client_id()
    {
        await fixture.WithDbAsync(async db =>
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                DatabaseAdministration.GrantRuntimeAccessAsync(db, "x]; DROP TABLE finance.Budgets; --"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                DatabaseAdministration.GrantRuntimeAccessAsync(db, "finops-app-id", Guid.Empty));
            var missing = await Assert.ThrowsAsync<SqlException>(() =>
                DatabaseAdministration.GrantRuntimeAccessAsync(db, "no-such-user"));
            Assert.Equal(50010, missing.Number);
            return 0;
        });
    }

    [Fact]
    public async Task Provisioning_a_real_user_is_idempotent_and_refuses_unknown_roles()
    {
        var objectId = Guid.NewGuid();
        await fixture.WithDbAsync(async db =>
        {
            await DatabaseAdministration.ProvisionUserAsync(db, objectId, "Real Person", "real@example.invalid", "Manager", "Demo Engineering");
            await DatabaseAdministration.ProvisionUserAsync(db, objectId, "Real Person (renamed)", "real@example.invalid", "Manager", "Demo Engineering");

            var counts = await db.Database.SqlQueryRaw<int>("""
                SELECT (SELECT COUNT(*) FROM [identity].UserProfiles WHERE EntraObjectId = @oid AND DisplayName = N'Real Person (renamed)')
                     + 10 * (SELECT COUNT(*) FROM [identity].UserRoles ur JOIN [identity].UserProfiles u ON u.Id = ur.UserId WHERE u.EntraObjectId = @oid)
                     + 100 * (SELECT COUNT(*) FROM [identity].DepartmentMemberships m JOIN [identity].UserProfiles u ON u.Id = m.UserId WHERE u.EntraObjectId = @oid) AS Value
                """, new SqlParameter("@oid", objectId)).SingleAsync();
            Assert.Equal(111, counts); // one profile, one role, one membership

            var role = await Assert.ThrowsAsync<SqlException>(() =>
                DatabaseAdministration.ProvisionUserAsync(db, Guid.NewGuid(), "X", "x@example.invalid", "Owner", null));
            Assert.Equal(50101, role.Number);
            var department = await Assert.ThrowsAsync<SqlException>(() =>
                DatabaseAdministration.ProvisionUserAsync(db, Guid.NewGuid(), "X", "x@example.invalid", "Employee", "Nowhere"));
            Assert.Equal(50102, department.Number);
            return 0;
        });
    }

    /// <summary>A SQL login mapped to a user that has exactly the runtime grants. Idempotent across tests.</summary>
    private async Task<string> RuntimeConnectionStringAsync()
    {
        var password = $"Rt-{Guid.NewGuid():N}-Aa1";
        await fixture.WithDbAsync(async db =>
        {
            // Test-only setup: on Azure the user comes FROM EXTERNAL PROVIDER (the app's managed identity) instead.
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @login nvarchar(300) = QUOTENAME(@name), @secret nvarchar(300) = QUOTENAME(@password, ''''), @sql nvarchar(max);
                SET @sql = CASE WHEN SUSER_ID(@name) IS NULL
                    THEN N'CREATE LOGIN ' + @login + N' WITH PASSWORD = ' + @secret + N', CHECK_POLICY = OFF;'
                    ELSE N'ALTER LOGIN ' + @login + N' WITH PASSWORD = ' + @secret + N';' END;
                EXEC (@sql);
                IF DATABASE_PRINCIPAL_ID(@name) IS NULL
                BEGIN
                    SET @sql = N'CREATE USER ' + @login + N' FOR LOGIN ' + @login + N';';
                    EXEC (@sql);
                END
                """, [new SqlParameter("@name", RuntimeUser), new SqlParameter("@password", password)]);
            await DatabaseAdministration.GrantRuntimeAccessAsync(db, RuntimeUser);
            await DatabaseAdministration.GrantRuntimeAccessAsync(db, RuntimeUser); // idempotent
            return 0;
        });

        return new SqlConnectionStringBuilder(fixture.ConnectionString) { UserID = RuntimeUser, Password = password }.ConnectionString;
    }

    private static ServiceProvider WorkerProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:FinOps"] = connectionString }).Build());
        services.AddLogging();
        services.AddInfrastructure();
        return services.BuildServiceProvider();
    }

    private static async Task<ExpenseDetailDto> ActAsync(HttpClient client, ExpenseDetailDto expense, string action,
        ErrorCapture errors)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/expenses/{expense.Id}/{action}");
        request.Headers.TryAddWithoutValidation("If-Match", expense.Version);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode}. {errors}");
        return (await response.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
    }

    /// <summary>Collects the API's error logs, including the innermost exception message.</summary>
    private sealed class ErrorCapture : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> lines = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{formatter(state, exception)} {exception?.GetBaseException().Message}");
        public void Dispose() { }
        public override string ToString() => lines.IsEmpty ? "No server errors logged." : "Server errors: " + string.Join(" || ", lines);
    }
}

/// <summary>Host wiring for deployment settings; no database needed.</summary>
public sealed class DeploymentHostTests
{
    private static readonly Dictionary<string, string?> SpaOrigin = new() { ["Cors:AllowedOrigins:0"] = "https://spa.example" };

    [Fact]
    public async Task A_configured_spa_origin_may_call_the_api_cross_origin()
    {
        await using var factory = new ApiFactory(settings: SpaOrigin);
        using var client = ApiFactory.CreateHttpsClient(factory);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/expenses/00000000-0000-0000-0000-000000000001/approve");
        preflight.Headers.Add("Origin", "https://spa.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,if-match,content-type");
        var response = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://spa.example", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("if-match", response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));

        using var authenticated = ApiFactory.CreateHttpsClient(factory, TestUsers.Employee);
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        get.Headers.Add("Origin", "https://spa.example");
        var actual = await authenticated.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        Assert.Contains("ETag", actual.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public async Task Other_origins_get_no_cors_grant()
    {
        await using var factory = new ApiFactory(settings: SpaOrigin);
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Employee);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("Origin", "https://evil.example");
        var response = await client.SendAsync(request);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("Production", "http://spa.example")]
    [InlineData("Production", "https://spa.example/app")]
    [InlineData("Development", "not an origin")]
    public void Invalid_cors_origins_stop_startup(string environment, string origin)
    {
        using var factory = new ApiFactory(environment, settings: new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = origin });
        var error = Assert.Throws<InvalidOperationException>(() => factory.Services);
        Assert.Contains("Cors:AllowedOrigins", error.Message);
    }

    [Fact]
    public void The_api_hosts_the_outbox_worker_only_when_asked()
    {
        using (var plain = new ApiFactory())
            Assert.DoesNotContain(plain.Services.GetServices<IHostedService>(), s => s.GetType().Name == "OutboxWorker");
        using var hosting = new ApiFactory(settings: new Dictionary<string, string?> { ["Messaging:RunInApi"] = "true" });
        Assert.Contains(hosting.Services.GetServices<IHostedService>(), s => s.GetType().Name == "OutboxWorker");
    }
}

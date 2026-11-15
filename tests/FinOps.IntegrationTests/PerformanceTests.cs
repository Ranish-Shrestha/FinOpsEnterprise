using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinOps.Application.Expenses;
using FinOps.Infrastructure;
using FinOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace FinOps.IntegrationTests;

/// <summary>
/// Latency targets (ADR 0006), measured through the full HTTP stack: routing, authentication, the per-request
/// access lookup, authorization, EF Core and SQL Server. The database holds the same 60,000 synthetic expenses as
/// <see cref="QueryPlanTests"/>. Each endpoint gets 20 warm-up requests, then 200 measured sequential requests.
/// The in-memory test server excludes network and TLS time, so these numbers are the server's share of latency.
/// <para>
/// Targets are enforced in the reference environment, CI (GitHub sets <c>CI=true</c>; set <c>FINOPS_PERF_ENFORCE=true</c>
/// to enforce elsewhere). Everywhere else the results are reported with the machine's own SQL commit latency, because a
/// laptop's Docker disk can make a single durable commit slower than a whole target (see docs/performance).
/// </para>
/// </summary>
[Trait("Category", "Database")]
[Trait("Category", "Performance")]
[Collection(SqlServerCollection.Name)]
public sealed class PerformanceTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    private const string Database = "FinOpsPerformance";
    private const int ExpenseCount = 60_000;
    private const int Warmup = 20;
    private const int Samples = 200;

    internal sealed record Measurement(string Scenario, double? TargetP95Ms, int Count, double P50Ms, double P95Ms,
        double P99Ms, double MaxMs)
    {
        public bool MeetsTarget => TargetP95Ms is not { } target || P95Ms < target;
    }

    private static bool Enforced =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("FINOPS_PERF_ENFORCE"), "true", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Api_endpoints_meet_their_p95_targets_at_60k_expenses()
    {
        var connectionString = await PrepareDatabaseAsync();
        await using var api = new SqlServerFixture.DatabaseApiFactory(connectionString, Unthrottled);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // The URLs the Angular tabs and panels actually send.
        (string Name, DemoPersona Persona, string Url, double? TargetMs)[] scenarios =
        [
            // No target: the per-request access lookup every endpoint pays, reported to explain the others.
            ("Access lookup only (GET /api/me)", DemoDataSeeder.EngineeringManager, "/api/me", null),
            ("Manager approval queue", DemoDataSeeder.EngineeringManager, "/api/expenses?status=Submitted&sort=submittedAt", 150),
            ("Employee: my expenses", DemoDataSeeder.Employee, "/api/expenses?mine=true", 100),
            ("Budget vs. actual report, one month (JSON)", DemoDataSeeder.FinanceOfficer,
                $"/api/reports/budget-vs-actual?year={today.Year}&month={today.Month}", 300),
        ];

        var results = await EnvironmentBaselineAsync(connectionString);
        foreach (var (name, persona, url, target) in scenarios)
        {
            using var client = ApiFactory.CreateHttpsClient(api, persona.EntraObjectId.ToString());
            for (var i = 0; i < Warmup; i++) await GetAsync(client, url);

            var timings = new List<double>(Samples);
            for (var i = 0; i < Samples; i++)
            {
                var started = Stopwatch.GetTimestamp();
                await GetAsync(client, url);
                timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            results.Add(Summarize(name, target, timings));
        }

        Report(results, "ms");
        AssertTargets(results);
    }

    [Fact]
    public async Task Outbox_delivers_a_posted_expense_within_three_seconds_at_p95()
    {
        const int Postings = 20;
        var connectionString = await PrepareDatabaseAsync();
        await using var api = new SqlServerFixture.DatabaseApiFactory(connectionString, Unthrottled);

        // The real hosted worker with its default 2-second idle poll, as FinOps.Worker runs it.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FinOps"] = connectionString,
            ["Messaging:PollIntervalSeconds"] = "2",
        }).Build());
        services.AddLogging();
        services.AddInfrastructure();
        services.AddOutboxWorker();
        await using var worker = services.BuildServiceProvider();
        var hosted = worker.GetServices<IHostedService>().ToList();
        foreach (var service in hosted) await service.StartAsync(CancellationToken.None);

        try
        {
            var random = new Random(7);
            var latencies = new List<double>(Postings);
            for (var i = 0; i < Postings; i++)
            {
                // Spread postings across the poll cycle so the samples include the worst case (just missed a poll).
                await Task.Delay(random.Next(0, 2000));
                var expenseId = await PostedThroughApiAsync(api);
                var posted = Stopwatch.GetTimestamp();
                while (!await ReimbursedAsync(connectionString, expenseId))
                {
                    Assert.True(Stopwatch.GetElapsedTime(posted) < TimeSpan.FromSeconds(30), "The worker did not deliver within 30 s.");
                    await Task.Delay(20);
                }

                latencies.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
            }

            var result = Summarize("Outbox: post → reimbursement scheduled", 3000, latencies);
            Report([result], "ms");
            AssertTargets([result]);
        }
        finally
        {
            foreach (var service in hosted) await service.StopAsync(CancellationToken.None);
        }
    }

    private static readonly Dictionary<string, string?> Unthrottled = new()
    {
        ["RateLimiting:PermitLimit"] = "1000000",
    };

    private static async Task GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Migrated, seeded database with 60k expenses; built once per test run and reused by both tests.</summary>
    private async Task<string> PrepareDatabaseAsync()
    {
        var connectionString = fixture.ConnectionStringFor(Database);
        await PreparationLock.WaitAsync();
        try
        {
            var options = new DbContextOptionsBuilder<FinOpsDbContext>().UseSqlServer(connectionString).Options;
            await using var db = new FinOpsDbContext(options);
            if (await db.Database.CanConnectAsync() && await db.ExpenseRequests.CountAsync() >= ExpenseCount) return connectionString;
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            await DemoDataSeeder.SeedAsync(db);
            await SyntheticExpenses.BulkInsertAsync(connectionString, ExpenseCount);
            return connectionString;
        }
        finally
        {
            PreparationLock.Release();
        }
    }

    private static readonly SemaphoreSlim PreparationLock = new(1, 1);

    private static async Task<Guid> PostedThroughApiAsync(SqlServerFixture.DatabaseApiFactory api)
    {
        using var employee = ApiFactory.CreateHttpsClient(api, DemoDataSeeder.Employee.EntraObjectId.ToString());
        var created = await employee.PostAsJsonAsync("/api/expenses", new
        {
            departmentId = DemoDataSeeder.EngineeringDepartment, amount = 12.34m, currency = "USD", category = "Meals",
            description = "Outbox latency sample", expenseDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });
        var expense = (await created.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
        expense = await ActAsync(api, DemoDataSeeder.Employee, expense, ExpenseActions.Submit);
        expense = await ActAsync(api, DemoDataSeeder.EngineeringManager, expense, ExpenseActions.Approve);
        await ActAsync(api, DemoDataSeeder.FinanceOfficer, expense, ExpenseActions.Post);
        return expense.Id;
    }

    private static async Task<ExpenseDetailDto> ActAsync(SqlServerFixture.DatabaseApiFactory api, DemoPersona persona,
        ExpenseDetailDto expense, string action)
    {
        using var client = ApiFactory.CreateHttpsClient(api, persona.EntraObjectId.ToString());
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/expenses/{expense.Id}/{action}");
        request.Headers.TryAddWithoutValidation("If-Match", expense.Version);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
    }

    private static async Task<bool> ReimbursedAsync(string connectionString, Guid expenseId)
    {
        var options = new DbContextOptionsBuilder<FinOpsDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new FinOpsDbContext(options);
        return await db.Reimbursements.AnyAsync(r => r.ExpenseRequestId == expenseId);
    }

    private void AssertTargets(IReadOnlyCollection<Measurement> results)
    {
        var missed = results.Where(r => !r.MeetsTarget).ToList();
        if (missed.Count == 0) return;
        var message = string.Join("; ", missed.Select(r => $"{r.Scenario}: p95 {r.P95Ms:F1} ms ≥ target {r.TargetP95Ms} ms"));
        if (Enforced) Assert.Fail(message);
        output.WriteLine($"NOT ENFORCED outside CI (set FINOPS_PERF_ENFORCE=true to enforce): {message}");
    }

    /// <summary>
    /// What this machine's SQL Server costs before any application code: a round trip, and a one-row durable commit
    /// (transaction-log flush). Endpoints that write an audit row pay the second on every request.
    /// </summary>
    private static async Task<List<Measurement>> EnvironmentBaselineAsync(string connectionString)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        async Task<Measurement> Time(string name, string sql)
        {
            await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
            for (var i = 0; i < Warmup; i++) await command.ExecuteNonQueryAsync();
            var timings = new List<double>(Samples);
            for (var i = 0; i < Samples; i++)
            {
                var started = Stopwatch.GetTimestamp();
                await command.ExecuteNonQueryAsync();
                timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            return Summarize(name, null, timings);
        }

        await using (var create = new Microsoft.Data.SqlClient.SqlCommand(
            "IF OBJECT_ID('dbo.PerfProbe') IS NULL CREATE TABLE dbo.PerfProbe (Id bigint IDENTITY PRIMARY KEY, At datetime2 NOT NULL);",
            connection)) await create.ExecuteNonQueryAsync();
        return
        [
            await Time("Environment: SQL round trip (SELECT 1)", "SELECT 1;"),
            await Time("Environment: one-row durable commit", "INSERT dbo.PerfProbe (At) VALUES (SYSUTCDATETIME());"),
        ];
    }

    internal static Measurement Summarize(string scenario, double? target, IReadOnlyCollection<double> timings)
    {
        var sorted = timings.Order().ToArray();
        // Nearest-rank percentile: the smallest value with at least p% of samples at or below it.
        double Percentile(double p) => sorted[Math.Max(0, (int)Math.Ceiling(p / 100 * sorted.Length) - 1)];
        return new Measurement(scenario, target, sorted.Length, Percentile(50), Percentile(95), Percentile(99), sorted[^1]);
    }

    private void Report(IEnumerable<Measurement> results, string unit)
    {
        foreach (var r in results)
        {
            var verdict = r.TargetP95Ms is { } target ? $"target p95 < {target}{unit}: {(r.MeetsTarget ? "met" : "MISSED")}" : "no target";
            output.WriteLine($"{r.Scenario}: n={r.Count} p50={r.P50Ms:F1}{unit} p95={r.P95Ms:F1}{unit} " +
                $"p99={r.P99Ms:F1}{unit} max={r.MaxMs:F1}{unit} ({verdict})");
            // Machine-readable copy for CI artifacts (TestResults/performance-*.json).
            var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "TestResults"));
            var file = Path.Combine(directory.FullName, $"performance-{Slug(r.Scenario)}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string Slug(string value) =>
        new string(value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}

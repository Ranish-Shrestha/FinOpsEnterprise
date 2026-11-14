using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using FinOps.Application.Expenses;
using FinOps.Domain.Expenses;
using FinOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace FinOps.IntegrationTests;

/// <summary>
/// Execution-plan evidence for the expense list queries (spec §7: "compare generated SQL and measured
/// performance before/after indexing"). Builds a separate database with ~60k synthetic expenses, then runs
/// the exact EF-generated SQL the way EF sends it (sp_executesql with parameters, so parameter sniffing
/// matches production) and records logical reads and the operators of the actual plan.
/// Results are written to the test output and summarized in docs/performance/expense-queries.md.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed partial class QueryPlanTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    private const int ExpenseCount = 60_000;
    private const string Database = "FinOpsQueryPlans";

    private const string After = "after: covering indexes, ID-first paging";
    private const string Before = "before: Phase 3a query and indexes";
    private const string NoIndexes = "no workload indexes";

    private sealed record Measurement(string Scenario, string Variant, long LogicalReads, string PlanOperators, int Rows);

    [Fact]
    public async Task Expense_list_queries_use_their_indexes()
    {
        var connectionString = fixture.ConnectionStringFor(Database);
        var options = new DbContextOptionsBuilder<FinOpsDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new FinOpsDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        await DemoDataSeeder.SeedAsync(db);
        await BulkInsertExpensesAsync(connectionString);

        var scenarios = Scenarios(db);
        var results = new List<Measurement>();

        // After: production queries with the current (covering) indexes.
        foreach (var (name, sql) in scenarios) results.Add(await MeasureAsync(connectionString, name, After, sql));

        foreach (var (name, sql) in ReportScenarios()) results.Add(await MeasureAsync(connectionString, name, After, sql));

        // Step 2 of production paging: fetch the page's rows by primary key.
        var managerIds = await ExpenseQueries.PageIds(ExpenseQueries.Filtered(db,
                new ExpenseVisibility(DemoDataSeeder.EngineeringManager.UserId, false, true, [DemoDataSeeder.EngineeringDepartment]),
                ExpenseStatus.Submitted, null, false), new ExpenseSort(ExpenseSortField.SubmittedAt, false), 1, 25)
            .ToListAsync();
        var fetchSql = db.ExpenseRequests.AsNoTracking().Where(e => managerIds.Contains(e.Id)).ToQueryString();
        results.Add(await MeasureAsync(connectionString, "Step 2: fetch 25 rows by key", After, fetchSql));

        // No indexes: production queries with every workload index dropped.
        await ExecuteAsync(connectionString, """
            DROP INDEX IX_ExpenseRequests_ApprovalQueue ON finance.ExpenseRequests;
            DROP INDEX IX_ExpenseRequests_Requester ON finance.ExpenseRequests;
            DROP INDEX IX_ExpenseRequests_Status ON finance.ExpenseRequests;
            DROP INDEX IX_ExpenseRequests_Reporting ON finance.ExpenseRequests;
            """);
        foreach (var (name, sql) in scenarios) results.Add(await MeasureAsync(connectionString, name, NoIndexes, sql));
        foreach (var (name, sql) in ReportScenarios()) results.Add(await MeasureAsync(connectionString, name, NoIndexes, sql));

        // Before: the Phase 3a query shape with the Phase 3a (non-covering) index definitions.
        await ExecuteAsync(connectionString, """
            CREATE INDEX IX_ExpenseRequests_ApprovalQueue ON finance.ExpenseRequests (DepartmentId, Status, SubmittedAtUtc);
            CREATE INDEX IX_ExpenseRequests_Requester ON finance.ExpenseRequests (RequesterId, CreatedAtUtc);
            """);
        foreach (var (name, sql) in LegacyScenarios(db)) results.Add(await MeasureAsync(connectionString, name, Before, sql));

        Report(results);

        // The evidence the indexes and query shape are doing their job.
        foreach (var (name, _) in scenarios)
        {
            var after = results.Single(r => r.Scenario == name && r.Variant == After);
            var before = results.Single(r => r.Scenario == name && r.Variant == Before);
            Assert.DoesNotContain("Clustered Index Scan", after.PlanOperators);
            Assert.Contains("Index Seek", after.PlanOperators);
            Assert.True(after.LogicalReads * 10 < before.LogicalReads,
                $"{name}: expected at least 10x fewer reads ({after.LogicalReads} vs {before.LogicalReads}).");
        }

        foreach (var (name, _) in ReportScenarios())
        {
            var after = results.Single(r => r.Scenario == name && r.Variant == After);
            var none = results.Single(r => r.Scenario == name && r.Variant == NoIndexes);
            Assert.Contains("IX_ExpenseRequests_Reporting", after.PlanOperators);
            Assert.DoesNotContain("Clustered Index Scan", after.PlanOperators);
            // A one-month report reads a small slice of the index. A whole-year report covers most of the
            // table, so its gain is only the narrower (covering) index, roughly 3x.
            var factor = name.Contains("one month") ? 10 : 2;
            Assert.True(after.LogicalReads * factor < none.LogicalReads,
                $"{name}: expected at least {factor}x fewer reads ({after.LogicalReads} vs {none.LogicalReads}).");
        }
        await db.Database.EnsureDeletedAsync();
    }

    /// <summary>The three list queries the UI issues most, built by the production query builder.</summary>
    private static List<(string Name, string Sql)> Scenarios(FinOpsDbContext db)
    {
        var manager = new ExpenseVisibility(DemoDataSeeder.EngineeringManager.UserId, false, true, [DemoDataSeeder.EngineeringDepartment]);
        var employee = new ExpenseVisibility(DemoDataSeeder.Employee.UserId, false, false, [DemoDataSeeder.EngineeringDepartment]);
        var finance = new ExpenseVisibility(DemoDataSeeder.FinanceOfficer.UserId, true, false, [DemoDataSeeder.FinanceDepartment]);

        // Step 1 of production paging (IDs only). Step 2 is a handful of primary-key seeks.
        string PageSql(ExpenseVisibility v, ExpenseStatus? status, bool mine, ExpenseSort sort) =>
            ExpenseQueries.PageIds(ExpenseQueries.Filtered(db, v, status, null, mine), sort, 1, 25).ToQueryString();

        return
        [
            ("Manager approval queue", PageSql(manager, ExpenseStatus.Submitted, false, new ExpenseSort(ExpenseSortField.SubmittedAt, false))),
            ("Employee: my expenses", PageSql(employee, null, true, ExpenseSort.Default)),
            ("Finance: ready to post", PageSql(finance, ExpenseStatus.Approved, false, ExpenseSort.Default)),
        ];
    }

    /// <summary>The budget-vs-actual stored procedure, for all departments (finance's view).</summary>
    private static List<(string Name, string Sql)> ReportScenarios()
    {
        var today = DateTime.UtcNow;
        return
        [
            ("Report: budget vs actual, whole year", $"EXEC finance.usp_BudgetVsActual @Year = {today.Year}, @Month = NULL, @DepartmentIds = NULL;"),
            ("Report: budget vs actual, one month", $"EXEC finance.usp_BudgetVsActual @Year = {today.Year}, @Month = {today.Month}, @DepartmentIds = NULL;"),
        ];
    }

    /// <summary>
    /// The Phase 3a query shape, kept here only as the measured baseline. Its catch-all predicate was:
    /// <c>RequesterId = @me OR @readAll = 1 OR (@readDepartment = 1 AND DepartmentId IN (...))</c>.
    /// </summary>
    private static List<(string Name, string Sql)> LegacyScenarios(FinOpsDbContext db)
    {
        IQueryable<ExpenseRequest> Legacy(Guid userId, bool readAll, bool readDepartment, List<Guid> departmentIds) =>
            db.ExpenseRequests.AsNoTracking().Where(e =>
                e.RequesterId == userId || readAll || (readDepartment && departmentIds.Contains(e.DepartmentId)));

        // Phase 3a paging: full rows, and an always-ascending Id tie-breaker.
        string LegacyPage(IQueryable<ExpenseRequest> q, bool bySubmitted) =>
            (bySubmitted ? q.OrderBy(e => e.SubmittedAtUtc) : q.OrderByDescending(e => e.CreatedAtUtc))
            .ThenBy(e => e.Id).Skip(0).Take(25).ToQueryString();

        return
        [
            ("Manager approval queue", LegacyPage(Legacy(DemoDataSeeder.EngineeringManager.UserId, false, true,
                [DemoDataSeeder.EngineeringDepartment]).Where(e => e.Status == ExpenseStatus.Submitted), bySubmitted: true)),
            ("Employee: my expenses", LegacyPage(Legacy(DemoDataSeeder.Employee.UserId, false, false,
                [DemoDataSeeder.EngineeringDepartment]).Where(e => e.RequesterId == DemoDataSeeder.Employee.UserId), bySubmitted: false)),
            ("Finance: ready to post", LegacyPage(Legacy(DemoDataSeeder.FinanceOfficer.UserId, true, false,
                [DemoDataSeeder.FinanceDepartment]).Where(e => e.Status == ExpenseStatus.Approved), bySubmitted: false)),
        ];
    }

    private static async Task BulkInsertExpensesAsync(string connectionString)
    {
        var requesters = DemoDataSeeder.Personas.Select(p => p.UserId).ToArray();
        Guid[] departments = [DemoDataSeeder.EngineeringDepartment, DemoDataSeeder.FinanceDepartment, DemoDataSeeder.OperationsDepartment];
        string[] categories = Enum.GetNames<ExpenseCategory>();
        var random = new Random(42); // deterministic data → repeatable measurements
        var now = DateTimeOffset.UtcNow;

        var table = new DataTable();
        foreach (var (name, type) in new (string, Type)[]
                 {
                     ("Id", typeof(Guid)), ("RequesterId", typeof(Guid)), ("DepartmentId", typeof(Guid)), ("Amount", typeof(decimal)),
                     ("Currency", typeof(string)), ("Category", typeof(string)), ("Description", typeof(string)),
                     ("ExpenseDate", typeof(DateTime)), ("Status", typeof(string)), ("CreatedAtUtc", typeof(DateTimeOffset)),
                     ("UpdatedAtUtc", typeof(DateTimeOffset)), ("SubmittedAtUtc", typeof(DateTimeOffset)),
                     ("DecidedAtUtc", typeof(DateTimeOffset)), ("PostedAtUtc", typeof(DateTimeOffset)), ("PostedById", typeof(Guid)),
                 })
        {
            table.Columns.Add(name, type);
        }

        for (var i = 0; i < ExpenseCount; i++)
        {
            // ~5% submitted, 5% draft, 5% approved, 85% posted: a queue is a small slice of history.
            var status = (i % 20) switch { 0 => "Submitted", 1 => "Draft", 2 => "Approved", _ => "Posted" };
            var created = now.AddMinutes(-random.Next(1, 360 * 24 * 60));
            var submitted = status == "Draft" ? (object)DBNull.Value : created.AddHours(1);
            var decided = status is "Approved" or "Posted" ? (object)created.AddHours(2) : DBNull.Value;
            var posted = status == "Posted" ? (object)created.AddHours(3) : DBNull.Value;
            var postedBy = status == "Posted" ? (object)DemoDataSeeder.FinanceOfficer.UserId : DBNull.Value;
            table.Rows.Add(Guid.NewGuid(), requesters[random.Next(requesters.Length)], departments[random.Next(departments.Length)],
                Math.Round((decimal)(random.NextDouble() * 900 + 5), 2), "USD", categories[random.Next(categories.Length)],
                $"Synthetic expense {i}", created.UtcDateTime.Date, status, created, created, submitted, decided, posted, postedBy);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "finance.ExpenseRequests", BatchSize = 10_000 };
        foreach (DataColumn column in table.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(table);
        await ExecuteAsync(connectionString, "UPDATE STATISTICS finance.ExpenseRequests WITH FULLSCAN;");
    }

    /// <summary>Runs the query as EF does (sp_executesql), capturing logical reads and the actual plan.</summary>
    private static async Task<Measurement> MeasureAsync(string connectionString, string scenario, string variant, string efSql)
    {
        await ExecuteAsync(connectionString, "DBCC FREEPROCCACHE WITH NO_INFOMSGS;");
        await using var connection = new SqlConnection(connectionString);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, e) => messages.AppendLine(e.Message);
        await connection.OpenAsync();

        await using (var on = new SqlCommand("SET STATISTICS IO ON; SET STATISTICS XML ON;", connection)) await on.ExecuteNonQueryAsync();

        await using var command = ToSpExecuteSql(efSql, connection);
        var rows = 0;
        var plans = new StringBuilder(); // one plan per statement (procedures have several)
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase);
                while (await reader.ReadAsync())
                {
                    if (isPlan) plans.Append(reader.GetString(0));
                    else rows++;
                }
            } while (await reader.NextResultAsync());
        }

        var reads = LogicalReads().Matches(messages.ToString())
            .Where(m => m.Groups["table"].Value == "ExpenseRequests")
            .Sum(m => long.Parse(m.Groups["reads"].Value));
        return new Measurement(scenario, variant, reads, Operators(plans.ToString()), rows);
    }

    /// <summary>Turns EF's ToQueryString() output ("DECLARE @p type = value; SELECT ...") into sp_executesql.</summary>
    private static SqlCommand ToSpExecuteSql(string efSql, SqlConnection connection)
    {
        var declarations = Declaration().Matches(efSql).ToList();
        var body = Declaration().Replace(efSql, string.Empty).Trim();
        var parameterList = string.Join(", ", declarations.Select(d => $"{d.Groups["name"].Value} {d.Groups["type"].Value}"));
        var assignments = string.Join(", ", declarations.Select(d => $"{d.Groups["name"].Value} = {d.Groups["value"].Value}"));
        var sql = declarations.Count == 0
            ? body
            : $"EXEC sp_executesql N'{body.Replace("'", "''")}', N'{parameterList}', {assignments};";
        return new SqlCommand(sql, connection);
    }

    private static string Operators(string planXml)
    {
        // "Index Seek(IX_ExpenseRequests_ApprovalQueue)", "Clustered Index Scan(PK_ExpenseRequests)", ...
        var operators = PlanOperator().Matches(planXml)
            .Select(m => $"{m.Groups["op"].Value}({m.Groups["index"].Value})")
            .Where(o => o.Contains("ExpenseRequests", StringComparison.Ordinal))
            .Distinct();
        return string.Join(" + ", operators);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private void Report(IEnumerable<Measurement> results)
    {
        output.WriteLine($"Expense list queries over {ExpenseCount:N0} rows (page 1, size 25)");
        output.WriteLine("| Scenario | Variant | Logical reads (ExpenseRequests) | Rows | Plan operators on ExpenseRequests |");
        output.WriteLine("| --- | --- | ---: | ---: | --- |");
        foreach (var r in results.OrderBy(r => r.Scenario).ThenBy(r => r.Variant))
            output.WriteLine($"| {r.Scenario} | {r.Variant} | {r.LogicalReads:N0} | {r.Rows} | {r.PlanOperators} |");
    }

    [GeneratedRegex(@"DECLARE (?<name>@\w+) (?<type>[\w()]+(?:\(\d+(?:,\s*\d+)?\))?) = (?<value>.+?);\r?\n")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"Table '(?<table>\w+)'\. Scan count \d+, logical reads (?<reads>\d+)")]
    private static partial Regex LogicalReads();

    [GeneratedRegex(@"PhysicalOp=""(?<op>[^""]+)""[^>]*>(?:(?!<RelOp)[\s\S])*?<Object [^>]*Table=""\[(?<table>ExpenseRequests)\]""[^>]*Index=""\[(?<index>[^\]]+)\]""")]
    private static partial Regex PlanOperator();
}

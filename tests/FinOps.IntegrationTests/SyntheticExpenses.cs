using System.Data;
using FinOps.Domain.Expenses;
using FinOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace FinOps.IntegrationTests;

/// <summary>
/// Deterministic synthetic expense history (seed 42), shared by the query-plan and latency tests so both measure the
/// same data: six requesters, three departments, dates over the last year; 85% posted, 5% each submitted, approved, draft.
/// </summary>
internal static class SyntheticExpenses
{
    internal static async Task BulkInsertAsync(string connectionString, int count)
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

        for (var i = 0; i < count; i++)
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
        await using var stats = new SqlCommand("UPDATE STATISTICS finance.ExpenseRequests WITH FULLSCAN;", connection);
        await stats.ExecuteNonQueryAsync();
    }
}

using System.Data;
using FinOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace FinOps.IntegrationTests;

/// <summary>
/// The BI login used by SSRS and Power BI (<c>grant-reporting-access</c>): it can read the <c>reporting</c> views and run
/// the reporting procedures the RDL files call, and nothing else. The views agree with the procedures.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class ReportingAccessTests(SqlServerFixture fixture)
{
    private const string Login = "finops-reporting-test";

    [Fact]
    public async Task Reporting_login_reads_views_and_runs_reports_but_cannot_touch_tables()
    {
        var password = $"Rp-{Guid.NewGuid():N}-Aa1";
        await fixture.WithDbAsync(async db => { await DatabaseAdministration.GrantReportingAccessAsync(db, Login, password); return 0; });

        await using var connection = new SqlConnection(As(password));
        await connection.OpenAsync();

        foreach (var view in new[] { "Departments", "Expenses", "ApprovalDecisions", "BudgetVsActual" })
            Assert.True(await ScalarAsync(connection, $"SELECT COUNT(*) FROM reporting.{view};") > 0, $"reporting.{view} is empty");

        await using (var report = new SqlCommand("finance.usp_ExpenseSummary", connection) { CommandType = CommandType.StoredProcedure })
        {
            report.Parameters.Add(new SqlParameter("@From", SqlDbType.Date) { Value = DateTime.UtcNow.Date.AddYears(-1) });
            report.Parameters.Add(new SqlParameter("@To", SqlDbType.Date) { Value = DateTime.UtcNow.Date });
            await using var reader = await report.ExecuteReaderAsync();
            Assert.True(reader.HasRows);
        }

        // No table access, no writes, no other procedures.
        foreach (var sql in new[]
                 {
                     "SELECT TOP (1) Id FROM finance.ExpenseRequests;",
                     "SELECT TOP (1) Email FROM [identity].UserProfiles;",
                     "UPDATE finance.Budgets SET Amount = Amount WHERE 1 = 0;",
                     "SELECT TOP (1) Id FROM audit.AuditEvents;",
                 })
        {
            var denied = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync(connection, sql));
            Assert.Equal(229, denied.Number); // permission denied
        }
    }

    [Fact]
    public async Task Budget_view_matches_the_budget_vs_actual_procedure()
    {
        var year = DateTime.UtcNow.Year;
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var fromView = await ScalarDecimalAsync(connection,
            $"SELECT COALESCE(SUM(COALESCE(Budget, 0) + Posted + Committed + Pending), 0) FROM reporting.BudgetVsActual WHERE [Year] = {year};");

        await using var procedure = new SqlCommand("finance.usp_BudgetVsActual", connection) { CommandType = CommandType.StoredProcedure };
        procedure.Parameters.Add(new SqlParameter("@Year", SqlDbType.Int) { Value = year });
        decimal fromProcedure = 0;
        await using (var reader = await procedure.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                fromProcedure += (reader.IsDBNull(reader.GetOrdinal("Budget")) ? 0 : reader.GetDecimal(reader.GetOrdinal("Budget")))
                    + reader.GetDecimal(reader.GetOrdinal("Posted")) + reader.GetDecimal(reader.GetOrdinal("Committed"))
                    + reader.GetDecimal(reader.GetOrdinal("Pending"));
            }
        }

        Assert.True(fromProcedure > 0);
        Assert.Equal(fromProcedure, fromView);
    }

    [Fact]
    public async Task Granting_again_resets_the_password()
    {
        var first = $"Rp-{Guid.NewGuid():N}-Aa1";
        var second = $"Rp-{Guid.NewGuid():N}-Bb2";
        await fixture.WithDbAsync(async db =>
        {
            await DatabaseAdministration.GrantReportingAccessAsync(db, Login, first);
            await DatabaseAdministration.GrantReportingAccessAsync(db, Login, second);
            await Assert.ThrowsAsync<ArgumentException>(() => DatabaseAdministration.GrantReportingAccessAsync(db, "bad name;", second));
            return 0;
        });

        await using (var ok = new SqlConnection(As(second))) await ok.OpenAsync();
        await using var old = new SqlConnection(As(first));
        Assert.Equal(18456, (await Assert.ThrowsAsync<SqlException>(() => old.OpenAsync())).Number); // login failed
    }

    private string As(string password) => new SqlConnectionStringBuilder(fixture.ConnectionString)
    {
        UserID = Login, Password = password, Pooling = false,
    }.ConnectionString;

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<decimal> ScalarDecimalAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToDecimal(await command.ExecuteScalarAsync() ?? 0m);
    }
}

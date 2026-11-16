using FinOps.Infrastructure;
using FinOps.Infrastructure.Persistence;

namespace FinOps.Api;

/// <summary>
/// One-shot commands run instead of the web host: <c>dotnet FinOps.Api.dll &lt;command&gt;</c>. They are explicit
/// operator or pipeline steps; the web app itself never migrates or changes permissions at startup.
/// </summary>
internal static class AdminCommands
{
    internal const string SeedDemoData = "seed-demo-data";
    internal const string Migrate = "migrate";
    internal const string GrantAppAccess = "grant-app-access";
    internal const string ProvisionUser = "provision-user";
    internal const string SeedDemoExpenses = "seed-demo-expenses";
    internal const string GrantReportingAccess = "grant-reporting-access";
    internal const string ReportingPasswordVariable = "FINOPS_REPORTING_PASSWORD";

    /// <summary>
    /// Runs a command if the arguments name one; returns its exit code, or null to start the web host. Commands get a
    /// minimal host with only the data layer, so they need a connection string and nothing else (no Entra settings).
    /// </summary>
    internal static async Task<int?> TryRunAsync(string[] args)
    {
        var command = args.FirstOrDefault(a => a is SeedDemoData or Migrate or GrantAppAccess or ProvisionUser or SeedDemoExpenses
            or GrantReportingAccess);
        if (command is null) return null;

        var builder = WebApplication.CreateSlimBuilder(args);
        // Synthetic demo identities exist only in Development; refuse before touching any database.
        // SQL-authentication logins with every department's data: local or self-hosted report servers only.
        if (command is SeedDemoData or SeedDemoExpenses or GrantReportingAccess && !builder.Environment.IsDevelopment())
        {
            Console.Error.WriteLine($"{command} is available only when ASPNETCORE_ENVIRONMENT=Development.");
            return 1;
        }

        // Validate before touching the database.
        ProvisionRequest? provision = null;
        if (command == ProvisionUser && (provision = ProvisionRequest.Parse(args)) is null) return ProvisionUsage();
        var reportingPassword = Environment.GetEnvironmentVariable(ReportingPasswordVariable);
        if (command == GrantReportingAccess && string.IsNullOrEmpty(reportingPassword)) return ReportingUsage();
        SeedExpensesRequest? seedExpenses = null;
        if (command == SeedDemoExpenses && (seedExpenses = SeedExpensesRequest.Parse(args)) is null) return SeedExpensesUsage();

        builder.Services.AddInfrastructure();
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        switch (command)
        {
            case SeedDemoData:
                Console.WriteLine($"Demo data seeded: {await DemoDataSeeder.SeedAsync(db)} row(s) inserted.");
                return 0;

            // seed-demo-expenses [--months <1-11>] [--for-user <object id> [--per-month <1-20>]]: Development-only history.
            case SeedDemoExpenses:
                try
                {
                    var result = await DemoExpenseGenerator.GenerateAsync(db, seedExpenses!.Months, seedExpenses.ForUser,
                        extraPerMonth: seedExpenses.PerMonth);
                    var statuses = string.Join(", ", result.ByStatus.Where(s => s.Value > 0).Select(s => $"{s.Value} {s.Key}"));
                    Console.WriteLine($"Demo expenses: {result.Expenses} inserted ({(statuses == "" ? "none" : statuses)}), "
                        + $"{result.AlreadyPresent} already present; {result.Notifications} notification(s), {result.Budgets} budget(s) added or resized.");
                    return 0;
                }
                catch (InvalidOperationException error)
                {
                    Console.Error.WriteLine(error.Message);
                    return 2;
                }

            // grant-reporting-access [--login <name>]: read-only login for SSRS and Power BI. Password from the environment.
            case GrantReportingAccess:
                var loginIndex = Array.IndexOf(args, "--login");
                var login = loginIndex >= 0 && loginIndex + 1 < args.Length ? args[loginIndex + 1] : "finops_reporting";
                try
                {
                    await DatabaseAdministration.GrantReportingAccessAsync(db, login, reportingPassword!);
                }
                catch (Exception error) when (error is ArgumentException or Microsoft.Data.SqlClient.SqlException)
                {
                    Console.Error.WriteLine(error.Message); // never includes the password
                    return 2;
                }
                Console.WriteLine($"Reporting login '{login}' can read the reporting views and run the "
                    + $"{DatabaseAdministration.ReportingProcedures.Count} reporting procedures; nothing else.");
                return 0;

            // Used by the deploy workflow with the deploying identity (Entra admin of the SQL server).
            case Migrate:
                await DatabaseAdministration.MigrateAsync(db);
                Console.WriteLine("Migrations applied.");
                return 0;

            // grant-app-access <managed identity name> [<client id>]: least-privilege user for the running app.
            case GrantAppAccess:
                var index = Array.IndexOf(args, GrantAppAccess);
                Guid? clientId = null;
                if (index + 2 < args.Length)
                {
                    if (!Guid.TryParse(args[index + 2], out var parsed)) return Usage();
                    clientId = parsed;
                }

                if (index + 1 >= args.Length) return Usage();
                await DatabaseAdministration.GrantRuntimeAccessAsync(db, args[index + 1], clientId);
                Console.WriteLine($"Runtime access granted to '{args[index + 1]}'.");
                return 0;

            // provision-user --object-id <oid> --name <name> --email <email> --role <role> [--department <name>]
            case ProvisionUser:
                try
                {
                    await DatabaseAdministration.ProvisionUserAsync(db, provision!.ObjectId, provision.Name, provision.Email,
                        provision.Role, provision.Department);
                }
                catch (Microsoft.Data.SqlClient.SqlException error) when (error.Number is 50101 or 50102)
                {
                    Console.Error.WriteLine(error.Message);
                    return 2;
                }

                Console.WriteLine($"Provisioned {provision.Name} ({provision.ObjectId}) as {provision.Role}"
                    + (provision.Department is null ? "." : $" in {provision.Department}."));
                return 0;
        }

        return null;
    }

    private static int ProvisionUsage()
    {
        Console.Error.WriteLine("""
            Usage: provision-user --object-id <Entra object ID> --name "<display name>" --email <email>
                                  --role <Employee|Manager|FinanceOfficer|Administrator> [--department "<Demo Engineering|Demo Finance|Demo Operations>"]
            The object ID is shown on the app's /access-denied page after the user's first sign-in.
            """);
        return 2;
    }

    /// <summary>The arguments of <c>provision-user</c>; null when one is missing or the object ID is not a GUID.</summary>
    internal sealed record ProvisionRequest(Guid ObjectId, string Name, string Email, string Role, string? Department)
    {
        internal static ProvisionRequest? Parse(string[] args)
        {
            string? Value(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[index + 1] : null;
            }

            if (!Guid.TryParse(Value("--object-id"), out var objectId) || objectId == Guid.Empty) return null;
            var (name, email, role) = (Value("--name"), Value("--email"), Value("--role"));
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(role)) return null;
            return new ProvisionRequest(objectId, name, email, role, Value("--department"));
        }
    }

    private static int ReportingUsage()
    {
        Console.Error.WriteLine($"""
            Usage: grant-reporting-access [--login <name>]   (default name: finops_reporting)
            Set the password in {ReportingPasswordVariable} first, so it never appears in the command line or history:
              $env:{ReportingPasswordVariable} = "<a strong password>"
            """);
        return 2;
    }

    private static int SeedExpensesUsage()
    {
        Console.Error.WriteLine("""
            Usage: seed-demo-expenses [--months <1-11>] [--for-user <object ID> [--per-month <1-20>]]
              --months    months of history, including this one (default 6)
              --for-user  also create expenses for this provisioned user, in their department
              --per-month expenses per month for that user (default 3); raising it on a rerun adds the difference
            """);
        return 2;
    }

    /// <summary>The arguments of <c>seed-demo-expenses</c>; null when a value is invalid.</summary>
    internal sealed record SeedExpensesRequest(int Months, Guid? ForUser, int PerMonth)
    {
        internal static SeedExpensesRequest? Parse(string[] args)
        {
            string? Value(string name)
            {
                var index = Array.IndexOf(args, name);
                if (index < 0) return null;
                return index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : "";
            }

            var months = 6;
            if (Value("--months") is { } m && (!int.TryParse(m, out months) || months is < 1 or > 11)) return null;
            Guid? forUser = null;
            if (Value("--for-user") is { } u)
            {
                if (!Guid.TryParse(u, out var id) || id == Guid.Empty) return null;
                forUser = id;
            }
            var perMonth = DemoExpenseGenerator.DefaultExtraPerMonth;
            if (Value("--per-month") is { } p)
            {
                // Only meaningful for the --for-user account.
                if (forUser is null || !int.TryParse(p, out perMonth)
                    || perMonth is < 1 or > DemoExpenseGenerator.MaximumExtraPerMonth) return null;
            }
            return new SeedExpensesRequest(months, forUser, perMonth);
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: grant-app-access <managed-identity-name> [<managed-identity-client-id>]");
        return 2;
    }
}

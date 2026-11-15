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

    /// <summary>
    /// Runs a command if the arguments name one; returns its exit code, or null to start the web host. Commands get a
    /// minimal host with only the data layer, so they need a connection string and nothing else (no Entra settings).
    /// </summary>
    internal static async Task<int?> TryRunAsync(string[] args)
    {
        var command = args.FirstOrDefault(a => a is SeedDemoData or Migrate or GrantAppAccess or ProvisionUser);
        if (command is null) return null;

        var builder = WebApplication.CreateSlimBuilder(args);
        // Synthetic demo identities exist only in Development; refuse before touching any database.
        if (command == SeedDemoData && !builder.Environment.IsDevelopment())
        {
            Console.Error.WriteLine("seed-demo-data is available only when ASPNETCORE_ENVIRONMENT=Development.");
            return 1;
        }

        // Validate before touching the database.
        ProvisionRequest? provision = null;
        if (command == ProvisionUser && (provision = ProvisionRequest.Parse(args)) is null) return ProvisionUsage();

        builder.Services.AddInfrastructure();
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        switch (command)
        {
            case SeedDemoData:
                Console.WriteLine($"Demo data seeded: {await DemoDataSeeder.SeedAsync(db)} row(s) inserted.");
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

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: grant-app-access <managed-identity-name> [<managed-identity-client-id>]");
        return 2;
    }
}

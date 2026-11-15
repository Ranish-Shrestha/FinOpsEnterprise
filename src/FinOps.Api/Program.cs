using System.Diagnostics;
using FinOps.Api;
using FinOps.Api.Security;
using FinOps.Application.Departments;
using FinOps.Application.Identity;
using FinOps.Infrastructure;
using FinOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure();
builder.Services.AddFinOpsSecurity(builder.Configuration, builder.Environment);
builder.Services.AddApiRateLimiting(builder.Configuration);

var app = builder.Build();

// `dotnet run --project src/FinOps.Api -- seed-demo-data` inserts synthetic demo identities.
// Development only: migrations never create users, so other environments start with no accounts.
if (args.Contains("seed-demo-data", StringComparer.Ordinal))
{
    if (!app.Environment.IsDevelopment())
    {
        Console.Error.WriteLine("seed-demo-data is available only when ASPNETCORE_ENVIRONMENT=Development.");
        return 1;
    }

    await using var scope = app.Services.CreateAsyncScope();
    var inserted = await DemoDataSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<FinOpsDbContext>());
    Console.WriteLine($"Demo data seeded: {inserted} row(s) inserted.");
    return 0;
}

app.UseCorrelationId();
app.UseApiSecurityHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = WriteHealthResponse
}).WithSummary("API liveness; does not contact SQL Server.").AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse
}).WithSummary("Readiness; verifies the current SQL Server schema.").AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

var api = app.MapGroup("/api").RequireRateLimiting(HttpPipeline.ApiRateLimitPolicy);

api.MapGet("/me", async (CurrentUserAccess access) =>
        await access.GetAsync() is { } user ? Results.Ok(user) : Results.Forbid())
    .WithName("GetCurrentUser").WithTags("Identity")
    .WithSummary("Current provisioned application profile and effective access.")
    .Produces<CurrentUserDto>()
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden);

api.MapGet("/departments", async (CurrentUserAccess access, IDepartmentReader reader,
        CancellationToken cancellationToken) =>
    {
        // The permission policy already loaded the user; this call reuses the per-request result.
        var user = await access.GetAsync();
        if (user is null) return Results.Forbid();
        var includeAll = user.Permissions.Contains(ApplicationPermissions.DepartmentsReadAll, StringComparer.Ordinal);
        return Results.Ok(await reader.GetAuthorizedAsync(user.DepartmentIds, includeAll, cancellationToken));
    })
    .RequireAuthorization(ApplicationPermissions.DepartmentsRead)
    .WithName("GetDepartments").WithTags("Organization")
    .WithSummary("Lists only departments authorized by application-owned membership and permissions.")
    .Produces<IReadOnlyList<DepartmentDto>>()
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden);

api.MapExpenseEndpoints();
api.MapBudgetEndpoints();
api.MapReportEndpoints();
api.MapMessagingEndpoints();

app.Run();
return 0;

static Task WriteHealthResponse(HttpContext context, HealthReport report) =>
    context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() });

public partial class Program { }

using System.Diagnostics;
using FinOps.Api;
using FinOps.Api.Security;
using FinOps.Application.Departments;
using FinOps.Application.Identity;
using FinOps.Infrastructure;
using FinOps.Infrastructure.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// seed-demo-data (Development), migrate, grant-app-access <identity>: one-shot commands, see AdminCommands.
if (await AdminCommands.TryRunAsync(args) is { } exitCode) return exitCode;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
builder.Services.AddFinOpsOpenApi(mockAuthentication: builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("Security:UseDevelopmentMockAuthentication"));
builder.Services.AddInfrastructure();
builder.Services.AddFinOpsSecurity(builder.Configuration, builder.Environment);
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.AddApiCors(builder.Configuration, builder.Environment);
// Scale-to-zero hosting (ADR 0007): the API drains the outbox while it is running, so nothing polls SQL when idle.
if (builder.Configuration.GetValue<bool>("Messaging:RunInApi")) builder.Services.AddOutboxWorker();
builder.AddFinOpsTelemetry("finops-api",
    tracing: t => t.AddAspNetCoreInstrumentation(options =>
        options.Filter = context => !context.Request.Path.StartsWithSegments("/health")),
    metrics: m => m.AddAspNetCoreInstrumentation());

var app = builder.Build();

app.UseCorrelationId();
app.UseApiSecurityHeaders();
app.UseCors();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseFinOpsSwaggerUi(); // Development only; static page, so it sits before authentication.
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

// Development only: the OpenAPI document that Swagger UI (/swagger) displays.
app.MapFinOpsOpenApi();

var api = app.MapGroup("/api").RequireRateLimiting(HttpPipeline.ApiRateLimitPolicy);

api.MapGet("/me", async (CurrentUserAccess access) =>
        await access.GetAsync() is { } user ? Results.Ok(user) : Results.Forbid())
    .WithName("GetCurrentUser").WithTags("Identity")
    .WithSummary("Current provisioned application profile and effective access.")
    .Produces<CurrentUserDto>()
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden);

api.MapSignInEndpoints();

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

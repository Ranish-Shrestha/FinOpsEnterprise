using FinOps.Infrastructure;
using FinOps.Infrastructure.Observability;

// Background worker: delivers outbox messages to their handlers (notifications, reimbursements).
// Run next to the API: `dotnet run --project src/FinOps.Worker`. Several instances can run at once.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddInfrastructure();
builder.Services.AddOutboxWorker();
builder.AddFinOpsTelemetry("finops-worker");

await builder.Build().RunAsync();

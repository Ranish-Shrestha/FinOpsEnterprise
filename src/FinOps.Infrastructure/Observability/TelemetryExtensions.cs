using System.Reflection;
using FinOps.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FinOps.Infrastructure.Observability;

/// <summary>
/// OpenTelemetry for the API and the worker. Export is opt-in: nothing is registered unless
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (environment variable or configuration), so tests and plain runs are
/// unaffected. Locally it points at the optional Aspire dashboard (docker compose --profile observability).
/// </summary>
public static class TelemetryExtensions
{
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static bool IsTelemetryEnabled(this IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]);

    public static IHostApplicationBuilder AddFinOpsTelemetry(this IHostApplicationBuilder builder, string serviceName,
        Action<TracerProviderBuilder>? tracing = null, Action<MeterProviderBuilder>? metrics = null)
    {
        if (!builder.Configuration.IsTelemetryEnabled()) return builder;
        if (!Uri.TryCreate(builder.Configuration[OtlpEndpointKey], UriKind.Absolute, out _))
            throw new InvalidOperationException($"{OtlpEndpointKey} must be an absolute URI, for example http://localhost:4317.");

        // Logs carry the active trace and span IDs, so a log line links straight to its trace.
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
        });

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: version)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(t =>
            {
                t.AddSource(FinOpsDiagnostics.SourceName).AddSqlClientInstrumentation();
                tracing?.Invoke(t);
            })
            .WithMetrics(m =>
            {
                // System.Runtime: the .NET runtime's built-in meter (GC, thread pool, exceptions).
                m.AddMeter(FinOpsDiagnostics.SourceName, "System.Runtime");
                metrics?.Invoke(m);
            })
            .UseOtlpExporter();
        return builder;
    }
}

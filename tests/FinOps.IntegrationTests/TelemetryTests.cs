using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using FinOps.Application.Common;
using FinOps.Application.Expenses;
using FinOps.Application.Messaging;
using FinOps.Infrastructure;
using FinOps.Infrastructure.Messaging;
using FinOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FinOps.IntegrationTests;

/// <summary>
/// Telemetry that operators rely on: one trace from the HTTP request through the outbox to the worker, and the metrics
/// for transitions, denials and deliveries. Uses BCL listeners, so it tests what the code emits independent of exporters.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class TelemetryTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task The_worker_continues_the_trace_of_the_request_that_wrote_the_event()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is FinOpsDiagnostics.SourceName or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var metrics = new MetricCapture("finops.expense.transitions", "finops.messaging.settled", "finops.messaging.delivery.latency");

        // A caller-supplied W3C trace context, as a browser or gateway would send it.
        var traceId = ActivityTraceId.CreateRandom();
        var expense = await SubmittedAsync();
        using (var manager = As(DemoDataSeeder.EngineeringManager))
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/expenses/{expense.Id}/approve"))
        {
            request.Headers.TryAddWithoutValidation("If-Match", expense.Version);
            request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
            Assert.Equal(HttpStatusCode.OK, (await manager.SendAsync(request)).StatusCode);
        }

        var message = await fixture.WithDbAsync(db => db.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Payload.Contains(expense.Id.ToString())));
        Assert.StartsWith($"00-{traceId}-", message.TraceParent);

        await using var worker = WorkerProvider();
        var processor = worker.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0) { }

        var process = Assert.Single(spans, s => s.OperationName == $"process {IntegrationEventTypes.ExpenseApproved}"
            && s.TraceId == traceId);
        Assert.Equal(ActivityKind.Consumer, process.Kind);
        Assert.Equal(message.Id.ToString(), process.GetTagItem("messaging.message.id"));
        Assert.Equal(message.CorrelationId, process.GetTagItem("finops.correlation_id"));
        var handler = Assert.Single(spans, s => s.ParentSpanId == process.SpanId);
        Assert.Equal("handle notifications.expense-status", handler.OperationName);
        Assert.Contains(spans, s => s.Source.Name == "Microsoft.AspNetCore" && s.TraceId == traceId);

        Assert.True(metrics.Sum("finops.expense.transitions", ("action", "approve")) >= 1);
        Assert.True(metrics.Sum("finops.messaging.settled", ("outcome", "completed")) >= 1);
        Assert.True(metrics.Count("finops.messaging.delivery.latency") >= 1);
    }

    [Fact]
    public async Task Failures_are_visible_as_error_spans_and_retry_metrics()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FinOpsDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var metrics = new MetricCapture("finops.messaging.settled");

        await using var worker = WorkerProvider(services => services.AddScoped<IIntegrationEventHandler, AlwaysFails>());
        var processor = worker.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0) { }

        var messageId = await fixture.WithDbAsync(async db =>
        {
            var m = FinOps.Domain.Messaging.OutboxMessage.Create(Guid.NewGuid(), AlwaysFails.MessageType, "{}", "telemetry-test",
                DateTimeOffset.UtcNow);
            db.OutboxMessages.Add(m);
            await db.SaveChangesAsync();
            return m.Id;
        });
        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0) { }

        var span = Assert.Single(spans, s => Equals(s.GetTagItem("messaging.message.id"), messageId.ToString()));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Contains(span.Events, e => e.Name == "exception");
        Assert.True(metrics.Sum("finops.messaging.settled", ("outcome", "retried")) >= 1);

        // Leave nothing pending for other tests.
        await fixture.WithDbAsync(db => db.OutboxMessages.Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeadLetteredAtUtc, DateTimeOffset.UtcNow)));
    }

    private ServiceProvider WorkerProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FinOps"] = fixture.ConnectionString,
        }).Build());
        services.AddLogging();
        services.AddInfrastructure();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private HttpClient As(DemoPersona persona) => ApiFactory.CreateHttpsClient(fixture.Factory, persona.EntraObjectId.ToString());

    private async Task<ExpenseDetailDto> SubmittedAsync()
    {
        using var employee = As(DemoDataSeeder.Employee);
        var created = await employee.PostAsJsonAsync("/api/expenses", new
        {
            departmentId = DemoDataSeeder.EngineeringDepartment, amount = 15m, currency = "USD", category = "Meals",
            description = "Telemetry test", expenseDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });
        var expense = (await created.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/expenses/{expense.Id}/submit");
        request.Headers.TryAddWithoutValidation("If-Match", expense.Version);
        var submitted = await employee.SendAsync(request);
        return (await submitted.Content.ReadFromJsonAsync<ExpenseDetailDto>())!;
    }

    private sealed class AlwaysFails : IIntegrationEventHandler
    {
        public const string MessageType = "test.telemetry-failure.v1";
        public string Name => "test.telemetry-failure";
        public bool CanHandle(string messageType) => messageType == MessageType;
        public Task HandleAsync(IntegrationMessage message, CancellationToken cancellationToken) =>
            throw new TimeoutException("Simulated dependency timeout.");
    }

    /// <summary>Collects measurements of the named FinOps instruments with their tags.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentBag<(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)> measurements = [];

        public MetricCapture(params string[] instruments)
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == FinOpsDiagnostics.SourceName && instruments.Contains(instrument.Name))
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((i, value, tags, _) => measurements.Add((i.Name, value, tags.ToArray())));
            listener.SetMeasurementEventCallback<double>((i, value, tags, _) => measurements.Add((i.Name, value, tags.ToArray())));
            listener.Start();
        }

        public double Sum(string instrument, (string Key, string Value) tag) => measurements
            .Where(m => m.Instrument == instrument && m.Tags.Any(t => t.Key == tag.Key && Equals(t.Value, tag.Value)))
            .Sum(m => m.Value);

        public int Count(string instrument) => measurements.Count(m => m.Instrument == instrument);

        public void Dispose() => listener.Dispose();
    }
}

/// <summary>Host wiring for telemetry; no database needed.</summary>
public sealed class TelemetryRegistrationTests
{
    [Fact]
    public void Telemetry_is_registered_only_when_an_otlp_endpoint_is_configured()
    {
        using (var plain = new ApiFactory())
        {
            Assert.Null(plain.Services.GetService<TracerProvider>());
            Assert.Null(plain.Services.GetService<MeterProvider>());
        }

        using (var exporting = new ApiFactory(settings: new Dictionary<string, string?>
               { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4317" }))
        {
            Assert.NotNull(exporting.Services.GetService<TracerProvider>());
            Assert.NotNull(exporting.Services.GetService<MeterProvider>());
        }

        using var invalid = new ApiFactory(settings: new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "not a uri" });
        Assert.Throws<InvalidOperationException>(() => invalid.Services);
    }
}

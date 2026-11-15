using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FinOps.Application.Common;

/// <summary>
/// The application's own traces and metrics, built on the BCL (<see cref="ActivitySource"/>, <see cref="Meter"/>) so the
/// domain-facing layers need no telemetry SDK. The hosts subscribe to <see cref="SourceName"/> and export via OpenTelemetry.
/// Tags are low-cardinality (action, outcome, message type, reason): never user, expense or department IDs.
/// </summary>
public static class FinOpsDiagnostics
{
    public const string SourceName = "FinOps";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> ExpenseTransitions = Meter.CreateCounter<long>(
        "finops.expense.transitions", unit: "{transition}", description: "Committed expense workflow transitions.");

    public static readonly Counter<long> AccessDenials = Meter.CreateCounter<long>(
        "finops.authorization.denials", unit: "{denial}", description: "Denied expense actions, by action and reason.");

    public static readonly Counter<long> MessagesSettled = Meter.CreateCounter<long>(
        "finops.messaging.settled", unit: "{message}",
        description: "Outbox deliveries by outcome: completed, retried, dead_lettered or lock_lost.");

    public static readonly Histogram<double> DeliveryLatency = Meter.CreateHistogram<double>(
        "finops.messaging.delivery.latency", unit: "s",
        description: "Time from the business change (outbox write) to the message being completed.");

    private static OutboxSnapshot outbox = new(0, 0, 0);

    static FinOpsDiagnostics()
    {
        Meter.CreateObservableGauge("finops.messaging.backlog", () => Volatile.Read(ref outbox).Pending,
            unit: "{message}", description: "Messages waiting for delivery (including scheduled retries).");
        Meter.CreateObservableGauge("finops.messaging.dead_letters", () => Volatile.Read(ref outbox).DeadLettered,
            unit: "{message}", description: "Dead-lettered messages awaiting an operator.");
        Meter.CreateObservableGauge("finops.messaging.oldest_pending.age", () => Volatile.Read(ref outbox).OldestPendingAgeSeconds,
            unit: "s", description: "Age of the oldest undelivered message; grows when the worker is down or stuck.");
    }

    /// <summary>Published by the worker after it samples the outbox; read by the observable gauges.</summary>
    public static void ReportOutbox(int pending, int deadLettered, double oldestPendingAgeSeconds) =>
        Volatile.Write(ref outbox, new OutboxSnapshot(pending, deadLettered, oldestPendingAgeSeconds));

    private sealed record OutboxSnapshot(int Pending, int DeadLettered, double OldestPendingAgeSeconds);
}

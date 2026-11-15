namespace FinOps.Domain.Messaging;

/// <summary>
/// A business event waiting to be delivered (transactional outbox). It is inserted in the same transaction as
/// the state change it describes, so an event exists if and only if the change was committed.
/// <para>
/// Delivery follows peek-lock semantics: a worker claims the message with a lease and a lock token, then
/// completes it, abandons it for a later retry, or dead-letters it. Only the holder of the current lock token
/// can settle a message, so a worker whose lease expired cannot overwrite another worker's outcome.
/// </para>
/// </summary>
public sealed class OutboxMessage
{
    public const int MaximumTypeLength = 100;
    public const int MaximumPayloadLength = 4000;
    public const int MaximumErrorLength = 1000;
    public const int MaximumCorrelationIdLength = 64;
    public const int MaximumTraceParentLength = 55;

    private OutboxMessage() { }

    /// <summary>Stable message ID. Handlers use it as their idempotency key, so it never changes on retry or replay.</summary>
    public Guid Id { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string? CorrelationId { get; private set; }

    /// <summary>W3C <c>traceparent</c> of the request that wrote the message, so the worker's span joins that trace.</summary>
    public string? TraceParent { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Number of times the message has been claimed (Service Bus "delivery count").</summary>
    public int DeliveryCount { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public Guid? LockToken { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }
    public string? LastError { get; private set; }

    public static OutboxMessage Create(Guid id, string type, string payload, string? correlationId, DateTimeOffset occurredAtUtc,
        string? traceParent = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("A message ID is required.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (type.Length > MaximumTypeLength) throw new ArgumentException("Message type is too long.", nameof(type));
        if (payload.Length > MaximumPayloadLength) throw new ArgumentException("Message payload is too long.", nameof(payload));
        if (occurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamps must be UTC.", nameof(occurredAtUtc));

        return new OutboxMessage
        {
            Id = id,
            Type = type,
            Payload = payload,
            CorrelationId = correlationId is { Length: > MaximumCorrelationIdLength } ? correlationId[..MaximumCorrelationIdLength] : correlationId,
            OccurredAtUtc = occurredAtUtc,
            NextAttemptAtUtc = occurredAtUtc,
            // Only a well-formed W3C value (55 chars); anything else is dropped rather than truncated.
            TraceParent = traceParent is { Length: MaximumTraceParentLength } ? traceParent : null
        };
    }

    /// <summary>Sends a dead-lettered message back to the queue with a fresh delivery count. Its ID is kept.</summary>
    public void Replay(DateTimeOffset nowUtc)
    {
        if (DeadLetteredAtUtc is null) throw new InvalidOperationException("Only dead-lettered messages can be replayed.");
        DeadLetteredAtUtc = null;
        DeliveryCount = 0;
        NextAttemptAtUtc = nowUtc;
        LockToken = null;
        LockedUntilUtc = null;
    }

    public static string TrimError(string error) =>
        error.Length <= MaximumErrorLength ? error : error[..MaximumErrorLength];
}

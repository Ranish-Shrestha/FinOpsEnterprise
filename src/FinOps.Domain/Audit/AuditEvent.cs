namespace FinOps.Domain.Audit;

public enum AuditOutcome { Succeeded, Denied }

/// <summary>
/// An append-only record of a business-critical action or a denied attempt. Metadata is a small,
/// allow-listed JSON object: never tokens, free-text descriptions, or personal data beyond IDs.
/// </summary>
public sealed class AuditEvent
{
    public const int MaximumActionLength = 64;
    public const int MaximumResourceTypeLength = 64;
    public const int MaximumCorrelationIdLength = 64;
    public const int MaximumMetadataLength = 1000;

    private AuditEvent() { }

    public long Id { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string ResourceType { get; private set; } = string.Empty;
    public Guid? ResourceId { get; private set; }
    public AuditOutcome Outcome { get; private set; }
    public string? CorrelationId { get; private set; }
    public string? Metadata { get; private set; }

    public static AuditEvent Record(DateTimeOffset occurredAtUtc, Guid? actorUserId, string action,
        string resourceType, Guid? resourceId, AuditOutcome outcome, string? correlationId, string? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        if (action.Length > MaximumActionLength) throw new ArgumentException("Action is too long.", nameof(action));
        if (resourceType.Length > MaximumResourceTypeLength) throw new ArgumentException("Resource type is too long.", nameof(resourceType));
        if (metadata?.Length > MaximumMetadataLength) throw new ArgumentException("Metadata is too long.", nameof(metadata));

        return new AuditEvent
        {
            OccurredAtUtc = occurredAtUtc,
            ActorUserId = actorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = outcome,
            CorrelationId = correlationId is { Length: > MaximumCorrelationIdLength } ? correlationId[..MaximumCorrelationIdLength] : correlationId,
            Metadata = metadata
        };
    }
}

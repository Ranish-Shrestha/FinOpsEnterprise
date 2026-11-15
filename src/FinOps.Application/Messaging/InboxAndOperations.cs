namespace FinOps.Application.Messaging;

/// <summary>A notification as its recipient sees it. Amount and description come from the expense at read time.</summary>
public sealed record NotificationDto(Guid Id, string Kind, Guid ExpenseId, string ExpenseDescription, decimal Amount,
    string Currency, DateTimeOffset CreatedAtUtc, DateTimeOffset? ReadAtUtc);

public sealed record NotificationInboxDto(int UnreadCount, IReadOnlyList<NotificationDto> Items);

/// <summary>Reads and updates only the caller's own notifications; the user ID always comes from the server.</summary>
public interface INotificationInbox
{
    public const int MaximumItems = 50;

    Task<NotificationInboxDto> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Returns false when the notification does not exist or belongs to someone else (both 404).</summary>
    Task<bool> MarkReadAsync(Guid userId, Guid notificationId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

public sealed record DeadLetterDto(Guid Id, string Type, int DeliveryCount, DateTimeOffset OccurredAtUtc,
    DateTimeOffset DeadLetteredAtUtc, string? LastError, string? CorrelationId);

public sealed record OutboxStatusDto(int Pending, int DeadLettered, IReadOnlyList<DeadLetterDto> DeadLetters);

/// <summary>Operator view of the outbox. Payloads are not exposed.</summary>
public interface IOutboxOperations
{
    public const int MaximumItems = 100;

    Task<OutboxStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns a dead-lettered message to the queue, keeping its ID so handlers that already committed stay
    /// committed, and records an audit event in the same transaction. False when no such dead letter exists.
    /// </summary>
    Task<bool> ReplayAsync(Guid messageId, Guid actorUserId, string? correlationId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}

using System.Text.Json;
using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;
using FinOps.Domain.Notifications;

namespace FinOps.Application.Messaging;

/// <summary>
/// Versioned event type names. A breaking payload change gets a new version (".v2"); the worker keeps a handler for
/// the old version until the outbox holds no more of its messages.
/// </summary>
public static class IntegrationEventTypes
{
    public const string ExpenseApproved = "expense.approved.v1";
    public const string ExpenseRejected = "expense.rejected.v1";
    public const string ExpensePosted = "expense.posted.v1";
}

/// <summary>Payload of every expense status event. IDs and amounts only: no description, comment or personal data.</summary>
public sealed record ExpenseStatusChanged(Guid ExpenseId, Guid RequesterId, Guid DepartmentId, decimal Amount,
    string Currency, Guid ActorUserId, DateTimeOffset OccurredAtUtc)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static ExpenseStatusChanged Deserialize(string payload) =>
        JsonSerializer.Deserialize<ExpenseStatusChanged>(payload, Json)
        ?? throw new InvalidMessageException("The message payload is empty.");
}

/// <summary>A claimed message as a handler sees it. <see cref="Id"/> is stable across retries and replays.</summary>
public sealed record IntegrationMessage(Guid Id, string Type, string Payload, string? CorrelationId, int DeliveryCount);

/// <summary>A message claimed from the queue under a lease. Settling it requires the matching lock token.</summary>
public sealed record ClaimedMessage(IntegrationMessage Message, Guid LockToken, DateTimeOffset LockedUntilUtc);

/// <summary>
/// The broker port, modeled on Service Bus peek-lock. The local implementation is the SQL outbox table itself;
/// an Azure Service Bus implementation would add a relay (outbox → topic) and a receiver behind the same calls.
/// Settle methods return false when the lock was lost (lease expired and another worker claimed the message).
/// </summary>
public interface IMessageQueue
{
    Task<IReadOnlyList<ClaimedMessage>> ClaimAsync(int maxMessages, TimeSpan lease, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(ClaimedMessage claimed, CancellationToken cancellationToken);
    Task<bool> AbandonAsync(ClaimedMessage claimed, DateTimeOffset retryAtUtc, string error, CancellationToken cancellationToken);
    Task<bool> DeadLetterAsync(ClaimedMessage claimed, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// One consumer of business events. A message can have several handlers; each runs in its own transaction with
/// its own idempotency record, so a retry after a partial failure re-runs only the handlers that did not commit.
/// Handlers add effects to <see cref="IMessageEffects"/> and never save; the processor commits the effects and the
/// idempotency record together.
/// </summary>
public interface IIntegrationEventHandler
{
    /// <summary>Stable name stored with each processed message. Renaming it makes old messages look unprocessed.</summary>
    string Name { get; }
    bool CanHandle(string messageType);
    Task HandleAsync(IntegrationMessage message, CancellationToken cancellationToken);
}

/// <summary>The unit of work handlers write to. Committed by the processor in one transaction.</summary>
public interface IMessageEffects
{
    void Add(Notification notification);
    void Add(Reimbursement reimbursement);
    void Add(AuditEvent auditEvent);
    Task<bool> ReimbursementExistsAsync(Guid expenseRequestId, CancellationToken cancellationToken);
}

/// <summary>A message that can never succeed (unknown type, unreadable payload). Dead-lettered without retrying.</summary>
public sealed class InvalidMessageException(string message) : Exception(message);

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>Deliveries before a failing message is dead-lettered (Service Bus MaxDeliveryCount).</summary>
    public int MaxDeliveryCount { get; set; } = 5;
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 60;
    public double PollIntervalSeconds { get; set; } = 2;
    public int RetryBaseSeconds { get; set; } = 5;
    public int RetryMaxSeconds { get; set; } = 300;
}

public static class RetryPolicy
{
    /// <summary>
    /// Exponential backoff after a failed delivery: base, 2×base, 4×base, … capped at max. Deterministic, so tests can
    /// assert the schedule; with several workers, claim order already spreads retries.
    /// </summary>
    public static TimeSpan DelayAfter(int deliveryCount, MessagingOptions options)
    {
        var exponent = Math.Clamp(deliveryCount - 1, 0, 20);
        var seconds = Math.Min(options.RetryBaseSeconds * Math.Pow(2, exponent), options.RetryMaxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}

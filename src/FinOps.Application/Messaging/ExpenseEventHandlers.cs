using System.Text.Json;
using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;
using FinOps.Domain.Notifications;

namespace FinOps.Application.Messaging;

/// <summary>Tells the requester that their expense was approved, rejected or posted.</summary>
public sealed class ExpenseNotificationHandler(IMessageEffects effects, TimeProvider clock) : IIntegrationEventHandler
{
    public string Name => "notifications.expense-status";

    public bool CanHandle(string messageType) => KindFor(messageType) is not null;

    public Task HandleAsync(IntegrationMessage message, CancellationToken cancellationToken)
    {
        var kind = KindFor(message.Type) ?? throw new InvalidMessageException($"Unexpected message type '{message.Type}'.");
        var payload = ExpenseStatusChanged.Deserialize(message.Payload);
        effects.Add(Notification.Create(payload.RequesterId, payload.ExpenseId, kind, message.Id, clock.GetUtcNow()));
        return Task.CompletedTask;
    }

    private static NotificationKind? KindFor(string messageType) => messageType switch
    {
        IntegrationEventTypes.ExpenseApproved => NotificationKind.ExpenseApproved,
        IntegrationEventTypes.ExpenseRejected => NotificationKind.ExpenseRejected,
        IntegrationEventTypes.ExpensePosted => NotificationKind.ExpensePosted,
        _ => null
    };
}

/// <summary>
/// Schedules exactly one reimbursement per posted expense and audits it as a system action, carrying the
/// correlation ID of the HTTP request that posted the expense.
/// </summary>
public sealed class ReimbursementHandler(IMessageEffects effects, TimeProvider clock) : IIntegrationEventHandler
{
    public string Name => "reimbursements.schedule";

    public bool CanHandle(string messageType) => messageType == IntegrationEventTypes.ExpensePosted;

    public async Task HandleAsync(IntegrationMessage message, CancellationToken cancellationToken)
    {
        var payload = ExpenseStatusChanged.Deserialize(message.Payload);

        // Idempotent by business key as well as by message ID: a different message about the same posted expense
        // (for example one written by a future reprocessing tool) must not pay twice either.
        if (await effects.ReimbursementExistsAsync(payload.ExpenseId, cancellationToken)) return;

        var now = clock.GetUtcNow();
        var reimbursement = Reimbursement.Schedule(payload.ExpenseId, payload.RequesterId, payload.Amount,
            payload.Currency, message.Id, now);
        effects.Add(reimbursement);
        effects.Add(AuditEvent.Record(now, actorUserId: null, "reimbursement.scheduled", "expense", payload.ExpenseId,
            AuditOutcome.Succeeded, message.CorrelationId,
            JsonSerializer.Serialize(new { reimbursementId = reimbursement.Id, messageId = message.Id })));
    }
}

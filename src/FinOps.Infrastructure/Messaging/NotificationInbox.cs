using System.Text.Json;
using FinOps.Application.Messaging;
using FinOps.Domain.Audit;
using FinOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Messaging;

internal sealed class NotificationInbox(FinOpsDbContext db) : INotificationInbox
{
    public async Task<NotificationInboxDto> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var unread = await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, cancellationToken);
        var rows = await (
                from n in db.Notifications.AsNoTracking()
                where n.UserId == userId
                orderby n.CreatedAtUtc descending, n.Id descending
                join e in db.ExpenseRequests on n.ExpenseRequestId equals e.Id
                select new { n, e.Description, e.Amount, e.Currency })
            .Take(INotificationInbox.MaximumItems)
            .ToListAsync(cancellationToken);

        return new NotificationInboxDto(unread, rows.Select(r => new NotificationDto(r.n.Id, r.n.Kind.ToString(),
            r.n.ExpenseRequestId, r.Description, r.Amount, r.Currency, r.n.CreatedAtUtc, r.n.ReadAtUtc)).ToList());
    }

    public async Task<bool> MarkReadAsync(Guid userId, Guid notificationId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        // Ownership is part of the lookup, so another user's notification behaves exactly like a missing one.
        var notification = await db.Notifications.SingleOrDefaultAsync(
            n => n.Id == notificationId && n.UserId == userId, cancellationToken);
        if (notification is null) return false;
        notification.MarkRead(nowUtc);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

internal sealed class OutboxOperations(FinOpsDbContext db) : IOutboxOperations
{
    public async Task<OutboxStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var pending = await db.OutboxMessages.CountAsync(m => m.ProcessedAtUtc == null && m.DeadLetteredAtUtc == null, cancellationToken);
        var deadLettered = await db.OutboxMessages.CountAsync(m => m.DeadLetteredAtUtc != null, cancellationToken);
        var items = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.DeadLetteredAtUtc != null)
            .OrderByDescending(m => m.DeadLetteredAtUtc)
            .Take(IOutboxOperations.MaximumItems)
            .Select(m => new DeadLetterDto(m.Id, m.Type, m.DeliveryCount, m.OccurredAtUtc, m.DeadLetteredAtUtc!.Value,
                m.LastError, m.CorrelationId))
            .ToListAsync(cancellationToken);
        return new OutboxStatusDto(pending, deadLettered, items);
    }

    public async Task<bool> ReplayAsync(Guid messageId, Guid actorUserId, string? correlationId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var message = await db.OutboxMessages.SingleOrDefaultAsync(
            m => m.Id == messageId && m.DeadLetteredAtUtc != null, cancellationToken);
        if (message is null) return false;

        message.Replay(nowUtc);
        db.AuditEvents.Add(AuditEvent.Record(nowUtc, actorUserId, "message.replayed", "outbox-message", messageId,
            AuditOutcome.Succeeded, correlationId, JsonSerializer.Serialize(new { type = message.Type })));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

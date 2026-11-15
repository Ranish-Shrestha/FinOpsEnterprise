using FinOps.Application.Messaging;
using FinOps.Domain.Messaging;
using FinOps.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Messaging;

/// <summary>
/// The local broker: the outbox table is the queue. Claims use <c>UPDLOCK, READPAST</c>, so concurrent workers skip
/// each other's rows instead of blocking or double-claiming, and each claim gets a fresh lock token. A crashed
/// worker's messages become claimable again when the lease ends (at-least-once delivery, which the handlers'
/// idempotency makes safe).
/// </summary>
internal sealed class SqlOutboxQueue(FinOpsDbContext db, TimeProvider clock) : IMessageQueue
{
    private const string ClaimSql = """
        WITH next AS (
            SELECT TOP (@max) *
            FROM messaging.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE ProcessedAtUtc IS NULL AND DeadLetteredAtUtc IS NULL
              AND NextAttemptAtUtc <= @now
              AND (LockedUntilUtc IS NULL OR LockedUntilUtc <= @now)
            ORDER BY NextAttemptAtUtc
        )
        UPDATE next
        SET DeliveryCount = DeliveryCount + 1, LockToken = NEWID(), LockedUntilUtc = @lockedUntil
        OUTPUT inserted.Id, inserted.Type, inserted.Payload, inserted.CorrelationId, inserted.DeliveryCount,
               inserted.LockToken, inserted.LockedUntilUtc;
        """;

    private sealed record ClaimRow(Guid Id, string Type, string Payload, string? CorrelationId, int DeliveryCount,
        Guid LockToken, DateTimeOffset LockedUntilUtc);

    public async Task<IReadOnlyList<ClaimedMessage>> ClaimAsync(int maxMessages, TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Database.SqlQueryRaw<ClaimRow>(ClaimSql,
                new SqlParameter("@max", System.Data.SqlDbType.Int) { Value = maxMessages },
                new SqlParameter("@now", System.Data.SqlDbType.DateTimeOffset) { Value = now },
                new SqlParameter("@lockedUntil", System.Data.SqlDbType.DateTimeOffset) { Value = now + lease })
            .ToListAsync(cancellationToken);

        return rows.OrderBy(r => r.Id).Select(r => new ClaimedMessage(
            new IntegrationMessage(r.Id, r.Type, r.Payload, r.CorrelationId, r.DeliveryCount),
            r.LockToken, r.LockedUntilUtc)).ToList();
    }

    public async Task<bool> CompleteAsync(ClaimedMessage claimed, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return await Locked(claimed).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.ProcessedAtUtc, now)
            .SetProperty(m => m.LockToken, (Guid?)null)
            .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null)
            .SetProperty(m => m.LastError, (string?)null), cancellationToken) == 1;
    }

    public async Task<bool> AbandonAsync(ClaimedMessage claimed, DateTimeOffset retryAtUtc, string error,
        CancellationToken cancellationToken)
    {
        var trimmed = OutboxMessage.TrimError(error);
        return await Locked(claimed).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.NextAttemptAtUtc, retryAtUtc)
            .SetProperty(m => m.LockToken, (Guid?)null)
            .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null)
            .SetProperty(m => m.LastError, trimmed), cancellationToken) == 1;
    }

    public async Task<bool> DeadLetterAsync(ClaimedMessage claimed, string reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var trimmed = OutboxMessage.TrimError(reason);
        return await Locked(claimed).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.DeadLetteredAtUtc, now)
            .SetProperty(m => m.LockToken, (Guid?)null)
            .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null)
            .SetProperty(m => m.LastError, trimmed), cancellationToken) == 1;
    }

    /// <summary>Settling requires the current lock token: a worker whose lease expired changes nothing.</summary>
    private IQueryable<OutboxMessage> Locked(ClaimedMessage claimed)
    {
        var id = claimed.Message.Id;
        var token = claimed.LockToken;
        return db.OutboxMessages.Where(m => m.Id == id && m.LockToken == token
            && m.ProcessedAtUtc == null && m.DeadLetteredAtUtc == null);
    }
}

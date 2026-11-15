namespace FinOps.Domain.Notifications;

public enum NotificationKind { ExpenseApproved, ExpenseRejected, ExpensePosted }

/// <summary>
/// An in-app message to one user about one of their expenses. Holds IDs only: the text is built when it is read,
/// so no description or comment is copied out of the expense.
/// </summary>
public sealed class Notification
{
    private Notification() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid ExpenseRequestId { get; private set; }
    public NotificationKind Kind { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ReadAtUtc { get; private set; }

    /// <summary>The outbox message that produced it. Unique per user, a database backstop against duplicates.</summary>
    public Guid SourceMessageId { get; private set; }

    public static Notification Create(Guid userId, Guid expenseRequestId, NotificationKind kind, Guid sourceMessageId,
        DateTimeOffset nowUtc)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user is required.", nameof(userId));
        if (expenseRequestId == Guid.Empty) throw new ArgumentException("An expense is required.", nameof(expenseRequestId));
        if (sourceMessageId == Guid.Empty) throw new ArgumentException("A source message is required.", nameof(sourceMessageId));
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unknown notification kind.", nameof(kind));
        if (nowUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamps must be UTC.", nameof(nowUtc));

        return new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ExpenseRequestId = expenseRequestId,
            Kind = kind,
            SourceMessageId = sourceMessageId,
            CreatedAtUtc = nowUtc
        };
    }

    /// <summary>Idempotent: marking a read notification again keeps the first read time.</summary>
    public void MarkRead(DateTimeOffset nowUtc) => ReadAtUtc ??= nowUtc;
}

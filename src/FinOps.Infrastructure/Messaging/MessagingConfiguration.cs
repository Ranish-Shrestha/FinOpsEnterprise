using FinOps.Domain.Expenses;
using FinOps.Domain.Identity;
using FinOps.Domain.Messaging;
using FinOps.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOps.Infrastructure.Messaging;

/// <summary>Idempotency record: handler <see cref="Handler"/> has committed its effects for message <see cref="MessageId"/>.</summary>
internal sealed class ProcessedMessage
{
    private ProcessedMessage() { }

    public ProcessedMessage(Guid messageId, string handler, DateTimeOffset processedAtUtc)
    {
        MessageId = messageId;
        Handler = handler;
        ProcessedAtUtc = processedAtUtc;
    }

    public Guid MessageId { get; private set; }
    public string Handler { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAtUtc { get; private set; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", "messaging", table =>
            table.HasCheckConstraint("CK_OutboxMessages_SingleOutcome",
                "[ProcessedAtUtc] IS NULL OR [DeadLetteredAtUtc] IS NULL"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Type).HasMaxLength(OutboxMessage.MaximumTypeLength).IsUnicode(false);
        builder.Property(x => x.Payload).HasMaxLength(OutboxMessage.MaximumPayloadLength);
        builder.Property(x => x.CorrelationId).HasMaxLength(OutboxMessage.MaximumCorrelationIdLength);
        builder.Property(x => x.LastError).HasMaxLength(OutboxMessage.MaximumErrorLength);
        builder.Property(x => x.TraceParent).HasMaxLength(OutboxMessage.MaximumTraceParentLength).IsUnicode(false);

        // The claim query: WHERE ProcessedAtUtc IS NULL AND DeadLetteredAtUtc IS NULL AND NextAttemptAtUtc <= @now
        // ORDER BY NextAttemptAtUtc. Filtered, so the index holds only the (small) backlog, never processed history.
        builder.HasIndex(x => x.NextAttemptAtUtc)
            .IncludeProperties(x => new { x.LockedUntilUtc })
            .HasFilter("[ProcessedAtUtc] IS NULL AND [DeadLetteredAtUtc] IS NULL")
            .HasDatabaseName("IX_OutboxMessages_Pending");
        builder.HasIndex(x => x.DeadLetteredAtUtc)
            .HasFilter("[DeadLetteredAtUtc] IS NOT NULL")
            .HasDatabaseName("IX_OutboxMessages_DeadLettered");
    }
}

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("ProcessedMessages", "messaging");
        // The primary key is the idempotency guarantee: a second commit for the same (message, handler) fails.
        builder.HasKey(x => new { x.MessageId, x.Handler });
        builder.Property(x => x.Handler).HasMaxLength(100).IsUnicode(false);
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications", "messaging");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExpenseRequest>().WithMany().HasForeignKey(x => x.ExpenseRequestId).OnDelete(DeleteBehavior.Restrict);
        // Backstop: one notification per user per event, even if idempotency tracking were bypassed.
        builder.HasIndex(x => new { x.SourceMessageId, x.UserId }).IsUnique().HasDatabaseName("UX_Notifications_Source_User");
        // The inbox: WHERE UserId = @me ORDER BY CreatedAtUtc DESC.
        builder.HasIndex(x => new { x.UserId, x.CreatedAtUtc })
            .IncludeProperties(x => new { x.ReadAtUtc })
            .HasDatabaseName("IX_Notifications_Inbox");
    }
}

internal sealed class ReimbursementConfiguration : IEntityTypeConfiguration<Reimbursement>
{
    public void Configure(EntityTypeBuilder<Reimbursement> builder)
    {
        builder.ToTable("Reimbursements", "finance", table =>
        {
            table.HasCheckConstraint("CK_Reimbursements_Amount_Positive", "[Amount] > 0");
            table.HasCheckConstraint("CK_Reimbursements_Currency_Iso", "LEN([Currency]) = 3");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Amount).HasPrecision(19, 4);
        builder.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.HasOne<ExpenseRequest>().WithMany().HasForeignKey(x => x.ExpenseRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.PayeeId).OnDelete(DeleteBehavior.Restrict);
        // The business rule "pay each posted expense once", enforced by the database.
        builder.HasIndex(x => x.ExpenseRequestId).IsUnique().HasDatabaseName("UX_Reimbursements_Expense");
    }
}

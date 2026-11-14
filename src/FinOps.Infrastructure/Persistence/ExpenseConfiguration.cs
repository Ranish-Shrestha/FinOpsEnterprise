using FinOps.Domain.Audit;
using FinOps.Domain.Expenses;
using FinOps.Domain.Identity;
using FinOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOps.Infrastructure.Persistence;

internal sealed class ExpenseRequestConfiguration : IEntityTypeConfiguration<ExpenseRequest>
{
    public void Configure(EntityTypeBuilder<ExpenseRequest> builder)
    {
        builder.ToTable("ExpenseRequests", "finance", table =>
        {
            // Database-level backstops for the domain rules; the domain gives the friendly errors.
            table.HasCheckConstraint("CK_ExpenseRequests_Amount_Positive", "[Amount] > 0");
            table.HasCheckConstraint("CK_ExpenseRequests_Currency_Iso", "LEN([Currency]) = 3");
            table.HasCheckConstraint("CK_ExpenseRequests_Status",
                "[Status] IN ('Draft','Submitted','Approved','Rejected','Posted','Cancelled')");
            table.HasCheckConstraint("CK_ExpenseRequests_Posted",
                "([Status] = 'Posted' AND [PostedAtUtc] IS NOT NULL AND [PostedById] IS NOT NULL) OR ([Status] <> 'Posted' AND [PostedAtUtc] IS NULL)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Amount).HasPrecision(19, 4);
        builder.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.Category).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Description).HasMaxLength(ExpenseRequest.MaximumDescriptionLength).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Financial records are never cascade-deleted.
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.PostedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);

        // Manager approval queue: WHERE DepartmentId IN (...) AND Status = 'Submitted' ORDER BY SubmittedAtUtc.
        builder.HasIndex(x => new { x.DepartmentId, x.Status, x.SubmittedAtUtc })
            .HasDatabaseName("IX_ExpenseRequests_ApprovalQueue");
        // "My expenses": WHERE RequesterId = @me ORDER BY CreatedAtUtc DESC.
        builder.HasIndex(x => new { x.RequesterId, x.CreatedAtUtc })
            .HasDatabaseName("IX_ExpenseRequests_Requester");
    }
}

internal sealed class ApprovalDecisionConfiguration : IEntityTypeConfiguration<ApprovalDecision>
{
    public void Configure(EntityTypeBuilder<ApprovalDecision> builder)
    {
        builder.ToTable("ApprovalDecisions", "finance", table =>
            table.HasCheckConstraint("CK_ApprovalDecisions_RejectionComment",
                "[Outcome] <> 'Rejected' OR LEN(LTRIM(RTRIM([Comment]))) > 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Comment).HasMaxLength(ExpenseRequest.MaximumCommentLength);
        builder.HasOne<ExpenseRequest>().WithMany().HasForeignKey(x => x.ExpenseRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.ApproverId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ExpenseRequestId, x.DecidedAtUtc });
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        // No foreign keys: audit history must outlive the rows it describes.
        builder.ToTable("AuditEvents", "audit");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityColumn();
        builder.Property(x => x.Action).HasMaxLength(AuditEvent.MaximumActionLength).IsUnicode(false).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(AuditEvent.MaximumResourceTypeLength).IsUnicode(false).IsRequired();
        builder.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.CorrelationId).HasMaxLength(AuditEvent.MaximumCorrelationIdLength).IsUnicode(false);
        builder.Property(x => x.Metadata).HasMaxLength(AuditEvent.MaximumMetadataLength);
        builder.HasIndex(x => x.OccurredAtUtc);
        builder.HasIndex(x => new { x.ResourceType, x.ResourceId });
    }
}

using FinOps.Domain.Audit;
using FinOps.Domain.Budgets;
using FinOps.Domain.Expenses;
using FinOps.Domain.Identity;
using FinOps.Domain.Messaging;
using FinOps.Domain.Notifications;
using FinOps.Infrastructure.Messaging;
using FinOps.Domain.Organization;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Persistence;

public sealed class FinOpsDbContext(DbContextOptions<FinOpsDbContext> options) : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<DepartmentMembership> DepartmentMemberships => Set<DepartmentMembership>();
    public DbSet<ExpenseRequest> ExpenseRequests => Set<ExpenseRequest>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    internal DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Reimbursement> Reimbursements => Set<Reimbursement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FinOpsDbContext).Assembly);
}

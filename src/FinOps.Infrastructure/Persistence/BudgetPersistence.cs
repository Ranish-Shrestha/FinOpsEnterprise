using System.Data;
using System.Text.Json;
using FinOps.Application.Budgets;
using FinOps.Application.Expenses;
using FinOps.Domain.Audit;
using FinOps.Domain.Budgets;
using FinOps.Domain.Organization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOps.Infrastructure.Persistence;

internal sealed class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    public void Configure(EntityTypeBuilder<Budget> builder)
    {
        builder.ToTable("Budgets", "finance", table =>
        {
            table.HasCheckConstraint("CK_Budgets_Amount_Positive", "[Amount] > 0");
            table.HasCheckConstraint("CK_Budgets_Month", "[Month] BETWEEN 1 AND 12");
            table.HasCheckConstraint("CK_Budgets_Currency_Iso", "LEN([Currency]) = 3");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Amount).HasPrecision(19, 4);
        builder.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        // One budget per department and month; also serves the report's (Year, Month) lookups per department.
        builder.HasIndex(x => new { x.DepartmentId, x.Year, x.Month }).IsUnique().HasDatabaseName("UX_Budgets_Department_Period");
    }
}

internal sealed class BudgetStore(FinOpsDbContext db) : IBudgetStore
{
    public Task<Budget?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        db.Budgets.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid departmentId, int year, int month, CancellationToken cancellationToken) =>
        db.Budgets.AnyAsync(x => x.DepartmentId == departmentId && x.Year == year && x.Month == month, cancellationToken);

    public Task<bool> DepartmentExistsAsync(Guid departmentId, CancellationToken cancellationToken) =>
        db.Departments.AnyAsync(x => x.Id == departmentId, cancellationToken);

    public void Add(Budget budget) => db.Budgets.Add(budget);
    public void Add(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);

    public async Task SaveChangesAsync(Budget? budget, byte[]? expectedVersion, CancellationToken cancellationToken)
    {
        if (budget is not null && expectedVersion is not null)
            db.Entry(budget).Property(x => x.RowVersion).OriginalValue = expectedVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Unique index UX_Budgets_Department_Period: another request created the same budget first.
            throw new DuplicateBudgetException();
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}

internal sealed class BudgetReader(FinOpsDbContext db) : IBudgetReader
{
    public async Task<IReadOnlyList<BudgetDto>> ListAsync(int year, DepartmentScope scope, CancellationToken cancellationToken)
    {
        var departmentIds = scope.DepartmentIds.ToList();
        var query = db.Budgets.AsNoTracking().Where(b => b.Year == year);
        if (!scope.All) query = query.Where(b => departmentIds.Contains(b.DepartmentId));
        var rows = await (from b in query
                          join d in db.Departments on b.DepartmentId equals d.Id
                          orderby d.Name, b.Month
                          select new { b, d.Name }).ToListAsync(cancellationToken);
        return rows.Select(r => ToDto(r.b, r.Name)).ToList();
    }

    public async Task<BudgetDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await (from b in db.Budgets.AsNoTracking()
                         where b.Id == id
                         join d in db.Departments on b.DepartmentId equals d.Id
                         select new { b, d.Name }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToDto(row.b, row.Name);
    }

    public async Task<IReadOnlyList<BudgetVsActualRow>> BudgetVsActualAsync(int year, int? month, DepartmentScope scope,
        CancellationToken cancellationToken)
    {
        // Typed parameters only. The department scope is a JSON array the procedure reads with OPENJSON;
        // NULL means "all departments" and is sent only for callers with budgets.read.all.
        var parameters = new[]
        {
            new SqlParameter("@Year", SqlDbType.Int) { Value = year },
            new SqlParameter("@Month", SqlDbType.Int) { Value = (object?)month ?? DBNull.Value },
            new SqlParameter("@DepartmentIds", SqlDbType.NVarChar, -1)
            {
                Value = scope.All ? DBNull.Value : JsonSerializer.Serialize(scope.DepartmentIds)
            },
        };

        var rows = await db.Database
            .SqlQueryRaw<BudgetVsActualRecord>("EXEC finance.usp_BudgetVsActual @Year, @Month, @DepartmentIds", parameters)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new BudgetVsActualRow(r.DepartmentId, r.DepartmentName, r.Year, r.Month, r.Budget,
            r.Posted, r.Committed, r.Pending, r.Remaining, r.UtilizationPercent)).ToList();
    }

    private static BudgetDto ToDto(Budget b, string departmentName) =>
        new(b.Id, b.DepartmentId, departmentName, b.Year, b.Month, b.Amount, b.Currency, b.UpdatedAtUtc,
            ExpenseVersion.ToETag(b.RowVersion));

    /// <summary>Column shape of finance.usp_BudgetVsActual.</summary>
    internal sealed class BudgetVsActualRecord
    {
        public Guid DepartmentId { get; set; }
        public string DepartmentName { get; set; } = string.Empty;
        public int Year { get; set; }
        public int Month { get; set; }
        public decimal? Budget { get; set; }
        public decimal Posted { get; set; }
        public decimal Committed { get; set; }
        public decimal Pending { get; set; }
        public decimal? Remaining { get; set; }
        public decimal? UtilizationPercent { get; set; }
    }
}

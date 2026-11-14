using FinOps.Application.Departments;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Persistence;

internal sealed class DepartmentReader(FinOpsDbContext dbContext) : IDepartmentReader
{
    // Department counts are small in this phase. The shared pagination contract arrives with
    // the first large collection (expenses); see docs/design/phase3-expenses.md.
    private const int MaximumDepartments = 200;

    public async Task<IReadOnlyList<DepartmentDto>> GetAuthorizedAsync(
        IReadOnlyCollection<Guid> departmentIds, bool includeAll, CancellationToken cancellationToken)
    {
        var query = dbContext.Departments.AsNoTracking();
        if (!includeAll)
        {
            query = query.Where(department => departmentIds.Contains(department.Id));
        }

        return await query
            .OrderBy(department => department.Name).ThenBy(department => department.Id)
            .Take(MaximumDepartments)
            .Select(department => new DepartmentDto(department.Id, department.Name))
            .ToListAsync(cancellationToken);
    }
}

namespace FinOps.Application.Departments;

public interface IDepartmentReader
{
    /// <summary>
    /// Returns departments the caller may see. The caller supplies server-resolved membership IDs;
    /// client-submitted department IDs must never be passed here.
    /// </summary>
    Task<IReadOnlyList<DepartmentDto>> GetAuthorizedAsync(
        IReadOnlyCollection<Guid> departmentIds, bool includeAll, CancellationToken cancellationToken);
}

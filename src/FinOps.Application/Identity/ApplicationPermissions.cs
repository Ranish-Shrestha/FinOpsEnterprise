namespace FinOps.Application.Identity;
public static class ApplicationPermissions
{
    public const string DepartmentsRead = "departments.read";
    public const string DepartmentsReadAll = "departments.read.all";
    public const string UserRolesManage = "user-roles.manage";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { DepartmentsRead, DepartmentsReadAll, UserRolesManage };
}

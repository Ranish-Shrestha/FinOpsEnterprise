namespace FinOps.Application.Identity;

/// <summary>
/// The application permission catalog. Every entry must also exist as migrated reference data
/// (PermissionConfiguration); PermissionCatalogTests enforces this. Each entry becomes an authorization policy.
/// </summary>
public static class ApplicationPermissions
{
    public const string DepartmentsRead = "departments.read";
    public const string DepartmentsReadAll = "departments.read.all";
    public const string UserRolesManage = "user-roles.manage";
    public const string ExpensesCreate = "expenses.create";
    public const string ExpensesReadDepartment = "expenses.read.department";
    public const string ExpensesReadAll = "expenses.read.all";
    public const string ExpensesApprove = "expenses.approve";
    public const string ExpensesPost = "expenses.post";
    public const string AuditRead = "audit.read";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DepartmentsRead, DepartmentsReadAll, UserRolesManage,
        ExpensesCreate, ExpensesReadDepartment, ExpensesReadAll, ExpensesApprove, ExpensesPost, AuditRead
    };

    public static bool Has(this CurrentUserDto user, string permission) =>
        user.Permissions.Contains(permission, StringComparer.Ordinal);
}

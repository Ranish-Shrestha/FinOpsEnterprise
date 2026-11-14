using FinOps.Application.Identity;
using FinOps.Domain.Expenses;

namespace FinOps.Application.Expenses;

public static class ExpenseActions
{
    public const string Edit = "edit";
    public const string Submit = "submit";
    public const string Withdraw = "withdraw";
    public const string Cancel = "cancel";
    public const string Revise = "revise";
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Post = "post";

    public static readonly IReadOnlySet<string> Transitions = new HashSet<string>(StringComparer.Ordinal)
        { Submit, Withdraw, Cancel, Revise, Approve, Reject, Post };
}

/// <summary>
/// The single statement of who may see and act on an expense. The list query in Infrastructure applies the
/// same visibility rule in SQL; tests cover both. Department access always comes from server-resolved
/// active memberships, never from the request.
/// </summary>
public static class ExpenseAccessPolicy
{
    public static ExpenseVisibility VisibilityFor(CurrentUserDto user) => new(
        user.Id,
        user.Has(ApplicationPermissions.ExpensesReadAll),
        user.Has(ApplicationPermissions.ExpensesReadDepartment),
        user.DepartmentIds);

    public static bool CanView(CurrentUserDto user, Guid requesterId, Guid departmentId) =>
        requesterId == user.Id
        || user.Has(ApplicationPermissions.ExpensesReadAll)
        || (user.Has(ApplicationPermissions.ExpensesReadDepartment) && user.DepartmentIds.Contains(departmentId));

    public static bool CanCreateIn(CurrentUserDto user, Guid departmentId) =>
        user.Has(ApplicationPermissions.ExpensesCreate) && user.DepartmentIds.Contains(departmentId);

    public static bool IsOwnerWithCreate(CurrentUserDto user, Guid requesterId) =>
        requesterId == user.Id && user.Has(ApplicationPermissions.ExpensesCreate);

    public static bool CanDecideIn(CurrentUserDto user, Guid departmentId) =>
        user.Has(ApplicationPermissions.ExpensesApprove) && user.DepartmentIds.Contains(departmentId);

    public static bool CanPost(CurrentUserDto user) => user.Has(ApplicationPermissions.ExpensesPost);

    /// <summary>
    /// Actions the caller could successfully take right now. The UI shows only these; the server re-checks
    /// everything on each request, so this list is a convenience, not a security control.
    /// </summary>
    public static IReadOnlyList<string> AllowedActions(CurrentUserDto user, Guid requesterId, Guid departmentId, ExpenseStatus status)
    {
        var actions = new List<string>();
        var owner = IsOwnerWithCreate(user, requesterId);
        var self = requesterId == user.Id;

        switch (status)
        {
            case ExpenseStatus.Draft when owner:
                actions.AddRange([ExpenseActions.Edit, ExpenseActions.Submit, ExpenseActions.Cancel]);
                break;
            case ExpenseStatus.Submitted:
                if (owner) actions.Add(ExpenseActions.Withdraw);
                if (!self && CanDecideIn(user, departmentId)) actions.AddRange([ExpenseActions.Approve, ExpenseActions.Reject]);
                break;
            case ExpenseStatus.Rejected when owner:
                actions.Add(ExpenseActions.Revise);
                break;
            case ExpenseStatus.Approved when !self && CanPost(user):
                actions.Add(ExpenseActions.Post);
                break;
        }

        return actions;
    }
}

/// <summary>Strong ETags over the SQL Server rowversion: <c>"base64(rowversion)"</c>.</summary>
public static class ExpenseVersion
{
    public static string ToETag(byte[] rowVersion) => $"\"{Convert.ToBase64String(rowVersion)}\"";

    public static bool TryParseIfMatch(string? header, out byte[] rowVersion)
    {
        rowVersion = [];
        if (string.IsNullOrWhiteSpace(header)) return false;
        var value = header.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal)) return false; // Weak ETags cannot guard writes.
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return false;
        try
        {
            rowVersion = Convert.FromBase64String(value[1..^1]);
            return rowVersion.Length == 8;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

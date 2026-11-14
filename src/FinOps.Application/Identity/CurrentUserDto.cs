namespace FinOps.Application.Identity;
public sealed record CurrentUserDto(Guid Id, string DisplayName, string Email,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions, IReadOnlyList<Guid> DepartmentIds);

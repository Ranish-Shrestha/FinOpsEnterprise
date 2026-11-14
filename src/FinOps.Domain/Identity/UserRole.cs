namespace FinOps.Domain.Identity;

public sealed class UserRole
{
    private UserRole() { }

    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }

    public static UserRole Assign(Guid userId, Guid roleId)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user ID is required.", nameof(userId));
        if (roleId == Guid.Empty) throw new ArgumentException("A role ID is required.", nameof(roleId));
        return new UserRole { UserId = userId, RoleId = roleId };
    }
}

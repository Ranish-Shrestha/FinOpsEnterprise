namespace FinOps.Domain.Identity;
public sealed class RolePermission
{
    private RolePermission() { }
    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }
}

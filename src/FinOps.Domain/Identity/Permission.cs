namespace FinOps.Domain.Identity;
public sealed class Permission
{
    public const int MaximumNameLength = 120;
    private Permission() { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
}

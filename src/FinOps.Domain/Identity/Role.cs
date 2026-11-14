namespace FinOps.Domain.Identity;
public sealed class Role
{
    public const int MaximumNameLength = 80;
    private Role() { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
}

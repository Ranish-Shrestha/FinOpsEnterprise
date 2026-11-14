namespace FinOps.Domain.Organization;

public sealed class Department
{
    public const int MaximumNameLength = 100;
    private Department() { }
    private Department(string name) { Id = Guid.NewGuid(); Name = name; }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;

    public static Department Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmedName = name.Trim();
        if (trimmedName.Length > MaximumNameLength)
            throw new ArgumentException($"Department names must be at most {MaximumNameLength} characters.", nameof(name));
        return new Department(trimmedName);
    }
}

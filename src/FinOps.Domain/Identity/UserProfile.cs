namespace FinOps.Domain.Identity;

public sealed class UserProfile
{
    public const int MaximumDisplayNameLength = 150;
    public const int MaximumEmailLength = 254;

    private UserProfile() { }

    public Guid Id { get; private set; }
    public Guid EntraObjectId { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }

    /// <summary>Provisions an application user for an immutable Entra object ID.</summary>
    public static UserProfile Provision(Guid id, Guid entraObjectId, string displayName, string email, bool isActive = true)
    {
        if (id == Guid.Empty) throw new ArgumentException("A user ID is required.", nameof(id));
        if (entraObjectId == Guid.Empty) throw new ArgumentException("An Entra object ID is required.", nameof(entraObjectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var trimmedName = displayName.Trim();
        var trimmedEmail = email.Trim();
        if (trimmedName.Length > MaximumDisplayNameLength)
            throw new ArgumentException($"Display names must be at most {MaximumDisplayNameLength} characters.", nameof(displayName));
        if (trimmedEmail.Length > MaximumEmailLength || !trimmedEmail.Contains('@', StringComparison.Ordinal))
            throw new ArgumentException("A valid email address is required.", nameof(email));

        return new UserProfile
        {
            Id = id,
            EntraObjectId = entraObjectId,
            DisplayName = trimmedName,
            Email = trimmedEmail,
            IsActive = isActive
        };
    }

    public void Deactivate() => IsActive = false;
}

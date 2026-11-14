namespace FinOps.Domain.Identity;

/// <summary>A dated membership. It grants department access only while <see cref="IsActiveAt"/> is true.</summary>
public sealed class DepartmentMembership
{
    private DepartmentMembership() { }

    public Guid UserId { get; private set; }
    public Guid DepartmentId { get; private set; }
    public DateTimeOffset StartsAtUtc { get; private set; }
    public DateTimeOffset? EndsAtUtc { get; private set; }

    public static DepartmentMembership Create(Guid userId, Guid departmentId,
        DateTimeOffset startsAtUtc, DateTimeOffset? endsAtUtc = null)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user ID is required.", nameof(userId));
        if (departmentId == Guid.Empty) throw new ArgumentException("A department ID is required.", nameof(departmentId));
        if (startsAtUtc.Offset != TimeSpan.Zero || endsAtUtc is { Offset.Ticks: not 0 })
            throw new ArgumentException("Membership dates must be UTC.");
        if (endsAtUtc <= startsAtUtc)
            throw new ArgumentException("A membership must end after it starts.", nameof(endsAtUtc));

        return new DepartmentMembership
        {
            UserId = userId,
            DepartmentId = departmentId,
            StartsAtUtc = startsAtUtc,
            EndsAtUtc = endsAtUtc
        };
    }

    /// <summary>Start is inclusive, end is exclusive. Mirrors the SQL filter in the access reader.</summary>
    public bool IsActiveAt(DateTimeOffset instant) =>
        StartsAtUtc <= instant && (EndsAtUtc is null || EndsAtUtc > instant);
}

using FinOps.Domain.Identity;

namespace FinOps.UnitTests;

public sealed class UserProfileTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly Guid ObjectId = Guid.NewGuid();

    [Fact]
    public void Provisioning_trims_values_and_defaults_to_active()
    {
        var user = UserProfile.Provision(Id, ObjectId, "  Demo User ", " demo@example.invalid ");
        Assert.Equal("Demo User", user.DisplayName);
        Assert.Equal("demo@example.invalid", user.Email);
        Assert.True(user.IsActive);
    }

    [Fact]
    public void Empty_identifiers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => UserProfile.Provision(Guid.Empty, ObjectId, "Demo", "d@example.invalid"));
        Assert.Throws<ArgumentException>(() => UserProfile.Provision(Id, Guid.Empty, "Demo", "d@example.invalid"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Invalid_emails_are_rejected(string email) =>
        Assert.ThrowsAny<ArgumentException>(() => UserProfile.Provision(Id, ObjectId, "Demo", email));

    [Fact]
    public void Display_names_over_the_storage_limit_are_rejected() =>
        Assert.Throws<ArgumentException>(() => UserProfile.Provision(
            Id, ObjectId, new string('a', UserProfile.MaximumDisplayNameLength + 1), "d@example.invalid"));

    [Fact]
    public void Deactivation_is_recorded()
    {
        var user = UserProfile.Provision(Id, ObjectId, "Demo", "d@example.invalid");
        user.Deactivate();
        Assert.False(user.IsActive);
    }
}

public sealed class DepartmentMembershipTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Start_is_inclusive_and_end_is_exclusive()
    {
        var membership = DepartmentMembership.Create(Guid.NewGuid(), Guid.NewGuid(), Start, Start.AddDays(10));
        Assert.False(membership.IsActiveAt(Start.AddTicks(-1)));
        Assert.True(membership.IsActiveAt(Start));
        Assert.True(membership.IsActiveAt(Start.AddDays(10).AddTicks(-1)));
        Assert.False(membership.IsActiveAt(Start.AddDays(10)));
    }

    [Fact]
    public void Open_ended_memberships_stay_active() =>
        Assert.True(DepartmentMembership.Create(Guid.NewGuid(), Guid.NewGuid(), Start).IsActiveAt(Start.AddYears(5)));

    [Fact]
    public void Membership_must_end_after_it_starts()
    {
        Assert.Throws<ArgumentException>(() => DepartmentMembership.Create(Guid.NewGuid(), Guid.NewGuid(), Start, Start));
        Assert.Throws<ArgumentException>(() => DepartmentMembership.Create(Guid.NewGuid(), Guid.NewGuid(), Start, Start.AddDays(-1)));
    }

    [Fact]
    public void Non_utc_dates_are_rejected() =>
        Assert.Throws<ArgumentException>(() => DepartmentMembership.Create(
            Guid.NewGuid(), Guid.NewGuid(), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5))));

    [Fact]
    public void Empty_identifiers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => DepartmentMembership.Create(Guid.Empty, Guid.NewGuid(), Start));
        Assert.Throws<ArgumentException>(() => DepartmentMembership.Create(Guid.NewGuid(), Guid.Empty, Start));
        Assert.Throws<ArgumentException>(() => UserRole.Assign(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => UserRole.Assign(Guid.NewGuid(), Guid.Empty));
    }
}

using FinOps.Domain.Organization;

namespace FinOps.UnitTests;

public sealed class DepartmentTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Blank_names_are_rejected(string name) =>
        Assert.Throws<ArgumentException>(() => Department.Create(name));

    [Fact]
    public void Null_name_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => Department.Create(null!));

    [Fact]
    public void Names_are_trimmed_and_department_ids_are_unique()
    {
        var first = Department.Create("  Demo Finance  ");
        var second = Department.Create("Demo Finance");
        Assert.Equal("Demo Finance", first.Name);
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Names_at_the_storage_limit_are_accepted() =>
        Assert.Equal(100, Department.Create(new string('a', 100)).Name.Length);

    [Fact]
    public void Names_over_the_storage_limit_are_rejected() =>
        Assert.Throws<ArgumentException>(() => Department.Create(new string('a', 101)));
}

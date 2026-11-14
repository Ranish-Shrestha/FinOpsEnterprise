using FinOps.Application.Identity;
using FinOps.Domain.Identity;
using FinOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FinOps.IntegrationTests;

/// <summary>Model-only checks. They build the EF model but never open a database connection.</summary>
public sealed class PermissionCatalogTests
{
    private static IReadOnlyList<IDictionary<string, object?>> SeedRows<TEntity>()
    {
        var options = new DbContextOptionsBuilder<FinOpsDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=ModelOnly;Integrated Security=True").Options;
        using var db = new FinOpsDbContext(options);
        var model = db.GetService<IDesignTimeModel>().Model;
        return model.FindEntityType(typeof(TEntity))!.GetSeedData().ToList();
    }

    [Fact]
    public void Code_permission_catalog_matches_migrated_permissions()
    {
        var seeded = SeedRows<Permission>().Select(row => (string)row[nameof(Permission.Name)]!).ToHashSet();
        Assert.True(ApplicationPermissions.All.SetEquals(seeded),
            $"Code: [{string.Join(", ", ApplicationPermissions.All.Order())}] Seed: [{string.Join(", ", seeded.Order())}]");
    }

    [Fact]
    public void Migrations_do_not_ship_user_accounts_or_memberships()
    {
        Assert.Empty(SeedRows<UserProfile>());
        Assert.Empty(SeedRows<UserRole>());
        Assert.Empty(SeedRows<DepartmentMembership>());
    }
}

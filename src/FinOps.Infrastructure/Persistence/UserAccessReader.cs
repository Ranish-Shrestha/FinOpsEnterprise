using FinOps.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Persistence;

/// <summary>
/// Loads the caller's profile, roles, permissions and active department memberships. This runs on every API request,
/// so it is one round trip: EF Core translates the three collection projections into a single SQL statement.
/// (It used to be four sequential queries; see docs/performance/expense-queries.md.)
/// </summary>
internal sealed class UserAccessReader(FinOpsDbContext dbContext, TimeProvider clock) : IUserAccessReader
{
    public async Task<CurrentUserDto?> FindByEntraObjectIdAsync(Guid entraObjectId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var row = await dbContext.UserProfiles.AsNoTracking()
            .Where(u => u.EntraObjectId == entraObjectId && u.IsActive)
            .Select(u => new
            {
                u.Id,
                u.DisplayName,
                u.Email,
                Roles = (from ur in dbContext.UserRoles
                         join role in dbContext.Roles on ur.RoleId equals role.Id
                         where ur.UserId == u.Id
                         select role.Name).ToList(),
                Permissions = (from ur in dbContext.UserRoles
                               join rp in dbContext.RolePermissions on ur.RoleId equals rp.RoleId
                               join permission in dbContext.Permissions on rp.PermissionId equals permission.Id
                               where ur.UserId == u.Id
                               select permission.Name).ToList(),
                // Half-open interval [StartsAtUtc, EndsAtUtc), the same rule as DepartmentMembership.IsActiveAt.
                Departments = dbContext.DepartmentMemberships
                    .Where(m => m.UserId == u.Id && m.StartsAtUtc <= now && (m.EndsAtUtc == null || m.EndsAtUtc > now))
                    .Select(m => m.DepartmentId).ToList()
            })
            .AsSingleQuery()
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        // The single query multiplies rows across the three collections; de-duplicate and order in memory.
        return new CurrentUserDto(row.Id, row.DisplayName, row.Email,
            row.Roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            row.Permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            row.Departments.Distinct().ToList());
    }
}

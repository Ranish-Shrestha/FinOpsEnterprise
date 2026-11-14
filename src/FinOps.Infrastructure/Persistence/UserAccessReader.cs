using FinOps.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Persistence;

internal sealed class UserAccessReader(FinOpsDbContext dbContext) : IUserAccessReader
{
    public async Task<CurrentUserDto?> FindByEntraObjectIdAsync(Guid entraObjectId, CancellationToken cancellationToken)
    {
        var user = await dbContext.UserProfiles.AsNoTracking()
            .Where(x => x.EntraObjectId == entraObjectId && x.IsActive)
            .Select(x => new { x.Id, x.DisplayName, x.Email }).SingleOrDefaultAsync(cancellationToken);
        if (user is null) return null;
        var roles = await (from ur in dbContext.UserRoles.AsNoTracking() join role in dbContext.Roles on ur.RoleId equals role.Id
            where ur.UserId == user.Id orderby role.Name select role.Name).ToListAsync(cancellationToken);
        var permissions = await (from ur in dbContext.UserRoles.AsNoTracking()
            join rp in dbContext.RolePermissions on ur.RoleId equals rp.RoleId
            join permission in dbContext.Permissions on rp.PermissionId equals permission.Id
            where ur.UserId == user.Id orderby permission.Name select permission.Name).Distinct().ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var departments = await dbContext.DepartmentMemberships.AsNoTracking()
            .Where(x => x.UserId == user.Id && x.StartsAtUtc <= now && (x.EndsAtUtc == null || x.EndsAtUtc > now))
            .Select(x => x.DepartmentId).Distinct().ToListAsync(cancellationToken);
        return new(user.Id, user.DisplayName, user.Email, roles, permissions, departments);
    }
}

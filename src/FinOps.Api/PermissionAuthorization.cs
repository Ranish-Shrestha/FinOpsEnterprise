using FinOps.Application.Identity;
using Microsoft.AspNetCore.Authorization;

namespace FinOps.Api.Security;

internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>
/// Grants a permission requirement only when the provisioned, active application user holds that permission.
/// Token claims never supply permissions; they only identify the caller by immutable Entra object ID.
/// </summary>
internal sealed class PermissionAuthorizationHandler(CurrentUserAccess access)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = await access.GetAsync();
        if (user?.Permissions.Contains(requirement.Permission, StringComparer.Ordinal) == true)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Resolves the current caller's application access once per request. Authorization handlers and
/// endpoints share the same result, so a request performs a single access lookup.
/// </summary>
internal sealed class CurrentUserAccess(IHttpContextAccessor accessor, IUserAccessReader users)
{
    private Task<CurrentUserDto?>? cached;

    public Task<CurrentUserDto?> GetAsync() => cached ??= LoadAsync();

    private Task<CurrentUserDto?> LoadAsync()
    {
        var context = accessor.HttpContext;
        if (context is null || !Guid.TryParse(context.User.FindFirst(ClaimNames.ObjectId)?.Value, out var objectId))
        {
            return Task.FromResult<CurrentUserDto?>(null);
        }

        return users.FindByEntraObjectIdAsync(objectId, context.RequestAborted);
    }
}

internal static class ClaimNames
{
    internal const string ObjectId = "oid";
    internal const string Scope = "scp";
}

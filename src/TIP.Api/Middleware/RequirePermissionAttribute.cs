using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TIP.Api.Middleware;

/// <summary>
/// Authorization filter that requires the authenticated user to have a specific permission
/// in their JWT claims.
///
/// Design rationale:
/// - Permissions are stored as individual "permission" claims in the JWT.
/// - Admin role has all permissions; this filter checks per-endpoint.
/// - Used on controller actions: [RequirePermission("admin.users")]
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : Attribute, IAuthorizationFilter
{
    private readonly string _permission;

    /// <summary>
    /// Requires the specified permission claim in the JWT.
    /// </summary>
    /// <param name="permission">Permission string (e.g., "admin.users").</param>
    public RequirePermissionAttribute(string permission)
    {
        _permission = permission;
    }

    /// <summary>
    /// Checks if the user has the required permission.
    /// </summary>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;

        if (user.Identity == null || !user.Identity.IsAuthenticated)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var permissions = user.Claims
            .Where(c => c.Type == "permission")
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!permissions.Contains(_permission))
        {
            context.Result = new ForbidResult();
        }
    }
}

using GSTAutoPilot.Application.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GSTAutoPilot.API.Security;

/// <summary>
/// Requires the caller's JWT to carry a "perm" claim for the given module key.
/// Admins bypass the check — they hold every permission by definition.
/// Apply alongside [Authorize]; this filter only inspects an already-authenticated principal.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresPermissionAttribute : Attribute, IAuthorizationFilter
{
    private readonly string _permission;

    public RequiresPermissionAttribute(string permission) => _permission = permission;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            // Leave the 401 to the authentication middleware.
            return;
        }

        if (user.IsInRole("Admin")) return;

        var granted = user.Claims.Any(
            c => c.Type == "perm" && string.Equals(c.Value, _permission, StringComparison.OrdinalIgnoreCase));

        if (!granted)
        {
            context.Result = new ObjectResult(new
            {
                error = $"Your account does not have access to the '{_permission}' module.",
            })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }
    }
}

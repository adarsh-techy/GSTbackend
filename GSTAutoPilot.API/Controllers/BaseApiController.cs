using System.Security.Claims;
using GSTAutoPilot.Domain.DTOs.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public abstract class BaseApiController : ControllerBase
{
    /// <summary>
    /// Authenticated User ID from JWT Claims.
    /// </summary>
    protected string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    /// <summary>
    /// Authenticated User Email from JWT Claims.
    /// </summary>
    protected string CurrentUserEmail => User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email") ?? string.Empty;

    /// <summary>
    /// Current Tenant ID from JWT Claims or request headers.
    /// </summary>
    protected string CurrentTenantId => User.FindFirstValue("tenant_id") ?? Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// Checks if the current authenticated user has an Admin role.
    /// </summary>
    protected bool IsAdmin => User.IsInRole("Admin") || User.FindFirstValue("is_admin") == "true";

    /// <summary>
    /// Helper to return a standardized 200 OK success response envelope.
    /// </summary>
    protected IActionResult ResultOk<T>(T data, string message = "Operation completed successfully")
    {
        return Ok(ApiResponse<T>.Ok(data, message));
    }

    /// <summary>
    /// Helper to return a standardized error response envelope.
    /// </summary>
    protected IActionResult ResultFail<T>(string message, List<string>? errors = null, int statusCode = StatusCodes.Status400BadRequest)
    {
        var response = ApiResponse<T>.Fail(message, errors);
        return statusCode switch
        {
            StatusCodes.Status401Unauthorized => Unauthorized(response),
            StatusCodes.Status403Forbidden => StatusCode(StatusCodes.Status403Forbidden, response),
            StatusCodes.Status404NotFound => NotFound(response),
            _ => BadRequest(response)
        };
    }
}

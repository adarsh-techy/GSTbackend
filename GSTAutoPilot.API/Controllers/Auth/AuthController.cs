using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.API.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest("Username is required.");
        }

        LoginResult? result;
        try
        {
            result = await _authService.LoginAsync(
                new LoginCommand { Username = request.Username, Password = request.Password },
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        if (result is null)
        {
            return Unauthorized(new { error = "Invalid credentials or user not provisioned for this tenant." });
        }

        return Ok(new LoginResponse
        {
            AccessToken = result.AccessToken,
            ExpiresAt = result.ExpiresAt,
            EmplCode = result.EmplCode,
            DisplayName = result.DisplayName,
            Role = result.Role,
            TenantId = result.TenantId,
        });
    }
}

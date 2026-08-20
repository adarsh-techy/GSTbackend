using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/user-roles")]
[Authorize(Roles = "Admin")]
public class UserRolesController : ControllerBase
{
    private readonly IUserRolesService _service;

    public UserRolesController(IUserRolesService service)
    {
        _service = service;
    }

    [HttpGet("employees")]
    public async Task<ActionResult<IReadOnlyList<CarolEmployeeDto>>> Employees(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.ListCarolEmployeesAsync(cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserRoleDto>>> List(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.ListUserRolesAsync(cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<UserRoleDto>> Add(
        [FromBody] AddUserRoleCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.AddAsync(command, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{userRoleId:int}")]
    public async Task<ActionResult> Remove(int userRoleId, CancellationToken cancellationToken)
    {
        try
        {
            var removed = await _service.RemoveAsync(userRoleId, cancellationToken);
            return removed ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

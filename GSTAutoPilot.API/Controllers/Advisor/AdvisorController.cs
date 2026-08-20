using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/advisor")]
[Authorize]
public class AdvisorController : ControllerBase
{
    private readonly IGstAdvisorService _advisor;

    public AdvisorController(IGstAdvisorService advisor)
    {
        _advisor = advisor;
    }

    // Lets the UI hide the advisor entirely on servers where it isn't configured.
    [HttpGet("status")]
    public IActionResult Status() => Ok(new { enabled = _advisor.IsEnabled });

    [HttpPost("chat")]
    public async Task<ActionResult<AdvisorChatResponse>> Chat(
        [FromBody] AdvisorChatRequest request,
        CancellationToken cancellationToken)
    {
        if (!_advisor.IsEnabled)
        {
            return StatusCode(503, new { error = "The GST advisor is not enabled on this server." });
        }

        try
        {
            return Ok(await _advisor.ChatAsync(request, cancellationToken));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}

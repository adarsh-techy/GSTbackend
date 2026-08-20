using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Exceptions;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/gstr2b")]
[Authorize]
public class Gstr2bController : ControllerBase
{
    private readonly IGstr2bService _gstr2bService;

    public Gstr2bController(IGstr2bService gstr2bService)
    {
        _gstr2bService = gstr2bService;
    }

    [HttpGet("fetch/{period}")]
    public async Task<ActionResult<Gstr2bFetchResponse>> Fetch(string period, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _gstr2bService.FetchAsync(period, cancellationToken);
            return Ok(response);
        }
        catch (GstnNotConnectedException ex)
        {
            // 409: prerequisites unmet (no GSP config / no OTP session). The UI
            // uses `code`/`reason` to prompt the user to connect rather than
            // treating it as a hard error.
            return Conflict(new { error = ex.Message, code = "NOT_CONNECTED", reason = ex.Reason });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("{period}")]
    public async Task<ActionResult<Gstr2bFetchResponse>> Get(string period, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _gstr2bService.GetAsync(period, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

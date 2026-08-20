using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/gstr3b")]
[Authorize]
public class Gstr3bController : ControllerBase
{
    private readonly IGstr3bService _gstr3bService;

    public Gstr3bController(IGstr3bService gstr3bService)
    {
        _gstr3bService = gstr3bService;
    }

    [HttpGet]
    public async Task<ActionResult<Gstr3bResponse>> Get(
        [FromQuery] string period,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(period) || period.Length != 6
            || !int.TryParse(period.AsSpan(0, 4), out var year)
            || !int.TryParse(period.AsSpan(4, 2), out var month)
            || month < 1 || month > 12)
        {
            return BadRequest("period must be in YYYYMM format (e.g. 202604).");
        }

        var result = await _gstr3bService.ComputeAsync(year, month, cancellationToken);
        return Ok(result);
    }

    [HttpGet("trend")]
    public async Task<ActionResult<Gstr3bTrendResponse>> Trend(
        [FromQuery] string period,
        [FromQuery] int months = 6,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _gstr3bService.ComputeTrendAsync(period, months, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/carolerp")]
public class CarolErpController : ControllerBase
{
    private readonly ICarolErpPeriodsService _periodsService;

    public CarolErpController(ICarolErpPeriodsService periodsService)
    {
        _periodsService = periodsService;
    }

    [HttpGet("periods")]
    public async Task<ActionResult<IReadOnlyList<CarolErpPeriod>>> Periods(CancellationToken cancellationToken)
    {
        try
        {
            var periods = await _periodsService.ListPeriodsAsync(cancellationToken);
            return Ok(periods);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

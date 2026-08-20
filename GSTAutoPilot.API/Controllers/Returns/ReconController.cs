using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/recon")]
[Authorize]
public class ReconController : ControllerBase
{
    private readonly IReconService _reconService;

    public ReconController(IReconService reconService)
    {
        _reconService = reconService;
    }

    [HttpPost("run/{period}")]
    public async Task<ActionResult<ReconRunResponse>> Run(string period, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _reconService.RunAsync(period, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("results/{period}")]
    public async Task<ActionResult<ReconReportResponse>> Results(string period, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _reconService.GetResultsAsync(period, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}

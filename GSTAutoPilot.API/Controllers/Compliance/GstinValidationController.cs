using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/gstin")]
public class GstinValidationController : ControllerBase
{
    private readonly IGstinValidationService _service;

    public GstinValidationController(IGstinValidationService service)
    {
        _service = service;
    }

    [HttpGet("validate/{gstin}")]
    public async Task<ActionResult<GstinValidationResponse>> Validate(string gstin, CancellationToken cancellationToken)
    {
        var result = await _service.ValidateAsync(gstin, cancellationToken);
        return result.FormatValid ? Ok(result) : BadRequest(result);
    }

    [HttpGet("history/{gstin}")]
    public async Task<ActionResult<IReadOnlyList<GstinValidationResponse>>> History(string gstin, CancellationToken cancellationToken)
    {
        var rows = await _service.GetHistoryAsync(gstin, cancellationToken);
        return Ok(rows);
    }

    [HttpPost("bulk-validate")]
    public async Task<ActionResult<BulkValidateResponse>> BulkValidate([FromBody] BulkValidateRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.BulkValidateAsync(request?.Gstins ?? new(), cancellationToken);
        return Ok(result);
    }
}

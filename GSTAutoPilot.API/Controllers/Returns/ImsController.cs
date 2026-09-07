using System.Text;
using GSTAutoPilot.API.Security;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Security;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/ims")]
[Authorize]
[RequiresPermission(ModulePermissions.Gstr2b)]
public class ImsController : ControllerBase
{
    private readonly IImsService _imsService;

    public ImsController(IImsService imsService)
    {
        _imsService = imsService;
    }

    /// <summary>
    /// Fetches live inward invoices directly from the GST portal into memory via GSP session.
    /// Does NOT write to the database.
    /// </summary>
    [HttpGet("inward/{period}")]
    public async Task<ActionResult<ImsInwardFetchResponse>> FetchInward(string period, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _imsService.FetchInwardAsync(period, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to fetch IMS records: {ex.Message}" });
        }
    }

    /// <summary>
    /// Parses an uploaded official IMS JSON file from the GST portal purely in memory.
    /// Does NOT write to the database.
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ImsInwardFetchResponse>> UploadImsJson(
        [FromForm] IFormFile file,
        [FromForm] string period,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Please provide an IMS JSON file to upload." });

        try
        {
            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
            var jsonContent = await reader.ReadToEndAsync(cancellationToken);
            var response = _imsService.ParseImsJson(jsonContent, period ?? string.Empty);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Submits IMS actions (Accept / Reject / Pending / Reset) directly to GSTN via GSP session.
    /// </summary>
    [HttpPost("submit")]
    public async Task<ActionResult<ImsActionSubmitResponse>> SubmitActions(
        [FromBody] ImsActionSubmitRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _imsService.SubmitActionsAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Generates the standard GSTN IMS action JSON payload for manual upload to gst.gov.in.
    /// </summary>
    [HttpPost("export-action-json")]
    public IActionResult ExportActionJson([FromBody] ImsActionSubmitRequest request)
    {
        try
        {
            var json = _imsService.GenerateActionJson(request);
            var bytes = Encoding.UTF8.GetBytes(json);
            var fileName = $"IMS_Actions_{request.FilingPeriod}_{DateTime.UtcNow:yyyyMMddHHmm}.json";
            return File(bytes, "application/json", fileName);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

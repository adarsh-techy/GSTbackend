using System.Data.Common;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/gst")]
[Authorize]
public class GstSummaryController : ControllerBase
{
    private readonly IGstSummaryService _summaryService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<GstSummaryController> _logger;

    public GstSummaryController(
        IGstSummaryService summaryService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<GstSummaryController> logger)
    {
        _summaryService = summaryService;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    [HttpGet("summary/{period}")]
    public async Task<ActionResult<GstSummaryResponse>> Summary(string period, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await _summaryService.GetSummaryAsync(period, cancellationToken);
            return Ok(summary);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (DbException ex)
        {
            _logger.LogError(ex, "Tenant DB unreachable while computing GST summary for {Period}", period);
            return Ok(EmptySummary(period, $"Tenant database unreachable: {ex.Message}"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Operation failed while computing GST summary for {Period}", period);
            return Ok(EmptySummary(period, ex.Message));
        }
    }

    private GstSummaryResponse EmptySummary(string period, string remark)
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant;
        return new GstSummaryResponse
        {
            Period = period,
            TenantGSTIN = tenant?.GSTIN ?? string.Empty,
            OutputGST = new OutputGstSection(),
            ItcFromGSTR2B = new ItcFromGstr2BSection(),
            ReconSummary = new ReconSummary(),
            NetTaxPayable = new NetTaxPayableSection(),
            CarryForward = new CarryForwardSection
            {
                Remarks = "No ITC carry-forward this period",
            },
            AIRemarks = remark,
        };
    }
}

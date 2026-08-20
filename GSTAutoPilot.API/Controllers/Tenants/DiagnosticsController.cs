using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

// Operational diagnostics for the resolved tenant. Right now this surfaces the
// health of the outward/inward stored-procedure data sources so the UI (and
// support) can see at a glance whether each SP is configured and actually
// returning data — the "is my real purchase/sales feed working?" check.
[ApiController]
[Route("api/diagnostics")]
[Authorize]
public class DiagnosticsController : ControllerBase
{
    private readonly SpOutwardService _outward;
    private readonly SpInwardService _inward;

    public DiagnosticsController(SpOutwardService outward, SpInwardService inward)
    {
        _outward = outward;
        _inward = inward;
    }

    // Exercises both SPs live (24-month count) and reports a green/amber/red
    // status per direction. Safe to call repeatedly; read-only.
    [HttpGet("sp-profile")]
    public async Task<ActionResult<SpDiagnosticsDto>> SpProfile(CancellationToken cancellationToken)
    {
        var tenant = HttpContext.Items["Tenant"] as Tenant;
        var dto = new SpDiagnosticsDto
        {
            TenantName = tenant?.Name,
            Outward = await ProbeAsync(_outward.IsConfigured, tenant?.OutwardSP,
                () => _outward.OutwardCountsByPeriodAsync(cancellationToken)),
            Inward = await ProbeAsync(_inward.IsConfigured, tenant?.InwardSP,
                () => _inward.InwardCountsByPeriodAsync(cancellationToken)),
        };
        return Ok(dto);
    }

    private static async Task<SpDirectionDiagnostics> ProbeAsync(
        bool configured, string? spName, Func<Task<Dictionary<string, int>>> run)
    {
        var d = new SpDirectionDiagnostics { SpName = spName, Configured = configured };
        if (!configured)
        {
            d.Status = "NotConfigured";
            return d;
        }
        try
        {
            var counts = await run();
            d.Tested = true;
            d.Ok = true;
            d.PeriodCount = counts.Count;
            d.InvoiceCount = counts.Values.Sum();
            d.Status = d.InvoiceCount > 0 ? "Green" : "Amber";
        }
        catch (Exception ex)
        {
            d.Tested = true;
            d.Ok = false;
            d.Error = ex.Message;
            d.Status = "Red";
        }
        return d;
    }
}

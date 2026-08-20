using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/export")]
[Authorize]
public class ExportController : ControllerBase
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IExportService _exportService;

    public ExportController(IExportService exportService)
    {
        _exportService = exportService;
    }

    [HttpGet("gstr1/{period}")]
    public Task<IActionResult> Gstr1(string period, [FromQuery] string? section, CancellationToken cancellationToken)
        => RunAsync(() => _exportService.ExportGstr1Async(period, section, cancellationToken));

    [HttpGet("gstr3b/{period}")]
    public Task<IActionResult> Gstr3b(string period, CancellationToken cancellationToken)
        => RunAsync(() => _exportService.ExportGstr3bAsync(period, cancellationToken));

    [HttpGet("invoices/{period}")]
    public Task<IActionResult> Invoices(string period, CancellationToken cancellationToken)
        => RunAsync(() => _exportService.ExportInvoicesAsync(period, cancellationToken));

    [HttpGet("recon/{period}")]
    public Task<IActionResult> Recon(string period, CancellationToken cancellationToken)
        => RunAsync(() => _exportService.ExportReconAsync(period, cancellationToken));

    private async Task<IActionResult> RunAsync(Func<Task<ExportResult>> work)
    {
        try
        {
            var result = await work();
            return File(result.Bytes, XlsxContentType, result.FileName);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

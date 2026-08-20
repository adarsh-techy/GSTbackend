using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Exceptions;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class InvoiceController : ControllerBase
{
    private readonly IInvoiceService _invoiceService;
    private readonly IInvoicePdfService _pdfService;
    private readonly IGstnReturnService _gstnService;

    public InvoiceController(IInvoiceService invoiceService, IInvoicePdfService pdfService, IGstnReturnService gstnService)
    {
        _invoiceService = invoiceService;
        _pdfService = pdfService;
        _gstnService = gstnService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InvoiceResponse>>> List(
        [FromQuery] string period,
        CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, out var year, out var month))
        {
            return BadRequest("period must be in YYYYMM format (e.g. 202604).");
        }
        var invoices = await _invoiceService.ListAsync(year, month, cancellationToken);
        return Ok(invoices);
    }

    [HttpGet("gstr1")]
    public async Task<ActionResult<IReadOnlyList<Gstr1SummaryRow>>> Gstr1(
        [FromQuery] string period,
        CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, out var year, out var month))
        {
            return BadRequest("period must be in YYYYMM format (e.g. 202604).");
        }
        var summary = await _invoiceService.GetGstr1SummaryAsync(year, month, cancellationToken);
        return Ok(summary);
    }

    [HttpGet("gstr1/tables")]
    public async Task<ActionResult<Gstr1TablesResponse>> Gstr1Tables(
        [FromQuery] string period,
        CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, out var year, out var month))
        {
            return BadRequest("period must be in YYYYMM format (e.g. 202604).");
        }
        var tables = await _invoiceService.GetGstr1TablesAsync(year, month, cancellationToken);
        return Ok(tables);
    }

    // Rate-wise B2CS summary (the filed shape) — reuses the GSTN b2cs builder.
    [HttpGet("gstr1/b2cs")]
    public async Task<ActionResult<IReadOnlyList<Gstr1B2cs>>> Gstr1B2cs(
        [FromQuery] string period,
        CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, out var year, out var month))
        {
            return BadRequest("period must be in YYYYMM format (e.g. 202604).");
        }
        try
        {
            var gstn = await _gstnService.BuildGstr1Async(year, month, cancellationToken);
            return Ok(gstn.B2cs ?? new List<Gstr1B2cs>());
        }
        catch (Gstr1UnreportedInvoicesException ex)
        {
            // The whole return is unsafe to show if part of the book reaches no
            // table; the filing screen's validation lists which invoices.
            return UnprocessableEntity(new { error = "NOT_IN_ANY_TABLE", message = ex.Message });
        }
    }

    [HttpGet("{billId:int}/pdf")]
    public async Task<IActionResult> Pdf(int billId, CancellationToken cancellationToken)
    {
        var result = await _pdfService.RenderAsync(billId, cancellationToken);
        if (result is null) return NotFound();
        return File(result.Bytes, "application/pdf", result.FileName);
    }

    private static bool TryParsePeriod(string period, out int year, out int month)
    {
        year = 0;
        month = 0;
        if (string.IsNullOrWhiteSpace(period) || period.Length != 6) return false;
        if (!int.TryParse(period.AsSpan(0, 4), out year)) return false;
        if (!int.TryParse(period.AsSpan(4, 2), out month)) return false;
        return month >= 1 && month <= 12;
    }
}

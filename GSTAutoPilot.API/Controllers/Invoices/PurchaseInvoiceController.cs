using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/purchase-invoice")]
public class PurchaseInvoiceController : ControllerBase
{
    private readonly IPurchaseInvoiceService _service;

    public PurchaseInvoiceController(IPurchaseInvoiceService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PurchaseInvoiceResponse>>> List(
        [FromQuery] string period,
        CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, out var year, out var month))
        {
            return BadRequest("period must be in YYYYMM format (e.g. 202604).");
        }
        var rows = await _service.ListAsync(year, month, cancellationToken);
        return Ok(rows);
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

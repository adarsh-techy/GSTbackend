using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

// Work lists for the bulk toolbars. Read-only by design — the bulk runs
// themselves go through the existing single-item endpoints, one at a time, so
// every generated IRN and every email keeps the same validation, audit trail
// and rate limit it has when done by hand.
[ApiController]
[Route("api/bulk")]
[Authorize]
public class BulkController : ControllerBase
{
    private readonly IBulkOperationsService _bulk;

    public BulkController(IBulkOperationsService bulk)
    {
        _bulk = bulk;
    }

    // Invoices in the period that need an IRN and don't have one.
    [HttpGet("pending-irn/{period}")]
    public async Task<ActionResult<BulkCandidatesResponse>> PendingIrn(string period, CancellationToken cancellationToken)
    {
        try { return Ok(await _bulk.PendingIrnAsync(period, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Generated IRNs in the period that have not been emailed to the buyer.
    [HttpGet("pending-email/{period}")]
    public async Task<ActionResult<BulkCandidatesResponse>> PendingEmail(string period, CancellationToken cancellationToken)
    {
        try { return Ok(await _bulk.PendingEmailAsync(period, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // GSTR-1 / GSTR-3B state for the period, in filing order.
    [HttpGet("pending-returns/{period}")]
    public async Task<ActionResult<PendingReturnsResponse>> PendingReturns(string period, CancellationToken cancellationToken)
    {
        try { return Ok(await _bulk.PendingReturnsAsync(period, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}

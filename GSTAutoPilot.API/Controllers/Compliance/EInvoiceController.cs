using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Services.Bulk;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/einvoice")]
public class EInvoiceController : ControllerBase
{
    private readonly IEInvoiceService _einvoiceService;
    private readonly OperationRateLimiter _limiter;

    public EInvoiceController(IEInvoiceService einvoiceService, OperationRateLimiter limiter)
    {
        _einvoiceService = einvoiceService;
        _limiter = limiter;
    }

    // Both outward-facing operations are paced here rather than in the browser:
    // a bulk run is client-driven, so a client-side delay would be a suggestion.
    // Applies to hand-clicked calls too, which is the point — the portal and the
    // mail server don't care which button caused the traffic.
    private ActionResult? RateLimited(OperationLimit limit)
    {
        var tenant = HttpContext.Items["Tenant"] as Tenant;
        if (tenant is null) return null; // tenant middleware will have rejected it already
        if (_limiter.TryAcquire(tenant.TenantId, limit, out var retryAfter)) return null;

        var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        Response.Headers.RetryAfter = seconds.ToString();
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = "RATE_LIMITED",
            message = $"Limit of {limit.Max} per {Describe(limit.Period)} reached. Next slot in {seconds}s.",
            retryAfterSeconds = seconds,
            limit = limit.Max,
            periodSeconds = (int)limit.Period.TotalSeconds,
        });
    }

    private static string Describe(TimeSpan period)
        => period >= TimeSpan.FromHours(1) ? $"{period.TotalHours:0.#} hour(s)" : $"{period.TotalMinutes:0.#} minute(s)";

    [HttpPost("generate/{invoiceId:guid}")]
    public async Task<ActionResult<IRNResponse>> Generate(Guid invoiceId, CancellationToken cancellationToken)
    {
        try
        {
            var irn = await _einvoiceService.GenerateAsync(invoiceId, cancellationToken);
            return Ok(irn);
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
    }

    // CarolERP-sourced invoices are addressed by integer BillId. This is the
    // route the WhiteBooks e-Invoice flow uses.
    [HttpPost("generate/bill/{billId:int}")]
    public async Task<ActionResult<IRNResponse>> GenerateForBill(int billId, CancellationToken cancellationToken)
    {
        if (RateLimited(OperationRateLimiter.EInvoiceGenerate) is { } limited) return limited;
        try
        {
            var irn = await _einvoiceService.GenerateForBillAsync(billId, cancellationToken);
            return Ok(irn);
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("status/bill/{billId:int}")]
    public async Task<ActionResult<IRNResponse>> StatusByBill(int billId, CancellationToken cancellationToken)
    {
        var irn = await _einvoiceService.GetByBillAsync(billId, cancellationToken);
        return irn is null ? NotFound($"No IRN found for BillId {billId}.") : Ok(irn);
    }

    // Returns the NIC v1.1 JSON we'd POST to WhiteBooks for this bill — without
    // calling WhiteBooks. Use ?download=1 to get it as a file attachment.
    [HttpGet("preview-payload/bill/{billId:int}")]
    public async Task<IActionResult> PreviewPayload(int billId, [FromQuery] bool download, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _einvoiceService.PreviewPayloadAsync(billId, cancellationToken);
            if (download)
            {
                return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json",
                    $"einvoice-payload-bill-{billId}.json");
            }
            return Content(json, "application/json");
        }
        catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("cancel/{irnId:guid}")]
    public async Task<ActionResult<IRNResponse>> Cancel(Guid irnId, [FromBody] CancelIrnRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var irn = await _einvoiceService.CancelAsync(irnId, request?.Reason ?? string.Empty, request?.Remarks, cancellationToken);
            return Ok(irn);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Window closed / already cancelled / provider rejection.
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("download-json/{billId:int}")]
    public async Task<IActionResult> DownloadJson(int billId, CancellationToken cancellationToken)
    {
        var file = await _einvoiceService.GetSignedJsonAsync(billId, cancellationToken);
        return file is null
            ? NotFound(new { error = $"No signed e-Invoice found for BillId {billId}." })
            : File(file.Bytes, file.ContentType, file.FileName);
    }

    [HttpGet("download-qr/{billId:int}")]
    public async Task<IActionResult> DownloadQr(int billId, CancellationToken cancellationToken)
    {
        var file = await _einvoiceService.GetQrPngAsync(billId, cancellationToken);
        return file is null
            ? NotFound(new { error = $"No e-Invoice QR found for BillId {billId}." })
            : File(file.Bytes, file.ContentType, file.FileName);
    }

    [HttpGet("status/{invoiceId:guid}")]
    public async Task<ActionResult<IRNResponse>> Status(Guid invoiceId, CancellationToken cancellationToken)
    {
        var irn = await _einvoiceService.GetByInvoiceAsync(invoiceId, cancellationToken);
        return irn is null ? NotFound($"No IRN found for invoice {invoiceId}.") : Ok(irn);
    }

    [HttpPost("email-json/{billId:int}")]
    public async Task<ActionResult<IRNResponse>> EmailJson(int billId, [FromBody] EmailJsonRequest request, CancellationToken cancellationToken)
    {
        if (RateLimited(OperationRateLimiter.EInvoiceEmail) is { } limited) return limited;
        try
        {
            var irn = await _einvoiceService.EmailJsonAsync(billId, request, cancellationToken);
            return Ok(irn);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Email failed: {ex.Message}" });
        }
    }

    [HttpGet("list")]
    public async Task<ActionResult<IReadOnlyList<IRNResponse>>> List(CancellationToken cancellationToken)
    {
        var rows = await _einvoiceService.ListAsync(cancellationToken);
        return Ok(rows);
    }
}

using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Infrastructure.Services.WhiteBooksGst;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

// WhiteBooks GST-returns OTP session + GSTR-2B fetch. The GSTN returns APIs need
// an OTP-authenticated session: request OTP (SMS/email to the taxpayer) -> verify
// OTP+TXN -> ~6h session token.
[ApiController]
[Route("api/gst")]
[Authorize]
public class GstApiController : ControllerBase
{
    private readonly IWhiteBooksGstClient _gst;

    public GstApiController(IWhiteBooksGstClient gst) => _gst = gst;

    [HttpGet("session")]
    public ActionResult<object> Session()
        => Ok(new { configured = _gst.IsConfigured, hasSession = _gst.HasSession });

    [HttpPost("otp/request")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RequestOtp(CancellationToken cancellationToken)
    {
        try
        {
            var txn = await _gst.RequestOtpAsync(cancellationToken);
            return Ok(new { txn });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("otp/verify")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> VerifyOtp([FromBody] GstOtpVerifyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _gst.VerifyOtpAsync(request?.Txn ?? string.Empty, request?.Otp ?? string.Empty, cancellationToken);
            return Ok(new { success = true });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Raw GSTR-2B (period YYYYMM -> rtnprd MMYYYY). Requires an OTP session.
    // `filenum` is the GSTR-2B part number; "1" is correct for most taxpayers.
    [HttpGet("gstr2b/raw")]
    public async Task<IActionResult> Gstr2bRaw([FromQuery] string period, [FromQuery] string? filenum, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(period) || period.Length != 6)
            return BadRequest(new { error = "period must be YYYYMM." });
        try
        {
            var json = await _gst.FetchGstr2bRawAsync(
                WhiteBooksGstClient.ToRetPeriod(period),
                string.IsNullOrWhiteSpace(filenum) ? "1" : filenum.Trim(),
                cancellationToken);
            return Content(json, "application/json");
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}

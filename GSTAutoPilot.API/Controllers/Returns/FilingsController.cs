using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Exceptions;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/filings")]
[Authorize]
public class FilingsController : ControllerBase
{
    private readonly IFilingService _filingService;
    private readonly IGstnReturnService _gstnService;
    private readonly IReturnValidationService _validation;

    public FilingsController(IFilingService filingService, IGstnReturnService gstnService, IReturnValidationService validation)
    {
        _filingService = filingService;
        _gstnService = gstnService;
        _validation = validation;
    }

    // Pre-file validation — run before locking/filing GSTR-1 so the preview can
    // list fixable problems. Errors block a clean file; warnings are advisory.
    [HttpGet("gstr1/{period}/validate")]
    public async Task<ActionResult<ReturnValidationResult>> ValidateGstr1(string period, CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, out var year, out var month))
            return BadRequest(new { error = "period must be YYYYMM (e.g. 202604)." });
        return Ok(await _validation.ValidateGstr1Async(year, month, cancellationToken));
    }

    [HttpGet("gstr1/{period}/json")]
    public async Task<IActionResult> Gstr1Json(string period, CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, out var year, out var month))
            return BadRequest(new { error = "period must be YYYYMM (e.g. 202604)." });
        Gstr1Json gstn;
        try
        {
            gstn = await _gstnService.BuildGstr1Async(year, month, cancellationToken);
        }
        catch (Gstr1UnreportedInvoicesException ex)
        {
            // The builder refuses to emit a return that leaves invoices out. Say
            // so in a shape the UI can show, instead of a 500 — the download is
            // blocked on purpose, and the validation screen lists the invoices.
            return UnprocessableEntity(new
            {
                error = "NOT_IN_ANY_TABLE",
                message = ex.Message,
                invoiceCount = ex.InvoiceCount,
                taxableValue = ex.TaxableValue,
                tax = ex.Tax,
                invoiceNumbers = ex.InvoiceNumbers,
            });
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(_gstnService.Serialize(gstn));
        return File(bytes, "application/json", $"GSTR1_{gstn.Gstin}_{period}.json");
    }

    [HttpGet("gstr3b/{period}/json")]
    public async Task<IActionResult> Gstr3bJson(string period, CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, out var year, out var month))
            return BadRequest(new { error = "period must be YYYYMM (e.g. 202604)." });
        var gstn = await _gstnService.BuildGstr3bAsync(year, month, cancellationToken);
        var bytes = System.Text.Encoding.UTF8.GetBytes(_gstnService.Serialize(gstn));
        return File(bytes, "application/json", $"GSTR3B_{gstn.Gstin}_{period}.json");
    }

    // 422 with the reason, so the UI can tell "confirm the period is empty"
    // apart from "you asked for NIL but there is data here".
    private ActionResult NilConflict(NilReturnConfirmationException ex)
        => UnprocessableEntity(new
        {
            error = ex.Reason,
            message = ex.Message,
            period = ex.Period,
            returnType = ex.ReturnType,
        });

    private static bool TryParseFilingType(string type, out FilingType filingType)
    {
        switch (type?.Trim().ToLowerInvariant())
        {
            case "gstr1": filingType = FilingType.Gstr1; return true;
            case "gstr3b": filingType = FilingType.Gstr3b; return true;
            default: filingType = default; return false;
        }
    }

    private static bool TryParsePeriod(string period, out int year, out int month)
    {
        year = 0; month = 0;
        if (string.IsNullOrWhiteSpace(period) || period.Length != 6) return false;
        if (!int.TryParse(period.AsSpan(0, 4), out year)) return false;
        if (!int.TryParse(period.AsSpan(4, 2), out month)) return false;
        return month >= 1 && month <= 12;
    }

    // Would locking this period produce a NIL return? Lets the UI offer NIL
    // filing as a deliberate choice instead of the user meeting a confirmation
    // prompt after pressing Lock.
    [HttpGet("{type}/{period}/nil-check")]
    public async Task<ActionResult<NilCheckResponse>> NilCheck(string type, string period, CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, out _, out _))
            return BadRequest(new { error = "period must be YYYYMM (e.g. 202604)." });
        if (!TryParseFilingType(type, out var filingType))
            return BadRequest(new { error = "type must be gstr1 or gstr3b." });
        return Ok(await _filingService.CheckNilAsync(period, filingType, cancellationToken));
    }

    // `confirmNil=true` is the caller stating that a period with no transactions
    // is meant to be filed as a NIL return. Without it an empty period is
    // refused, so a failed data load can never become a NIL declaration.
    [HttpPost("gstr1/{period}/lock")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<FilingResponse>> LockGstr1(
        string period, [FromQuery] bool confirmNil, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _filingService.LockGstr1Async(period, confirmNil, cancellationToken));
        }
        catch (NilReturnConfirmationException ex)
        {
            return NilConflict(ex);
        }
        catch (Gstr1UnreportedInvoicesException ex)
        {
            // Never lock a return that doesn't account for the whole book.
            return UnprocessableEntity(new
            {
                error = "NOT_IN_ANY_TABLE",
                message = ex.Message,
                invoiceCount = ex.InvoiceCount,
                taxableValue = ex.TaxableValue,
                tax = ex.Tax,
                invoiceNumbers = ex.InvoiceNumbers,
            });
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

    [HttpPost("gstr3b/{period}/lock")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<FilingResponse>> LockGstr3b(
        string period, [FromQuery] bool confirmNil, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _filingService.LockGstr3bAsync(period, confirmNil, cancellationToken));
        }
        catch (NilReturnConfirmationException ex)
        {
            return NilConflict(ex);
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

    [HttpPost("{filingId:guid}/file")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<FilingResponse>> MarkFiled(Guid filingId, [FromBody] MarkFiledCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _filingService.MarkFiledAsync(filingId, command, cancellationToken));
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

    // Direct GSTN filing via GSP, step 1+2: retsave the locked return, then
    // retsubmit to lock it on the portal. A save rejected by GSTN stops here
    // and returns the validation report instead of advancing.
    [HttpPost("{filingId:guid}/gstn/submit")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<GstnSubmitResponse>> SubmitToGstn(Guid filingId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _filingService.SubmitToGstnAsync(filingId, cancellationToken));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Step 3: file the submitted return with a fresh OTP -> ARN.
    [HttpPost("{filingId:guid}/gstn/file")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<FilingResponse>> FileWithEvc(Guid filingId, [FromBody] FileWithEvcCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _filingService.FileWithEvcAsync(filingId, command, cancellationToken));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // GSTN's own computed summary for the period (retsum), so the preparer can
    // compare the portal's figures against ours before filing. Read-only.
    [HttpGet("{type}/{period}/gstn-summary")]
    public async Task<ActionResult<string>> GstnSummary(string type, string period, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _filingService.GetGstnSummaryAsync(type, period, cancellationToken);
            return Content(json, "application/json");
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FilingResponse>>> List(
        [FromQuery] string? period,
        [FromQuery] FilingType? type,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _filingService.ListAsync(period, type, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("latest")]
    public async Task<ActionResult<FilingResponse>> Latest(
        [FromQuery] string period,
        [FromQuery] FilingType type,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _filingService.LatestAsync(period, type, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{filingId:guid}")]
    public async Task<ActionResult<FilingDetailResponse>> Get(Guid filingId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _filingService.GetAsync(filingId, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Infrastructure.Services.WhiteBooksGst;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GSTAutoPilot.API.Filters;

// Turns any GstnApiException into one consistent, actionable response, from
// whichever endpoint raised it, and writes the portal's full reply to the log.
//
// Registered globally rather than caught per-controller: a portal rejection
// carries a code, the portal's own wording and (for retsave) a validation
// report, and every one of those was being thrown away by the generic
// `catch (InvalidOperationException) -> BadRequest(ex.Message)` blocks.
public class GstnExceptionFilter : IExceptionFilter
{
    private readonly ILogger<GstnExceptionFilter> _logger;

    public GstnExceptionFilter(ILogger<GstnExceptionFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not GstnApiException ex) return;

        var reference = context.HttpContext.TraceIdentifier;
        var details = ex.ParseDetails();

        // Full detail to the server log — including the raw portal body, which
        // is deliberately NOT sent to the browser (it can carry txn/session
        // values). `reference` ties a support ticket back to this line.
        _logger.LogError(
            "GSTN rejection [{Reference}] op={Operation} code={Code} http={HttpStatus} action={Action} details={DetailCount} portal={PortalMessage} body={Body}",
            reference, ex.Operation, ex.Code ?? "(none)", ex.HttpStatus, ex.Action,
            details.Count, ex.PortalMessage, ex.RawBody);

        var payload = new GstnErrorResponse
        {
            Code = ex.Code,
            Message = ex.Message,
            PortalMessage = ex.PortalMessage,
            Operation = ex.Operation,
            Action = ex.Action,
            Retryable = ex.IsRetryable,
            Details = details.ToList(),
            Reference = reference,
        };

        context.Result = new ObjectResult(payload) { StatusCode = StatusFor(ex) };
        context.ExceptionHandled = true;
    }

    // Deliberately never 401/403: the SPA treats those as "your login died" and
    // signs the user out, which is wrong — it's the GSTN session that expired,
    // not the app session. 409 says "the portal's state blocks this", 422 says
    // "your data was rejected".
    private static int StatusFor(GstnApiException ex) => ex.Code switch
    {
        "1005" or "AUTH4033" or "AUTH4034" => StatusCodes.Status409Conflict,
        "AUT4031" => StatusCodes.Status409Conflict,
        "RET191106" or "RET191116" => StatusCodes.Status409Conflict,
        "RET191113" or "RET191115" => StatusCodes.Status422UnprocessableEntity,
        "RETSAVE_ERR" or "RETFILE_ERR" => StatusCodes.Status422UnprocessableEntity,
        _ => ex.IsRetryable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status400BadRequest,
    };
}

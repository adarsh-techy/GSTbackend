using GSTAutoPilot.Infrastructure.Services.WhiteBooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

// Namespace deliberately not ...Controllers.System — a namespace segment named
// "System" shadows the global System namespace inside this file.
namespace GSTAutoPilot.API.Controllers;

// TEMPORARY (2026-09-10) — backs the Sandbox/Production toggle in the app
// header and the red "SANDBOX" frame drawn around every page.
//
// Read-only in every sense: it touches no database, writes no tenant row, and
// only reports what the server would do with the environment header the caller
// sent. The whole feature is switched off by setting
// WhiteBooksEInvoice:AllowEnvHeaderOverride to false in appsettings, after
// which this endpoint reports CanToggle=false and the UI hides the toggle.
[ApiController]
[Route("api/system/environment")]
[AllowAnonymous]
public class EnvironmentController : ControllerBase
{
    private readonly WhiteBooksOptions _wb;

    public EnvironmentController(IOptions<WhiteBooksOptions> wb)
    {
        _wb = wb.Value;
    }

    [HttpGet]
    public ActionResult<EnvironmentStatusDto> Get()
    {
        var requested = WhiteBooksClient.ReadEnvHeader(HttpContext, _wb);
        // Mirrors WhiteBooksClient.Resolve(): ForceSandbox outranks everything.
        var sandbox = _wb.ForceSandbox || (requested ?? _wb.UseSandbox);

        // Same "is this a real value, not a [placeholder]" test WhiteBooksOptions
        // applies internally; repeated here because that helper is not public.
        static bool IsReal(string? v) => !string.IsNullOrWhiteSpace(v) && !v.TrimStart().StartsWith('[');

        var productionReady =
            IsReal(_wb.Production.ClientId)
            && IsReal(_wb.Production.ClientSecret)
            && IsReal(_wb.ProductionUrl);

        string? lockedReason = null;
        if (_wb.ForceSandbox)
            lockedReason = "The server is locked to sandbox (WhiteBooksEInvoice:ForceSandbox is true). Clear that flag for a signed-off go-live.";
        else if (!productionReady)
            lockedReason = "No production credentials are configured for this deployment.";

        return Ok(new EnvironmentStatusDto
        {
            Environment = sandbox ? "Sandbox" : "Production",
            IsSandbox = sandbox,
            CanToggle = _wb.AllowEnvHeaderOverride,
            ProductionLocked = lockedReason is not null,
            LockedReason = lockedReason,
            RequestedEnvironment = requested is null ? null : requested.Value ? "Sandbox" : "Production",
            HeaderName = WhiteBooksOptions.EnvHeaderName,
        });
    }
}

public class EnvironmentStatusDto
{
    // What this request would actually hit: "Sandbox" or "Production".
    public string Environment { get; set; } = "Sandbox";
    public bool IsSandbox { get; set; }
    // False once the temporary toggle feature is switched off server-side.
    public bool CanToggle { get; set; }
    // True when asking for production cannot succeed, whatever the toggle says.
    public bool ProductionLocked { get; set; }
    public string? LockedReason { get; set; }
    // What the caller's header asked for, or null if it sent none.
    public string? RequestedEnvironment { get; set; }
    public string HeaderName { get; set; } = WhiteBooksOptions.EnvHeaderName;
}

namespace GSTAutoPilot.Infrastructure.Services.WhiteBooksGst;

// Config for the WhiteBooks GST API (returns / GSTR-2B / GSTIN search) — a
// separate product/credential set from the e-Invoice API.
public class WhiteBooksGstOptions
{
    public const string SectionName = "WhiteBooksGst";

    public string BaseUrl { get; set; } = "https://api.whitebooks.in";
    // WhiteBooks sandbox. Note this host serves the *e-Invoice* sandbox for
    // certain; whether the RETURNS endpoints are available here is unconfirmed
    // — a retsave against it is the cheap way to find out. Filing against
    // production is irreversible, so keep UseSandbox=true until proven.
    public string SandboxUrl { get; set; } = "https://apisandbox.whitebooks.in";
    // Route GST-returns traffic to SandboxUrl instead of BaseUrl.
    public bool UseSandbox { get; set; }

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    // WhiteBooks ACCOUNT email the OTP/returns calls authenticate as — this is
    // the email registered with WhiteBooks (e.g. the sandbox account
    // support@carolsolutions.com), which is NOT necessarily the e-Invoice
    // Production.Email. When blank, WhiteBooksGstClient falls back to the
    // e-Invoice Production.Email for backwards compatibility.
    public string Email { get; set; } = string.Empty;
    // Taxpayer GST-portal API user for the RETURNS API — DIFFERENT from the
    // e-Invoice user (e.g. Flooratex: GST=FLOORATEX2020, e-Invoice=API_FLOORATEX2026).
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }

    // The host actually used for GST calls.
    public string EffectiveBaseUrl => UseSandbox && IsReal(SandboxUrl) ? SandboxUrl : BaseUrl;

    // Return-filing endpoint paths. Defaults verified against the WhiteBooks
    // Postman collection; overridable from config so a contract change can be
    // corrected without a code change/redeploy.
    public ReturnEndpointOptions Endpoints { get; set; } = new();

    public bool IsConfigured =>
        IsEnabled && IsReal(ClientId) && IsReal(ClientSecret) && IsReal(EffectiveBaseUrl);

    internal static bool IsReal(string? v) => !string.IsNullOrWhiteSpace(v) && !v.TrimStart().StartsWith('[');
}

// Paths are relative to BaseUrl. `{type}` is substituted with gstr1 / gstr3b.
// Verified against WB-GST-API.postman_collection.json. Note there is no
// retsubmit endpoint — the flow is retsave -> otpforevc -> retevcfile.
public class ReturnEndpointOptions
{
    // PUT — save the prepared return JSON. Returns reference_id + error_report.
    public string Save { get; set; } = "/{type}/retsave";
    // POST — file with an EVC OTP (passed as the `evcotp` query param).
    public string EvcFile { get; set; } = "/{type}/retevcfile";
    // POST — file with a DSC/other signature; body carries the return payload.
    public string File { get; set; } = "/{type}/retfile";
    // GET — GSTN's own computed summary, for comparison against ours.
    public string Summary { get; set; } = "/{type}/retsum";
    // GET — "new proceed to file". Not `{type}`-templated: the return type is a
    // query param, alongside the isNil flag that declares a NIL filing. Only
    // used for NIL returns; the ordinary flow is retsave -> otpforevc ->
    // retevcfile and is left alone.
    public string ProceedFile { get; set; } = "/all/newproceedfile";

    public string For(string action, string returnType)
    {
        var path = action switch
        {
            "save" => Save,
            "file" => File,
            "evcfile" => EvcFile,
            "summary" => Summary,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown return endpoint action."),
        };
        return path.Replace("{type}", returnType.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
    }
}

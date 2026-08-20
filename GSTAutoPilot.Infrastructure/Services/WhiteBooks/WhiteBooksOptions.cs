namespace GSTAutoPilot.Infrastructure.Services.WhiteBooks;

// WhiteBooks GSP e-Invoice config has two distinct credential sets — sandbox
// (a SHARED test account WhiteBooks gives every customer; same BVMGSP/
// EINSd535... for everyone) and production (the customer's own creds for the
// real GSTIN). The 1005 "Invalid Token" error we hit for weeks was caused by
// sending production creds to the sandbox URL; the two are not interchangeable.
// Sandbox creds live here as defaults; production creds are per-tenant overrides
// in TenantSettings.WhiteBooksClientId/Secret/Username/Password.
public class WhiteBooksOptions
{
    public const string SectionName = "WhiteBooksEInvoice";

    public string SandboxUrl { get; set; } = "https://apisandbox.whitebooks.in";
    public string ProductionUrl { get; set; } = string.Empty;

    public WhiteBooksCredentials Sandbox { get; set; } = new();
    public WhiteBooksCredentials Production { get; set; } = new();

    public bool IsEnabled { get; set; }
    public bool UseSandbox { get; set; } = true;

    public string BaseUrl => UseSandbox ? SandboxUrl : ProductionUrl;
    public WhiteBooksCredentials Active => UseSandbox ? Sandbox : Production;

    // True only when the integration is switched on AND real (non-placeholder)
    // credentials are present for the active environment.
    public bool IsConfigured =>
        IsEnabled
        && IsRealValue(Active.ClientId)
        && IsRealValue(Active.ClientSecret)
        && IsRealValue(BaseUrl)
        && IsRealValue(Active.Gstin);

    public string SourceLabel => UseSandbox ? "WHITEBOOKS_SANDBOX" : "WHITEBOOKS_PROD";

    internal static bool IsRealValue(string? v)
        => !string.IsNullOrWhiteSpace(v) && !v.TrimStart().StartsWith('[');
}

public class WhiteBooksCredentials
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    // Taxpayer GSTIN this credential set is registered for. For sandbox this is
    // WhiteBooks's shared test GSTIN (29AAGCB1286Q000); for production it's the
    // tenant's own GSTIN.
    public string Gstin { get; set; } = string.Empty;
    // WhiteBooks account email used as ?email= query param on every call.
    public string Email { get; set; } = string.Empty;
    // Taxpayer e-Invoice (NIC) portal username / password.
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

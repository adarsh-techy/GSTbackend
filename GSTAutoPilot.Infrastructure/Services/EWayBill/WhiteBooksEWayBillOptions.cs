namespace GSTAutoPilot.Infrastructure.Services.EwbApi;

// Config for the WhiteBooks e-Way Bill API. The EWB API lives on the same host
// as the GST-returns API (api.whitebooks.in) and authenticates with the
// taxpayer's NIC e-Way-Bill portal username/password (often the SAME NIC login
// as e-Invoice, but kept overridable here). GSP client_id/client_secret default
// to the e-Invoice Production block unless set explicitly.
//
// Endpoint paths are config so they can be corrected without a redeploy — the
// exact NIC/GSP path + header quirks historically needed live verification (the
// e-Invoice path cost multiple days over an `auth_token` vs `auth-token` hyphen).
public class WhiteBooksEWayBillOptions
{
    public const string SectionName = "WhiteBooksEWayBill";

    public string BaseUrl { get; set; } = "https://api.whitebooks.in";

    // Relative paths under BaseUrl. Defaults follow the documented WhiteBooks
    // EWB v1.03 contract; verify against the GSP's Postman collection.
    public string AuthPath { get; set; } = "/ewaybillapi/dec/v1.03/auth";
    public string GeneratePath { get; set; } = "/ewaybillapi/dec/v1.03/ewayapi/genewaybill";
    public string CancelPath { get; set; } = "/ewaybillapi/dec/v1.03/ewayapi/cancelewb";
    public string UpdateVehiclePath { get; set; } = "/ewaybillapi/dec/v1.03/ewayapi/vehewb";

    // GSP creds — fall back to the e-Invoice Production block when blank.
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    // Taxpayer NIC e-Way-Bill portal user — falls back to the e-Invoice user.
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public bool BaseConfigured => IsEnabled && IsReal(BaseUrl);

    internal static bool IsReal(string? v) => !string.IsNullOrWhiteSpace(v) && !v.TrimStart().StartsWith('[');
}

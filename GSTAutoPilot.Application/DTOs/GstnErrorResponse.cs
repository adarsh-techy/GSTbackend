namespace GSTAutoPilot.Application.DTOs;

// The single shape every GSTN/WhiteBooks rejection is returned in, so the UI can
// branch on `action` instead of matching message text. Emitted by the API's
// GstnExceptionFilter for any GstnApiException that escapes a controller.
public class GstnErrorResponse
{
    // Stable discriminator so the client can tell a portal rejection apart from
    // an ordinary 400 (which is just { error: "..." }).
    public string Error { get; set; } = "GSTN_ERROR";

    // The portal's own code (1005, RET191116, AUTH4034, ...), when it gave one.
    public string? Code { get; set; }

    // What a tax preparer should be told, in plain language.
    public string Message { get; set; } = string.Empty;

    // The portal's raw text, kept for the support/error report — never the only
    // thing shown, because GSTN's wording is frequently unhelpful.
    public string? PortalMessage { get; set; }

    // Which call failed ("GSTR1 retsave", "OTP verification", ...).
    public string Operation { get; set; } = string.Empty;

    // What the UI should offer next. See GstnErrorAction.
    public string Action { get; set; } = GstnErrorAction.None;

    // True only when retrying the identical request could succeed on its own.
    public bool Retryable { get; set; }

    // GSTN's structured validation report, flattened (retsave/retfile).
    public List<GstnErrorDetail> Details { get; set; } = new();

    // Correlation id, also written to the server log, for a support ticket.
    public string? Reference { get; set; }
}

// One row of GSTN's error_report.
public class GstnErrorDetail
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    // The invoice / note the error is about, when GSTN identifies one.
    public string? InvoiceNumber { get; set; }
    // The counter-party GSTIN, when the report is grouped by ctin.
    public string? Ctin { get; set; }
}

// Values for GstnErrorResponse.Action. The UI maps each to a next step; anything
// it doesn't recognise falls back to showing Message alone.
public static class GstnErrorAction
{
    public const string None = "none";
    // Session/OTP is gone — prompt for a fresh OTP.
    public const string Reauthenticate = "reauthenticate";
    // GSP credentials are wrong or not entitled — send them to Settings.
    public const string CheckSettings = "check_settings";
    // The return isn't open yet / period is wrong — show the due date.
    public const string CheckPeriod = "check_period";
    // GSTN has no data — offer to file a NIL return instead.
    public const string FileNil = "file_nil";
    // Already filed — show the existing ARN rather than a retry button.
    public const string ShowArn = "show_arn";
    // Data was rejected — send the user to the validation/preview screen.
    public const string FixInvoices = "fix_invoices";
    // Transient; a plain retry is worth offering.
    public const string Retry = "retry";
}

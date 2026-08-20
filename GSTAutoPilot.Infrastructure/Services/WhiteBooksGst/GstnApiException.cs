using System.Text.Json;
using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Infrastructure.Services.WhiteBooksGst;

// A GSTN/WhiteBooks rejection carrying the portal's own error code, so callers
// can branch on it (re-authenticate, offer NIL filing, show an existing ARN)
// instead of pattern-matching message text.
//
// Deliberately NOT an InvalidOperationException: it used to be, which meant the
// `catch (InvalidOperationException)` in every controller flattened it into a
// bare 400 and threw away the code, the portal's own message and the validation
// report. GstnExceptionFilter handles it globally instead, so a portal rejection
// reaches the UI intact from whichever endpoint raised it.
public class GstnApiException : Exception
{
    public string? Code { get; }
    // GSTN's structured validation report from a retsave, when present.
    public string? ErrorReportJson { get; }
    // The portal's own wording, before Explain() rephrased it.
    public string? PortalMessage { get; }
    // Which call failed, for the log and the support reference.
    public string Operation { get; }
    // The transport status, when the failure was an HTTP one rather than a
    // portal rejection carried in a 200 body.
    public int? HttpStatus { get; }
    // The portal's response verbatim, for the server log. Never returned to the
    // browser — it can carry session/txn values.
    public string? RawBody { get; }

    // True only when repeating the identical request could succeed by itself:
    // a throttle or a gateway/portal outage. No GSTN *business* code is
    // retryable — an expired session (1005) needs a new OTP, which is a user
    // action, and a rejected return needs the data fixed first.
    public bool IsRetryable => HttpStatus is 429 or (>= 500 and < 600);

    // What the UI should offer next.
    public string Action
    {
        get
        {
            var byCode = ActionFor(Code);
            return byCode == GstnErrorAction.None && IsRetryable ? GstnErrorAction.Retry : byCode;
        }
    }

    public GstnApiException(
        string message,
        string? code = null,
        string? errorReportJson = null,
        string? portalMessage = null,
        string? operation = null,
        int? httpStatus = null,
        string? rawBody = null)
        : base(message)
    {
        Code = code;
        ErrorReportJson = errorReportJson;
        PortalMessage = portalMessage;
        Operation = operation ?? string.Empty;
        HttpStatus = httpStatus;
        RawBody = Truncate(rawBody, MaxLoggedBody);
    }

    private const int MaxLoggedBody = 8000;

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max] + $"... [{s.Length - max} more chars]";

    // Code -> next step. Kept beside Explain() so a new code is described and
    // routed in one place.
    public static string ActionFor(string? code) => code switch
    {
        "1005" or "AUTH4033" or "AUTH4034" => GstnErrorAction.Reauthenticate,
        "AUT4031" or "RET191113" or "TEC4002" => GstnErrorAction.CheckSettings,
        "RET191106" => GstnErrorAction.CheckPeriod,
        "RET191115" => GstnErrorAction.FileNil,
        "RET191116" => GstnErrorAction.ShowArn,
        "RETSAVE_ERR" or "RETFILE_ERR" => GstnErrorAction.FixInvoices,
        _ => GstnErrorAction.None,
    };

    // Flattens GSTN's error_report into rows the preview can list. The report
    // arrives in several shapes (bare array, { error_report: [...] }, or grouped
    // by ctin with a nested invoice list), so this walks whatever it is given and
    // picks up any object carrying an error code or message.
    public IReadOnlyList<GstnErrorDetail> ParseDetails()
    {
        var rows = new List<GstnErrorDetail>();
        if (string.IsNullOrWhiteSpace(ErrorReportJson)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(ErrorReportJson);
            Walk(doc.RootElement, null, null, rows);
        }
        catch (JsonException) { /* keep the raw JSON; details are best-effort */ }
        return rows;
    }

    private const int MaxDetails = 200;

    private static void Walk(JsonElement el, string? ctin, string? inum, List<GstnErrorDetail> rows)
    {
        if (rows.Count >= MaxDetails) return;

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray()) Walk(child, ctin, inum, rows);
            return;
        }
        if (el.ValueKind != JsonValueKind.Object) return;

        // Carry the identifiers down: GSTN nests invoice errors under the ctin.
        var thisCtin = Str(el, "ctin") ?? ctin;
        var thisInum = Str(el, "inum", "ntnum", "num", "doc_num") ?? inum;

        var code = Str(el, "error_cd", "errorCode", "error_code", "code");
        var msg = Str(el, "error_msg", "errorMessage", "error_desc", "message", "desc");
        if (code is not null || msg is not null)
        {
            rows.Add(new GstnErrorDetail
            {
                Code = code,
                Message = msg,
                InvoiceNumber = thisInum,
                Ctin = thisCtin,
            });
        }

        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Walk(prop.Value, thisCtin, thisInum, rows);
        }
    }

    private static string? Str(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v)
                && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()))
            {
                return v.GetString();
            }
        }
        return null;
    }

    // Maps the documented WhiteBooks/GSTN codes to something a tax preparer can
    // act on. Unknown codes fall through to the raw portal message.
    public static string Explain(string? code, string? portalMessage)
    {
        var fallback = string.IsNullOrWhiteSpace(portalMessage)
            ? "The GST portal rejected the request."
            : portalMessage!.Trim();

        return code switch
        {
            "1005" => "The GSTN session expired. Re-authenticate with an OTP and try again.",
            "RET191106" => "This return is not open for filing yet. Check the due date for the period — GSTN only accepts filings once the period has closed.",
            "RET191113" => "This GSTIN is not registered for the selected return. Verify the GSTIN and its filing frequency (monthly vs quarterly) in Settings.",
            "RET191115" => "GSTN has no data for this period. If there were genuinely no transactions, file a NIL return instead.",
            "RET191116" => "This return has already been filed for this period. It cannot be filed again — fetch the existing ARN from the portal.",
            "RETSAVE_ERR" => "GSTN rejected the return data during save. See the validation errors below and correct the underlying invoices.",
            "RETFILE_ERR" => "GSTN rejected the filing. See the error detail below.",
            "AUTH4033" => "The GSTN session is invalid or the OTP was already used. Request a fresh OTP.",
            "AUTH4034" => "That OTP is not correct. Check the code sent to the registered mobile/email and try again.",
            // Returned when the GSP credentials are absent, malformed, or not
            // entitled to the GST-returns product (e.g. e-Invoice-only keys).
            "AUT4031" => "WhiteBooks did not accept the GSP credentials for the GST API. Check that WhiteBooksGst:ClientId/ClientSecret are the GST-product credentials — e-Invoice keys will not work here.",
            "TEC4002" => "WhiteBooks rejected the request format. This usually means a required parameter (email, GSTIN, or period) is missing or malformed.",
            _ => fallback,
        };
    }

    // Builds an exception from a WhiteBooks response body, pulling the error
    // code/message/validation report out of the several shapes GSTN uses.
    public static GstnApiException FromBody(string operation, string body, int? httpStatus = null)
    {
        var code = Field(body, "error_cd", "errorCode", "error_code", "code");
        var msg = Field(body, "error_desc", "errorDesc", "message", "status_desc", "error_message");
        var report = Section(body, "error_report", "errorReport", "error_details");

        var explained = Explain(code, msg);
        var prefix = string.IsNullOrWhiteSpace(code) ? operation : $"{operation} ({code})";
        return new GstnApiException($"{prefix}: {explained}", code, report, msg, operation, httpStatus, body);
    }

    private static string? Field(string body, params string[] names)
    {
        foreach (var scope in Scopes(body))
        {
            foreach (var n in names)
            {
                if (scope.TryGetProperty(n, out var v)
                    && v.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                {
                    return v.GetString();
                }
            }
        }
        return null;
    }

    private static string? Section(string body, params string[] names)
    {
        foreach (var scope in Scopes(body))
        {
            foreach (var n in names)
            {
                if (scope.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    return v.GetRawText();
            }
        }
        return null;
    }

    private static List<JsonElement> Scopes(string body)
    {
        var scopes = new List<JsonElement>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            // Clone: the JsonDocument is disposed when this method returns.
            var root = doc.RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var d)) scopes.Add(d);
                if (root.TryGetProperty("error", out var e)) scopes.Add(e);
                scopes.Add(root);
            }
        }
        catch (JsonException) { }
        return scopes;
    }
}

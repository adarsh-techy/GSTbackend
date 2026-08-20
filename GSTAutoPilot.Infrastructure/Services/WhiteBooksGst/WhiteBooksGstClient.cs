using System.Collections.Concurrent;
using System.Text.Json;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using GSTAutoPilot.Infrastructure.Services.WhiteBooks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GSTAutoPilot.Infrastructure.Services.WhiteBooksGst;

public interface IWhiteBooksGstClient
{
    bool IsConfigured { get; }
    // True when a valid (non-expired) OTP session exists for the tenant's GSTIN.
    bool HasSession { get; }
    // Step 1: request an OTP — sent to the taxpayer's GST-portal email + mobile. Returns TXN.
    Task<string> RequestOtpAsync(CancellationToken cancellationToken = default);
    // Step 2: verify OTP + TXN -> caches a ~6h session token for the tenant's GSTIN.
    Task VerifyOtpAsync(string txn, string otp, CancellationToken cancellationToken = default);
    // Raw GSTR-2B JSON for the tenant's GSTIN + period (MMYYYY). Requires an
    // OTP session. `filenum` is the part number — GSTR-2B may be delivered in
    // multiple parts when large; "1" is correct for most taxpayers.
    Task<string> FetchGstr2bRawAsync(string retPeriodMMyyyy, string filenum = "1", CancellationToken cancellationToken = default);
    // Public "search taxpayer" (GSTIN validation) — no OTP/session, just GSP creds.
    Task<string?> SearchTaxpayerRawAsync(string gstin, CancellationToken cancellationToken = default);

    // ----- Return FILING (needs an OTP session). returnType is "gstr1" / "gstr3b",
    // retPeriod is MMYYYY. Flow: retsave -> otpforevc -> retevcfile. There is
    // NO retsubmit endpoint in the WhiteBooks contract. -----
    // Step 1 (PUT /{type}/retsave): save the prepared return JSON to GSTN.
    // A save can succeed at the HTTP level yet carry validation errors, so the
    // result reports both the reference id and GSTN's error_report.
    Task<GstnSaveResult> SaveReturnAsync(string returnType, string retPeriodMMyyyy, string gstnJson, CancellationToken cancellationToken = default);
    // Step 2 (GET /authentication/otpforevc): send the EVC OTP to the authorised
    // signatory's registered mobile/email.
    Task RequestEvcOtpAsync(string returnType, CancellationToken cancellationToken = default);
    // NIL returns only (GET /all/newproceedfile?...&isNil=Y): declares to GSTN
    // that the period is being filed with no transactions. Runs between save and
    // the EVC OTP; the ordinary flow does not call it.
    Task ProceedToFileAsync(string returnType, string retPeriodMMyyyy, bool isNil, CancellationToken cancellationToken = default);
    // Step 3 (POST /{type}/retevcfile?evcotp=...): file the return. The body is
    // the return payload — for GSTR-3B the same JSON as retsave, for GSTR-1 the
    // chksum/sec_sum summary from retsum. Returns the ARN + filing date.
    Task<GstnFileResult> FileReturnAsync(string returnType, string retPeriodMMyyyy, string evcOtp, string filePayloadJson, CancellationToken cancellationToken = default);
    // GET /{type}/retsum — GSTN's own computed summary, for comparing against
    // our figures before filing. For GSTR-1 it also supplies the chksum/sec_sum
    // that retevcfile must echo back. Raw JSON.
    Task<string> GetReturnSummaryRawAsync(string returnType, string retPeriodMMyyyy, CancellationToken cancellationToken = default);
    // GET /gstr/retstatus — poll a saved return by its reference id.
    Task<string> GetReturnStatusAsync(string retPeriodMMyyyy, string referenceId, CancellationToken cancellationToken = default);
    // Validate explicit GSP creds (Settings "Test Connection"). Throws on failure.
    Task TestConnectionAsync(string clientId, string clientSecret, CancellationToken cancellationToken = default);
}

// Outcome of a retsave. HasErrors means GSTN accepted the call but rejected
// some rows — the caller must not advance to submit.
public sealed record GstnSaveResult(string ReferenceId, string? Status, string? ErrorReportJson)
{
    public bool HasErrors => !string.IsNullOrWhiteSpace(ErrorReportJson);
}

public sealed record GstnFileResult(string Arn, DateTime? FilingDate, string? Status);

// Thin HTTP client over the WhiteBooks GST API (returns / GSTR-2B / GSTIN).
// Base https://api.whitebooks.in. The GST-returns APIs use an OTP session:
// GET /authentication/otprequest (OTP to taxpayer) -> TXN, then
// /authentication/authtoken (OTP+TXN) -> token (valid ~6h). /public/search is
// public (no OTP). GSP creds resolve per-tenant (encrypted) with appsettings
// fallback; taxpayer identity (email/gstin/gst_username) is shared with the
// e-Invoice config.
public class WhiteBooksGstClient : IWhiteBooksGstClient
{
    // Session keyed by GSTIN: token + the TXN it was issued under + expiry.
    private static readonly ConcurrentDictionary<string, (string Token, string Txn, DateTime ExpiresUtc)> Sessions = new();

    private readonly HttpClient _http;
    private readonly WhiteBooksGstOptions _gstOptions;
    private readonly WhiteBooksOptions _taxpayerOptions; // e-Invoice section: shared Email/Username/GSTIN
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly MasterDbContext _master;
    private readonly ISecretProtector _protector;
    private readonly ILogger<WhiteBooksGstClient> _logger;

    public WhiteBooksGstClient(
        HttpClient http,
        IOptions<WhiteBooksGstOptions> gstOptions,
        IOptions<WhiteBooksOptions> taxpayerOptions,
        IHttpContextAccessor httpContextAccessor,
        MasterDbContext master,
        ISecretProtector protector,
        ILogger<WhiteBooksGstClient> logger)
    {
        _http = http;
        _gstOptions = gstOptions.Value;
        _taxpayerOptions = taxpayerOptions.Value;
        _httpContextAccessor = httpContextAccessor;
        _master = master;
        _protector = protector;
        _logger = logger;
    }

    private sealed record Creds(string ClientId, string ClientSecret, string BaseUrl, string Email, string Gstin, string Username, bool Configured);

    private Creds Resolve()
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant;
        var ts = tenant is null ? null
            : _master.TenantSettings.AsNoTracking().FirstOrDefault(t => t.TenantId == tenant.TenantId);

        string clientId, clientSecret;
        bool configured;
        if (ts is { WhiteBooksGstEnabled: true } && IsReal(ts.WhiteBooksGstClientId) && IsReal(ts.WhiteBooksGstClientSecret))
        {
            clientId = ts.WhiteBooksGstClientId!.Trim();
            clientSecret = (_protector.TryUnprotect(ts.WhiteBooksGstClientSecret, out var p) ? p : ts.WhiteBooksGstClientSecret!).Trim();
            configured = IsReal(_gstOptions.EffectiveBaseUrl);
        }
        else
        {
            clientId = _gstOptions.ClientId;
            clientSecret = _gstOptions.ClientSecret;
            configured = _gstOptions.IsConfigured;
        }

        // Host comes from EffectiveBaseUrl (WhiteBooksGst:UseSandbox switches to
        // SandboxUrl). Pull the taxpayer-identity fallbacks (email / gstin /
        // username) from the e-Invoice Production credential block.
        var prod = _taxpayerOptions.Production;
        // The GST-returns account email is its OWN setting (WhiteBooksGst:Email)
        // — the WhiteBooks account these creds belong to (e.g. the sandbox
        // support@ account) is not necessarily the e-Invoice Production.Email.
        // Fall back to the e-Invoice email only when no GST email is set.
        var email = IsReal(_gstOptions.Email) ? _gstOptions.Email : prod.Email;
        // Per-tenant GSTIN (Tenants master row) wins over the appsettings
        // fallback — the same anti-pattern as e-Invoice produced NIC 1015 when
        // a sandbox tenant got the prod GSTIN from appsettings.
        var gstin = IsReal(tenant?.GSTIN) ? tenant!.GSTIN : prod.Gstin;
        // GST RETURNS uses its own gst-portal API user (often DIFFERENT from
        // the e-Invoice user). Priority for the username:
        //   1) TenantSettings.WhiteBooksGstUsername (Settings → GST card)
        //   2) WhiteBooksGst:Username from appsettings/user-secrets
        //   3) TenantSettings.WhiteBooksUsername (e-Invoice card — legacy fallback)
        //   4) WhiteBooksEInvoice:Production.Username from appsettings (last resort)
        string username =
            IsReal(ts?.WhiteBooksGstUsername) ? ts!.WhiteBooksGstUsername!.Trim()
            : IsReal(_gstOptions.Username) ? _gstOptions.Username
            : IsReal(ts?.WhiteBooksUsername) ? ts!.WhiteBooksUsername!.Trim()
            : prod.Username;
        return new Creds(clientId, clientSecret, _gstOptions.EffectiveBaseUrl, email, gstin, username, configured);
    }

    public bool IsConfigured => Resolve().Configured;

    public bool HasSession
    {
        get
        {
            var c = Resolve();
            return c.Configured && IsReal(c.Gstin)
                && Sessions.TryGetValue(c.Gstin, out var s) && DateTime.UtcNow < s.ExpiresUtc;
        }
    }

    public async Task<string> RequestOtpAsync(CancellationToken cancellationToken = default)
    {
        var c = Resolve();
        EnsureConfigured(c);
        var url = $"{c.BaseUrl.TrimEnd('/')}/authentication/otprequest?email={Uri.EscapeDataString(c.Email)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddCommonHeaders(req, c);

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw GstnApiException.FromBody("OTP request failed", body, (int)res.StatusCode);
        var txn = ExtractField(body, "txn", "Txn", "TXN")
            ?? throw GstnApiException.FromBody("OTP request", body);
        _logger.LogInformation("WhiteBooks GST OTP requested for {Gstin} (txn {Txn})", c.Gstin, txn);
        return txn;
    }

    public async Task VerifyOtpAsync(string txn, string otp, CancellationToken cancellationToken = default)
    {
        var c = Resolve();
        EnsureConfigured(c);
        if (string.IsNullOrWhiteSpace(txn) || string.IsNullOrWhiteSpace(otp))
            throw new ArgumentException("TXN and OTP are required.");

        var url = $"{c.BaseUrl.TrimEnd('/')}/authentication/authtoken?email={Uri.EscapeDataString(c.Email)}"
            + $"&txn={Uri.EscapeDataString(txn.Trim())}&otp={Uri.EscapeDataString(otp.Trim())}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddCommonHeaders(req, c);

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw GstnApiException.FromBody("OTP verification failed", body, (int)res.StatusCode);
        // GST returns authtoken: success = status_cd "1" — and (unlike e-Invoice)
        // there is NO AuthToken field in the body. The TXN itself becomes the
        // ~6h session reference passed back as the `txn` header on subsequent
        // calls. AUTH4033 "Invalid Session" indicates an expired/used OTP/TXN.
        if (!IsSearchSuccess(body))
            throw GstnApiException.FromBody("OTP verification rejected", body);
        var token = ExtractToken(body) ?? txn.Trim();

        Sessions[c.Gstin] = (token, txn.Trim(), DateTime.UtcNow.AddHours(6).AddMinutes(-5));
        _logger.LogInformation("WhiteBooks GST session established for {Gstin}", c.Gstin);
    }

    public async Task<string> FetchGstr2bRawAsync(string retPeriodMMyyyy, string filenum = "1", CancellationToken cancellationToken = default)
    {
        var c = Resolve();
        EnsureConfigured(c);
        if (!Sessions.TryGetValue(c.Gstin, out var s) || DateTime.UtcNow >= s.ExpiresUtc)
            throw new InvalidOperationException("GST OTP session required. Authenticate with OTP first.");

        // Canonical contract from the WhiteBooks Postman collection
        // (WB-GST-API.postman_collection.json):
        //   GET /gstr2b/all?gstin&rtnprd&filenum&email
        //   headers: gst_username, state_cd, ip_address, txn, client_id, client_secret
        // Note: param is `rtnprd` (NOT `ret_period`), and `filenum` (the part
        // number — "1" for the first/only part) is required. There is NO
        // `auth_token` header on this call; the live ~6h session is referenced
        // solely by the `txn` issued at authtoken time.
        if (string.IsNullOrWhiteSpace(filenum)) filenum = "1";
        var url = $"{c.BaseUrl.TrimEnd('/')}/gstr2b/all"
            + $"?gstin={Uri.EscapeDataString(c.Gstin)}"
            + $"&rtnprd={Uri.EscapeDataString(retPeriodMMyyyy)}"
            + $"&filenum={Uri.EscapeDataString(filenum)}"
            + $"&email={Uri.EscapeDataString(c.Email)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddCommonHeaders(req, c);
        req.Headers.TryAddWithoutValidation("txn", s.Txn);

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw GstnApiException.FromBody("GSTR-2B fetch failed", body, (int)res.StatusCode);
        return body;
    }

    public async Task<string?> SearchTaxpayerRawAsync(string gstin, CancellationToken cancellationToken = default)
    {
        var c = Resolve();
        if (!c.Configured) return null;
        return await SearchAsync(c, gstin, cancellationToken);
    }

    // ----- Return filing -----
    // Contract verified against WB-GST-API.postman_collection.json (the
    // machine-exported WhiteBooks collection), NOT the prose spec — the two
    // disagree and the collection is authoritative. Differences that matter:
    //
    //   * gstin and ret_period are HEADERS on return calls, not query params.
    //     Only `email` (plus `pan` / `evcotp` on file) goes in the query.
    //   * There is NO retsubmit endpoint. The flow is retsave -> retevcfile.
    //   * retsum uses `retperiod`; only /gstr2b/all uses `rtnprd`.
    //   * The EVC OTP comes from GET /authentication/otpforevc and travels as
    //     the `evcotp` QUERY param — retevcfile has no request body.
    //   * retfile carries the RETURN PAYLOAD as its body (for 3B, the same JSON
    //     as retsave; for GSTR-1, a chksum + sec_sum summary from retsum) —
    //     it is not an `{otp}` envelope.
    //
    //   PUT  /{gstr1|gstr3b}/retsave  -> reference_id, status, error_report
    //   GET  /{gstr1|gstr3b}/retsum   -> GSTN's computed summary (+ chksum)
    //   GET  /authentication/otpforevc -> sends EVC OTP to the signatory
    //   POST /{gstr1|gstr3b}/retevcfile?evcotp=... -> ackNo (ARN)
    //   GET  /gstr/retstatus?refid=... -> poll a submitted return
    //
    // Paths come from WhiteBooksGst:Endpoints so a contract change stays a
    // config fix rather than a redeploy.

    public async Task<GstnSaveResult> SaveReturnAsync(string returnType, string retPeriodMMyyyy, string gstnJson, CancellationToken cancellationToken = default)
    {
        var body = await SendReturnAsync(returnType, "save", retPeriodMMyyyy, HttpMethod.Put, gstnJson, cancellationToken);

        var referenceId = ExtractField(body, "reference_id", "ref_id", "referenceId") ?? string.Empty;
        var status = ExtractField(body, "status", "status_cd");
        var errorReport = ExtractSection(body, "error_report", "errorReport", "error_details");

        // A save with no reference id AND no error report means we did not
        // understand the response — treat as a rejection rather than silently
        // proceeding to submit a return GSTN may not have.
        if (string.IsNullOrWhiteSpace(referenceId) && errorReport is null && !IsSearchSuccess(body))
            throw GstnApiException.FromBody($"{returnType} save rejected", body);

        if (errorReport is not null)
            _logger.LogWarning("WhiteBooks {Type} save returned validation errors for {Period}", returnType, retPeriodMMyyyy);
        else
            _logger.LogInformation("WhiteBooks {Type} saved for {Period} (ref {Ref})", returnType, retPeriodMMyyyy, referenceId);

        return new GstnSaveResult(referenceId, status, errorReport);
    }

    // GET /authentication/otpforevc — sends the EVC OTP to the authorised
    // signatory. `pan` defaults to the PAN embedded in the GSTIN (chars 3-12).
    public async Task RequestEvcOtpAsync(string returnType, CancellationToken cancellationToken = default)
    {
        var c = RequireSession(out var s);
        var formType = returnType.Trim().ToUpperInvariant() == "GSTR1" ? "GSTR1" : "GSTR3B";
        var url = $"{c.BaseUrl.TrimEnd('/')}/authentication/otpforevc"
            + $"?email={Uri.EscapeDataString(c.Email)}"
            + $"&gstin={Uri.EscapeDataString(c.Gstin)}"
            + $"&pan={Uri.EscapeDataString(PanFromGstin(c.Gstin))}"
            + $"&form_type={Uri.EscapeDataString(formType)}";

        var body = await SendOnceAsync(c, s.Txn, url, HttpMethod.Get, null, returnType, "otpforevc", cancellationToken);
        if (!IsSearchSuccess(body) && ExtractField(body, "error_cd", "errorCode") is not null)
            throw GstnApiException.FromBody($"{returnType} EVC OTP request failed", body);

        _logger.LogInformation("WhiteBooks EVC OTP requested for {Gstin} ({Form})", c.Gstin, formType);
    }

    // GET /all/newproceedfile?gstin=&retperiod=&type=&isNil=&email= — GSTN's
    // "proceed to file" step, whose isNil flag is how a NIL return is declared.
    // Contract from WB-GST-API_postman_collection.json ("New Proceed To
    // File(GSTR1,GSTR5,GSTR6)"); unlike the other return calls it takes the
    // return type and the period as QUERY params, not headers.
    //
    // Called only for NIL filings. The non-nil path (retsave -> otpforevc ->
    // retevcfile) is proven and is deliberately left untouched, so a change here
    // cannot regress ordinary filing.
    public async Task ProceedToFileAsync(
        string returnType, string retPeriodMMyyyy, bool isNil, CancellationToken cancellationToken = default)
    {
        var c = RequireSession(out var s);
        var formType = returnType.Trim().ToUpperInvariant() == "GSTR1" ? "GSTR1" : "GSTR3B";
        var url = $"{c.BaseUrl.TrimEnd('/')}{_gstOptions.Endpoints.ProceedFile}"
            + $"?gstin={Uri.EscapeDataString(c.Gstin)}"
            + $"&retperiod={Uri.EscapeDataString(retPeriodMMyyyy)}"
            + $"&type={Uri.EscapeDataString(formType)}"
            + $"&isNil={(isNil ? "Y" : "N")}"
            + $"&email={Uri.EscapeDataString(c.Email)}";

        var body = await SendOnceAsync(c, s.Txn, url, HttpMethod.Get, null, returnType, "newproceedfile", cancellationToken);
        if (!IsSearchSuccess(body) && ExtractField(body, "error_cd", "errorCode", "error_code") is not null)
            throw GstnApiException.FromBody($"{returnType} proceed-to-file", body);

        _logger.LogInformation(
            "WhiteBooks {Type} proceed-to-file for {Gstin} {Period} (isNil={IsNil})",
            formType, c.Gstin, retPeriodMMyyyy, isNil);
    }

    // POST /{type}/retevcfile?evcotp=... — the EVC OTP is a QUERY param and the
    // body carries the return payload (3B: the same JSON as retsave; GSTR-1: a
    // chksum/sec_sum summary). Returns the ARN.
    public async Task<GstnFileResult> FileReturnAsync(
        string returnType, string retPeriodMMyyyy, string evcOtp, string filePayloadJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(evcOtp)) throw new ArgumentException("EVC OTP is required.", nameof(evcOtp));
        var c = RequireSession(out var s);

        var url = ReturnUrl(c, returnType, "evcfile", retPeriodMMyyyy)
            + $"&pan={Uri.EscapeDataString(PanFromGstin(c.Gstin))}"
            + $"&evcotp={Uri.EscapeDataString(evcOtp.Trim())}";

        var body = await SendOnceAsync(c, s.Txn, url, HttpMethod.Post, filePayloadJson, returnType, "retevcfile", cancellationToken, retPeriodMMyyyy);

        var arn = ExtractField(body, "ackNo", "ack_no", "arn", "ARN", "ack_num");
        if (string.IsNullOrWhiteSpace(arn))
            throw GstnApiException.FromBody($"{returnType} file did not return an ARN", body);

        var filingDate = ParseGstnDate(ExtractField(body, "fillingDate", "filingDate", "filing_date", "fildt"));
        var status = ExtractField(body, "status", "status_cd");

        _logger.LogInformation("WhiteBooks {Type} FILED for {Gstin} {Period}: ARN {Arn}", returnType, c.Gstin, retPeriodMMyyyy, arn);
        return new GstnFileResult(arn!, filingDate, status);
    }

    public async Task<string> GetReturnSummaryRawAsync(string returnType, string retPeriodMMyyyy, CancellationToken cancellationToken = default)
        => await SendReturnAsync(returnType, "summary", retPeriodMMyyyy, HttpMethod.Get, null, cancellationToken);

    // GET /gstr/retstatus — poll a return by the reference id from retsave.
    public async Task<string> GetReturnStatusAsync(string retPeriodMMyyyy, string referenceId, CancellationToken cancellationToken = default)
    {
        var c = RequireSession(out var s);
        var url = $"{c.BaseUrl.TrimEnd('/')}/gstr/retstatus"
            + $"?gstin={Uri.EscapeDataString(c.Gstin)}"
            + $"&returnperiod={Uri.EscapeDataString(retPeriodMMyyyy)}"
            + $"&refid={Uri.EscapeDataString(referenceId)}"
            + $"&email={Uri.EscapeDataString(c.Email)}";
        return await SendOnceAsync(c, s.Txn, url, HttpMethod.Get, null, "gstr", "retstatus", cancellationToken);
    }

    // The PAN sits inside the GSTIN: 2 state digits + 10 PAN chars + 3 more.
    private static string PanFromGstin(string gstin)
        => gstin.Length >= 12 ? gstin.Substring(2, 10) : string.Empty;

    // Single path for every return call: builds the URL, attaches session +
    // common headers, and on an expired session (1005) drops the cached session
    // and surfaces a "re-authenticate" error. It does NOT retry — a GSTN session
    // is only renewable with a fresh OTP, which only the user can supply, so an
    // in-place retry would fail identically.
    private async Task<string> SendReturnAsync(
        string returnType, string action, string retPeriod, HttpMethod method, string? jsonBody, CancellationToken ct)
    {
        var c = RequireSession(out var s);
        var url = ReturnUrl(c, returnType, action, retPeriod);

        var body = await SendOnceAsync(c, s.Txn, url, method, jsonBody, returnType, action, ct, retPeriod);
        var code = ExtractField(body, "error_cd", "errorCode", "error_code");
        if (code is not "1005") return body;

        // Session expired mid-flow: drop it so HasSession reports false and the
        // UI prompts for a fresh OTP rather than looping on a dead session.
        Sessions.TryRemove(c.Gstin, out _);
        _logger.LogWarning("WhiteBooks GST session expired (1005) during {Type} {Action}; session cleared", returnType, action);
        throw new GstnApiException(
            $"{returnType} {action}: {GstnApiException.Explain("1005", null)}",
            "1005", errorReportJson: null, portalMessage: null,
            operation: $"{returnType} {action}", httpStatus: null, rawBody: body);
    }

    private async Task<string> SendOnceAsync(
        Creds c, string txn, string url, HttpMethod method, string? jsonBody, string returnType, string action,
        CancellationToken ct, string? retPeriod = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        AddCommonHeaders(req, c);
        if (retPeriod is not null) AddReturnHeaders(req, c, retPeriod);
        req.Headers.TryAddWithoutValidation("txn", txn);

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw GstnApiException.FromBody($"{returnType} {action} failed", body, (int)res.StatusCode);
        return body;
    }

    // GSTN returns dates as dd-MM-yyyy (sometimes with a time component).
    private static DateTime? ParseGstnDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] formats = ["dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm", "dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd"];
        return DateTime.TryParseExact(raw.Trim(), formats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static string? ExtractSection(string body, params string[] names)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var scopes = new List<JsonElement> { root };
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d)) scopes.Insert(0, d);
            foreach (var scope in scopes)
            {
                if (scope.ValueKind != JsonValueKind.Object) continue;
                foreach (var n in names)
                {
                    if (!scope.TryGetProperty(n, out var v)) continue;
                    // An empty object/array means "no errors" — don't report it
                    // as a validation failure.
                    if (v.ValueKind == JsonValueKind.Object && v.EnumerateObject().Any()) return v.GetRawText();
                    if (v.ValueKind == JsonValueKind.Array && v.EnumerateArray().Any()) return v.GetRawText();
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    private Creds RequireSession(out (string Token, string Txn, DateTime ExpiresUtc) session)
    {
        var c = Resolve();
        EnsureConfigured(c);
        if (!Sessions.TryGetValue(c.Gstin, out session) || DateTime.UtcNow >= session.ExpiresUtc)
            throw new InvalidOperationException("GST OTP session required. Authenticate with OTP first.");
        return c;
    }

    private string ReturnUrl(Creds c, string returnType, string action, string retPeriod)
    {
        var path = _gstOptions.Endpoints.For(action, returnType);
        var url = $"{c.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}"
            + $"?email={Uri.EscapeDataString(c.Email)}";
        // retsum is the exception: it takes gstin/retperiod in the QUERY (and
        // spells the period `retperiod`, not `rtnprd`). save/file take them as
        // headers only — see AddReturnHeaders.
        if (action == "summary")
        {
            url += $"&gstin={Uri.EscapeDataString(c.Gstin)}"
                 + $"&retperiod={Uri.EscapeDataString(retPeriod)}";
        }
        return url;
    }

    // gstin + ret_period ride as headers on retsave/retfile/retevcfile.
    private static void AddReturnHeaders(HttpRequestMessage req, Creds c, string retPeriod)
    {
        req.Headers.TryAddWithoutValidation("gstin", c.Gstin);
        req.Headers.TryAddWithoutValidation("ret_period", retPeriod);
    }

    public async Task TestConnectionAsync(string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        var c = Resolve() with { ClientId = clientId.Trim(), ClientSecret = clientSecret.Trim() };
        if (!IsReal(c.BaseUrl))
            throw new InvalidOperationException("WhiteBooks GST BaseUrl is not configured.");
        if (!IsReal(c.Gstin))
            throw new InvalidOperationException("A GSTIN is required to test the GST API (set WhiteBooksEInvoice:GSTIN).");
        // Validate creds via the public search API (no OTP needed).
        var body = await SearchAsync(c, c.Gstin, cancellationToken);
        if (!IsSearchSuccess(body))
            throw GstnApiException.FromBody("GST API connection test failed", body);
    }

    private async Task<string> SearchAsync(Creds c, string gstin, CancellationToken cancellationToken)
    {
        var url = $"{c.BaseUrl.TrimEnd('/')}/public/search?email={Uri.EscapeDataString(c.Email)}&gstin={Uri.EscapeDataString(gstin)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("client_id", c.ClientId);
        req.Headers.TryAddWithoutValidation("client_secret", c.ClientSecret);
        if (IsReal(c.Username)) req.Headers.TryAddWithoutValidation("gst_username", c.Username);
        if (gstin.Length >= 2) req.Headers.TryAddWithoutValidation("state_cd", gstin[..2]);
        req.Headers.TryAddWithoutValidation("ip_address", "0.0.0.0");

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw GstnApiException.FromBody("GSTIN lookup failed", body, (int)res.StatusCode);
        return body;
    }

    private static void AddCommonHeaders(HttpRequestMessage req, Creds c)
    {
        req.Headers.TryAddWithoutValidation("client_id", c.ClientId);
        req.Headers.TryAddWithoutValidation("client_secret", c.ClientSecret);
        if (IsReal(c.Username)) req.Headers.TryAddWithoutValidation("gst_username", c.Username);
        if (IsReal(c.Gstin) && c.Gstin.Length >= 2) req.Headers.TryAddWithoutValidation("state_cd", c.Gstin[..2]);
        req.Headers.TryAddWithoutValidation("ip_address", "0.0.0.0");
    }

    private static void EnsureConfigured(Creds c)
    {
        if (!c.Configured) throw new InvalidOperationException("WhiteBooks GST API is not configured (missing credentials).");
        if (!IsReal(c.Email)) throw new InvalidOperationException("WhiteBooks account email is not configured.");
        if (!IsReal(c.Gstin)) throw new InvalidOperationException("Taxpayer GSTIN is not configured.");
    }

    private static bool IsSearchSuccess(string body)
    {
        try { using var d = JsonDocument.Parse(body); return d.RootElement.TryGetProperty("status_cd", out var s) && s.GetString() == "1"; }
        catch (JsonException) { return false; }
    }

    private static string? ExtractToken(string body) => ExtractField(body, "AuthToken", "auth_token", "Token", "token");

    private static string? ExtractField(string body, params string[] names)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // WhiteBooks puts response fields in different places depending on
            // the call: `data` (most), top-level (status_cd/status_desc), or
            // `header` (otprequest TXN echoes alongside request headers).
            var scopes = new List<JsonElement> { root };
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var d)) scopes.Insert(0, d);
                if (root.TryGetProperty("header", out var h)) scopes.Add(h);
            }
            foreach (var scope in scopes)
            {
                if (scope.ValueKind != JsonValueKind.Object) continue;
                foreach (var n in names)
                {
                    if (!scope.TryGetProperty(n, out var v)) continue;
                    if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString();
                    if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static bool IsReal(string? v) => WhiteBooksGstOptions.IsReal(v);
    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    // Caller-side period conversion (YYYYMM -> MMYYYY) for ret_period.
    public static string ToRetPeriod(string yyyymm)
        => yyyymm.Length == 6 ? yyyymm[4..] + yyyymm[..4] : yyyymm;
}

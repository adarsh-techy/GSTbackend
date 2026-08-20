using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GSTAutoPilot.Infrastructure.Services.WhiteBooks;

public interface IWhiteBooksClient
{
    bool IsConfigured { get; }
    string SourceLabel { get; }
    // The GSTIN that auth will be made against (sandbox: shared BVMGSP GSTIN
    // 29AAGCB1286Q000; production: per-tenant GSTIN). EInvoiceService uses this
    // as the seller GSTIN in the NIC payload — otherwise a Flooratex bill
    // (32AABCF…) sent in sandbox mode gets NIC 1015 "Invalid GSTIN for this user".
    string ActiveGstin { get; }
    bool IsSandbox { get; }
    Task<EInvoiceProviderResult> GenerateIrnAsync(object invoicePayload, CancellationToken cancellationToken = default);
    // Cancel an IRN within the NIC 24-hour window. reason is "1".."4"; remarks is free text.
    Task CancelIrnAsync(string irn, string reason, string remarks, CancellationToken cancellationToken = default);
    // Validate explicit creds (used by the Settings "Test Connection" button
    // before they're persisted). Throws with the provider message on failure.
    // useSandbox=true uses the appsettings sandbox cred set as-is and ignores
    // the explicit args (clients have nothing to override in sandbox).
    Task TestConnectionAsync(string clientId, string clientSecret, bool useSandbox, string? username = null, string? password = null, CancellationToken cancellationToken = default);
}

// Thin HTTP client over the WhiteBooks GSP e-Invoice API. Sandbox creds come
// from appsettings (shared BVMGSP test account); production creds are
// per-tenant overrides from TenantSettings (entered in Settings → API Config).
// Auth tokens are cached in-memory per client_id.
public class WhiteBooksClient : IWhiteBooksClient
{
    // Cache the (AuthToken, Txn) pair per client_id. Txn is WhiteBooks's session
    // reference returned in `header.txn` of the auth response and required on
    // every subsequent /einvoice call — without it NIC returns 1005 "Invalid Token".
    private static readonly ConcurrentDictionary<string, (string Token, string Txn, DateTime ExpiresUtc)> TokenCache = new();

    // Outbound IP is required as the ip_address header on every auth call.
    // Cache after first detection — it doesn't change during process lifetime.
    private static string? _cachedLocalIp;

    // NIC's e-Invoice schema is strict PascalCase (Version / TranDtls / DocDtls / ...).
    // JsonContent.Create with no options defaults to JsonSerializerOptions.Web in
    // .NET 5+, which camelCases properties — that produced "JSON validation failed
    // due to required key [Version] not found" rejections. Keep names as declared.
    private static readonly JsonSerializerOptions NicJson = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly WhiteBooksOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly MasterDbContext _master;
    private readonly GSTAutoPilot.Infrastructure.CarolERP.CarolERPDbContext _carol;
    private readonly ILogger<WhiteBooksClient> _logger;

    // Per-request cache for the active GST group's GSTIN. CompanyGroupsAsync
    // reads CarolERP, so we resolve it at most once per WhiteBooksClient
    // instance (scoped per request) even though Resolve() runs from multiple
    // synchronous property getters in the same request.
    private string? _activeGroupGstinCache;
    private bool _activeGroupGstinResolved;

    public WhiteBooksClient(
        HttpClient http,
        IOptions<WhiteBooksOptions> options,
        IHttpContextAccessor httpContextAccessor,
        MasterDbContext master,
        GSTAutoPilot.Infrastructure.CarolERP.CarolERPDbContext carol,
        ILogger<WhiteBooksClient> logger)
    {
        _http = http;
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _master = master;
        _carol = carol;
        _logger = logger;
    }

    // Returns the GSTIN of the currently-selected GST group, falling back to
    // tenant.GSTIN when (a) no company is picked, (b) CarolERP is unreachable
    // or has no groups, or (c) the picked CoId isn't in any group.
    //
    // Sync-over-async because Resolve() is called from sync property getters
    // (IsConfigured / SourceLabel / ActiveGstin / IsSandbox). Refactoring
    // those to async would cascade into the IWhiteBooksClient interface and
    // every caller (EInvoiceService etc). CarolERPDbContext is per-request
    // scoped, so the actual DB hit happens at most once per request.
    private string ResolveActiveGroupGstin(Tenant tenant)
    {
        if (_activeGroupGstinResolved) return _activeGroupGstinCache ?? tenant.GSTIN;
        try
        {
            if (_carol.ActiveCompanyId is byte coId)
            {
                var groups = _carol.CompanyGroupsAsync().GetAwaiter().GetResult();
                var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(coId));
                if (!string.IsNullOrWhiteSpace(group?.Gstin))
                {
                    _activeGroupGstinCache = group.Gstin;
                    _logger.LogDebug(
                        "WhiteBooks active-group GSTIN resolved: tenant {Tenant} CoId {CoId} → {Gstin}",
                        tenant.TenantId, coId, group.Gstin);
                }
            }
        }
        catch (Exception ex)
        {
            // CarolERP unreachable or schema mismatch — fall back to
            // tenant.GSTIN silently. Logging at Debug so noisy CarolERP
            // failures don't spam Warning unless a downstream call actually
            // needs the GSTIN (and will surface its own error then).
            _logger.LogDebug(ex,
                "Could not resolve per-company GSTIN for tenant {Tenant}; falling back to tenant.GSTIN",
                tenant.TenantId);
        }
        _activeGroupGstinResolved = true;
        return _activeGroupGstinCache ?? tenant.GSTIN;
    }

    private sealed record Creds(string ClientId, string ClientSecret, string BaseUrl, string Gstin,
        string Email, string Username, string Password, bool Sandbox, bool Configured);

    private Creds Resolve()
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant;
        var useSandbox = tenant is not null
            ? GetTenantSettings(tenant)?.WhiteBooksUseSandbox ?? _options.UseSandbox
            : _options.UseSandbox;

        if (useSandbox)
        {
            // Sandbox is a SHARED WhiteBooks account — same BVMGSP creds for
            // every tenant. Ignore per-tenant overrides entirely; use the
            // appsettings sandbox block as-is.
            var s = _options.Sandbox;
            return new Creds(s.ClientId, s.ClientSecret, _options.SandboxUrl, s.Gstin,
                s.Email, s.Username, s.Password, true,
                _options.IsEnabled && IsReal(s.ClientId) && IsReal(s.ClientSecret) && IsReal(_options.SandboxUrl) && IsReal(s.Gstin));
        }

        // Production: prefer per-tenant overrides; fall back to appsettings
        // Production block for fields the tenant hasn't filled in.
        var p = _options.Production;
        if (tenant is not null)
        {
            var ts = GetTenantSettings(tenant);
            if (ts is { WhiteBooksEnabled: true }
                && IsReal(ts.WhiteBooksClientId) && IsReal(ts.WhiteBooksClientSecret))
            {
                // Active GST group's GSTIN wins for multi-GST tenants (Group 2
                // files under its own GST registration with its own WhiteBooks
                // account). Falls back to tenant.GSTIN for single-GST tenants
                // and to appsettings only when both are blank.
                var activeGstin = ResolveActiveGroupGstin(tenant);
                var gstin = IsReal(activeGstin) ? activeGstin : p.Gstin;
                var username = IsReal(ts.WhiteBooksUsername) ? ts.WhiteBooksUsername!.Trim() : p.Username;
                var password = IsReal(ts.WhiteBooksPassword) ? ts.WhiteBooksPassword!.Trim() : p.Password;
                return new Creds(ts.WhiteBooksClientId!.Trim(), ts.WhiteBooksClientSecret!.Trim(),
                    _options.ProductionUrl, gstin, p.Email, username, password,
                    false, IsReal(_options.ProductionUrl) && IsReal(gstin));
            }
        }
        // No tenant override -> straight appsettings production fallback (only
        // works if appsettings.Production was populated, which we don't ship).
        return new Creds(p.ClientId, p.ClientSecret, _options.ProductionUrl, p.Gstin,
            p.Email, p.Username, p.Password, false,
            _options.IsEnabled && IsReal(p.ClientId) && IsReal(p.ClientSecret) && IsReal(_options.ProductionUrl) && IsReal(p.Gstin));
    }

    // Per-company aware — mirrors TenantSettingsService.ReadCredsRowAsync so
    // the IRN / EWB generation path picks the same row that Settings → API
    // Config shows. Without this, multi-GST tenants would get whichever row
    // EF happened to return first, defeating the whole per-company split.
    private TenantSettings? GetTenantSettings(Tenant tenant)
    {
        var coId = _httpContextAccessor.HttpContext?.Items["CompanyId"] is byte b ? (byte?)b : null;
        if (coId.HasValue)
        {
            var perCompany = _master.TenantSettings.AsNoTracking()
                .FirstOrDefault(t => t.TenantId == tenant.TenantId && t.CompanyId == coId);
            if (perCompany is not null) return perCompany;
        }
        return _master.TenantSettings.AsNoTracking()
            .FirstOrDefault(t => t.TenantId == tenant.TenantId && t.CompanyId == null);
    }

    public bool IsConfigured => Resolve().Configured;
    public string SourceLabel => Resolve().Sandbox ? "WHITEBOOKS_SANDBOX" : "WHITEBOOKS_PROD";
    public string ActiveGstin => Resolve().Gstin;
    public bool IsSandbox => Resolve().Sandbox;

    public async Task TestConnectionAsync(string clientId, string clientSecret, bool useSandbox, string? username = null, string? password = null, CancellationToken cancellationToken = default)
    {
        Creds c;
        if (useSandbox)
        {
            // Sandbox creds are shared — never accept user input here; just
            // verify the BVMGSP defaults still authenticate.
            var s = _options.Sandbox;
            if (!IsReal(_options.SandboxUrl)) throw new InvalidOperationException("Sandbox URL is not configured.");
            c = new Creds(s.ClientId, s.ClientSecret, _options.SandboxUrl, s.Gstin,
                s.Email, s.Username, s.Password, true, true);
        }
        else
        {
            if (!IsReal(_options.ProductionUrl)) throw new InvalidOperationException("Production URL is not configured.");
            // Test the production credentials being entered. Username/password
            // override the resolved values when supplied (blank password on edit
            // keeps the stored one); gstin / email come from the resolved config.
            var resolved = Resolve();
            c = resolved with
            {
                ClientId = clientId.Trim(),
                ClientSecret = clientSecret.Trim(),
                BaseUrl = _options.ProductionUrl,
                Sandbox = false,
            };
            if (IsReal(username)) c = c with { Username = username!.Trim() };
            if (IsReal(password)) c = c with { Password = password!.Trim() };
        }
        await AuthenticateAsync(c, cancellationToken);
    }

    public async Task<EInvoiceProviderResult> GenerateIrnAsync(object invoicePayload, CancellationToken cancellationToken = default)
    {
        var c = Resolve();
        if (!c.Configured)
            throw new InvalidOperationException("WhiteBooks e-Invoice is not configured (missing credentials).");

        var (token, txn) = await GetAuthTokenAsync(c, cancellationToken);

        // WhiteBooks IRN generation: POST /einvoice/type/GENERATE/version/V1_03
        // (the old /ei/api/invoice path is NOT served — it 200s "Invalid
        // request!" and never reaches NIC). Requires the same client_id /
        // client_secret / gstin / username context as the auth call, PLUS both
        // auth_token AND the per-session `txn` echoed back from auth.header.txn
        // — without txn, NIC returns 1005 "Invalid Token". The invoice JSON
        // (NIC v1.1 schema) is sent as plain JSON; WhiteBooks handles SEK.
        var irnUrl = $"{c.BaseUrl.TrimEnd('/')}/einvoice/type/GENERATE/version/V1_03";
        if (IsReal(c.Email)) irnUrl += $"?email={Uri.EscapeDataString(c.Email.Trim())}";
        using var req = new HttpRequestMessage(HttpMethod.Post, irnUrl)
        {
            Content = JsonContent.Create(invoicePayload, options: NicJson),
        };
        req.Headers.TryAddWithoutValidation("client_id", c.ClientId);
        req.Headers.TryAddWithoutValidation("client_secret", c.ClientSecret);
        req.Headers.TryAddWithoutValidation("gstin", c.Gstin);
        req.Headers.TryAddWithoutValidation("ip_address", GetLocalIp());
        if (IsReal(c.Username)) req.Headers.TryAddWithoutValidation("username", c.Username);
        // Header MUST be `auth-token` (HYPHEN). `auth_token` (underscore) is
        // silently ignored by WhiteBooks's edge — they then proxy to NIC with
        // an empty/cached token, NIC rejects with 1005 "Invalid Token". The
        // hyphen vs underscore was a multi-day debugging rathole.
        req.Headers.TryAddWithoutValidation("auth-token", token);

        // Diagnostic: log redacted token + the request context.
        var bodyJson = await req.Content!.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation(
            "WhiteBooks IRN POST {Url} gstin={Gstin} user={User} tokenLen={Len} tokenHead={Head} txnHead={TxnHead} bodyLen={BodyLen} bodyHead={BodyHead}",
            irnUrl, c.Gstin, c.Username, token?.Length ?? 0, Head(token, 12),
            Head(txn, 12), bodyJson.Length, Head(bodyJson, 120));

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("WhiteBooks IRN response HTTP {Status}: {Body}",
            (int)res.StatusCode, Truncate(body));
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"WhiteBooks IRN generation failed: HTTP {(int)res.StatusCode}. {Truncate(body)}");
        }

        var env = JsonSerializer.Deserialize<WhiteBooksEnvelope<WhiteBooksIrnData>>(body);
        if (env is null || !env.IsSuccess || env.Data is null || string.IsNullOrWhiteSpace(env.Data.Irn))
        {
            throw new InvalidOperationException($"WhiteBooks IRN generation rejected: {env?.StatusDesc ?? Truncate(body)}");
        }

        var d = env.Data;
        return new EInvoiceProviderResult(
            Irn: d.Irn!,
            AckNo: d.AckNo?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            AckDate: ParseAckDate(d.AckDt),
            SignedInvoice: d.SignedInvoice ?? string.Empty,
            SignedQrCode: d.SignedQRCode ?? string.Empty,
            Status: string.IsNullOrWhiteSpace(d.Status) ? "ACT" : d.Status!);
    }

    public async Task CancelIrnAsync(string irn, string reason, string remarks, CancellationToken cancellationToken = default)
    {
        var c = Resolve();
        if (!c.Configured)
            throw new InvalidOperationException("WhiteBooks e-Invoice is not configured (missing credentials).");

        var (token, txn) = await GetAuthTokenAsync(c, cancellationToken);

        // WhiteBooks cancel: POST /einvoice/type/CANCEL/version/V1_03 (the
        // /ei/api/invoice/cancel path is not served — same NIC-direct mismatch
        // as generate). Body is the NIC cancel schema. Requires auth_token +
        // session txn for the same reason as generate.
        var url = $"{c.BaseUrl.TrimEnd('/')}/einvoice/type/CANCEL/version/V1_03";
        if (IsReal(c.Email)) url += $"?email={Uri.EscapeDataString(c.Email.Trim())}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { Irn = irn, CnlRsn = reason, CnlRem = remarks }, options: NicJson),
        };
        req.Headers.TryAddWithoutValidation("client_id", c.ClientId);
        req.Headers.TryAddWithoutValidation("client_secret", c.ClientSecret);
        req.Headers.TryAddWithoutValidation("gstin", c.Gstin);
        req.Headers.TryAddWithoutValidation("ip_address", GetLocalIp());
        if (IsReal(c.Username)) req.Headers.TryAddWithoutValidation("username", c.Username);
        // `auth-token` HYPHEN — see GenerateIrnAsync for the gory history.
        req.Headers.TryAddWithoutValidation("auth-token", token);

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"WhiteBooks IRN cancel failed: HTTP {(int)res.StatusCode}. {Truncate(body)}");

        var env = JsonSerializer.Deserialize<WhiteBooksEnvelope<object>>(body);
        if (env is null || !env.IsSuccess)
            throw new InvalidOperationException($"WhiteBooks IRN cancel rejected: {env?.StatusDesc ?? Truncate(body)}");
        _logger.LogInformation("WhiteBooks IRN {Irn} cancelled", irn);
    }

    private async Task<(string Token, string Txn)> GetAuthTokenAsync(Creds c, CancellationToken cancellationToken)
    {
        if (TokenCache.TryGetValue(c.ClientId, out var cached) && DateTime.UtcNow < cached.ExpiresUtc)
        {
            return (cached.Token, cached.Txn);
        }
        var (token, txn, expiry) = await AuthenticateAsync(c, cancellationToken);
        TokenCache[c.ClientId] = (token, txn, expiry);
        return (token, txn);
    }

    // WhiteBooks GSP auth: GET /einvoice/authenticate?email={accountEmail} with
    // client_id / client_secret / gstin / ip_address / username / password
    // headers. Returns the standard envelope with data.AuthToken + data.Sek.
    // (The old /auth/api/login POST is an NIC-direct path WhiteBooks doesn't
    // serve — it ignores creds and always 200s with "Invalid request!".)
    private async Task<(string Token, string Txn, DateTime ExpiresUtc)> AuthenticateAsync(Creds c, CancellationToken cancellationToken)
    {
        if (!IsReal(c.Email))
            throw new InvalidOperationException("WhiteBooks account email is not configured for this environment.");

        var url = $"{c.BaseUrl.TrimEnd('/')}/einvoice/authenticate?email={Uri.EscapeDataString(c.Email.Trim())}";
        _logger.LogInformation(
            "WhiteBooks auth GET {Url} clientId={ClientId} gstin={Gstin} user={User} emailLen={EmailLen} env={Env}",
            url, c.ClientId, c.Gstin, c.Username, c.Email?.Length ?? 0, c.Sandbox ? "SANDBOX" : "PROD");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("client_id", c.ClientId);
        req.Headers.TryAddWithoutValidation("client_secret", c.ClientSecret);
        if (IsReal(c.Gstin)) req.Headers.TryAddWithoutValidation("gstin", c.Gstin);
        if (IsReal(c.Username)) req.Headers.TryAddWithoutValidation("username", c.Username);
        if (IsReal(c.Password)) req.Headers.TryAddWithoutValidation("password", c.Password);
        // WhiteBooks requires ip_address header — NIC binds the session to the
        // calling server's outbound IP. Without it auth still returns a token
        // but subsequent /einvoice calls 1005 "Invalid Token".
        req.Headers.TryAddWithoutValidation("ip_address", GetLocalIp());

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"WhiteBooks auth failed: HTTP {(int)res.StatusCode}. {Truncate(body)}");
        }

        var env = JsonSerializer.Deserialize<WhiteBooksEnvelope<WhiteBooksAuthData>>(body);
        if (env is null || !env.IsSuccess || string.IsNullOrWhiteSpace(env.Data?.AuthToken))
        {
            throw new InvalidOperationException($"WhiteBooks auth rejected: {env?.StatusDesc ?? Truncate(body)}");
        }

        var expiry = ResolveExpiry(env.Data!.TokenExpiry);
        // header.txn is WhiteBooks's per-session transaction id — REQUIRED on
        // every subsequent /einvoice call as the `txn` header. Without it NIC
        // returns 1005 "Invalid Token" even when AuthToken is freshly valid.
        var txn = env.Header?.Txn ?? string.Empty;
        _logger.LogInformation(
            "WhiteBooks auth OK for {ClientId} gstin={Gstin} user={User} env={Env} | tokenLen={TokenLen} tokenHead={TokenHead} sekLen={SekLen} txnHead={TxnHead} expiry={Expiry:u}",
            c.ClientId, c.Gstin, c.Username, c.Sandbox ? "SANDBOX" : "PROD",
            env.Data.AuthToken!.Length, Head(env.Data.AuthToken, 12),
            env.Data.Sek?.Length ?? 0, Head(txn, 12), expiry);
        return (env.Data.AuthToken!, txn, expiry);
    }

    private static string GetLocalIp()
    {
        if (_cachedLocalIp is not null) return _cachedLocalIp;
        try
        {
            // Doesn't actually open a connection (UDP) — just asks the OS which
            // local interface would route to a public address. Returns the
            // outbound LAN/NAT IP, which is what WhiteBooks records.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            var ip = (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
            _cachedLocalIp = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip;
        }
        catch
        {
            _cachedLocalIp = "127.0.0.1";
        }
        return _cachedLocalIp;
    }

    private static bool IsReal(string? v) => !string.IsNullOrWhiteSpace(v) && !v.TrimStart().StartsWith('[');

    private static DateTime ResolveExpiry(string? tokenExpiry)
    {
        if (!string.IsNullOrWhiteSpace(tokenExpiry)
            && DateTime.TryParse(tokenExpiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed.AddMinutes(-5);
        }
        return DateTime.UtcNow.AddHours(6);
    }

    private static DateTime ParseAckDate(string? ackDt)
        => DateTime.TryParse(ackDt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : DateTime.UtcNow;

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
    private static string Head(string? s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s[..n] + "…");
}

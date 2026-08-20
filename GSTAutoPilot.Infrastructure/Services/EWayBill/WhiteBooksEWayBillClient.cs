using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using GSTAutoPilot.Infrastructure.Services.WhiteBooks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GSTAutoPilot.Infrastructure.Services.EwbApi;

public sealed record EWayBillProviderResult(string EwbNo, DateTime ValidUntil);

public interface IWhiteBooksEWayBillClient
{
    bool IsConfigured { get; }
    string SourceLabel { get; }
    // Seller GSTIN auth + the EWB will be filed under (per-company aware).
    string ActiveGstin { get; }
    Task<EWayBillProviderResult> GenerateAsync(object payload, CancellationToken cancellationToken = default);
    // reasonCode: 1=Duplicate, 2=Order cancelled, 3=Data entry mistake, 4=Others.
    Task CancelAsync(string ewbNo, string reasonCode, string remarks, CancellationToken cancellationToken = default);
    Task UpdateVehicleAsync(object payload, CancellationToken cancellationToken = default);
}

// Thin HTTP client over the WhiteBooks e-Way Bill API. Same shape as the proven
// e-Invoice WhiteBooksClient: username/password auth -> ~6h token (cached per
// client_id), then genewaybill / cancelewb / vehewb with an auth-token header.
// GSP client_id/secret + taxpayer login fall back to the e-Invoice Production
// block; the seller GSTIN is the active GST group's (per-company aware).
//
// NOTE: built against the documented WhiteBooks/NIC EWB v1.03 contract but not
// yet verified against a live account — endpoint paths and the exact auth-token
// header spelling are config/centralised here so they can be corrected without
// touching call sites (the e-Invoice path needed live debugging of the same).
public class WhiteBooksEWayBillClient : IWhiteBooksEWayBillClient
{
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresUtc)> TokenCache = new();
    private static string? _cachedLocalIp;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly WhiteBooksEWayBillOptions _ewb;
    private readonly WhiteBooksOptions _einv; // e-Invoice block: cred fallbacks
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly MasterDbContext _master;
    private readonly GSTAutoPilot.Infrastructure.CarolERP.CarolERPDbContext _carol;
    private readonly ISecretProtector _protector;
    private readonly ILogger<WhiteBooksEWayBillClient> _logger;

    private string? _groupGstinCache;
    private bool _groupGstinResolved;

    public WhiteBooksEWayBillClient(
        HttpClient http,
        IOptions<WhiteBooksEWayBillOptions> ewb,
        IOptions<WhiteBooksOptions> einv,
        IHttpContextAccessor httpContextAccessor,
        MasterDbContext master,
        GSTAutoPilot.Infrastructure.CarolERP.CarolERPDbContext carol,
        ISecretProtector protector,
        ILogger<WhiteBooksEWayBillClient> logger)
    {
        _http = http;
        _ewb = ewb.Value;
        _einv = einv.Value;
        _httpContextAccessor = httpContextAccessor;
        _master = master;
        _carol = carol;
        _protector = protector;
        _logger = logger;
    }

    private sealed record Creds(string ClientId, string ClientSecret, string BaseUrl, string Gstin,
        string Email, string Username, string Password, bool Configured);

    private Creds Resolve()
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant;
        var ts = tenant is null ? null : GetTenantSettings(tenant);
        var prod = _einv.Production;

        // GSP creds: EWB section -> per-tenant e-Invoice creds -> e-Invoice prod.
        var clientId = IsReal(_ewb.ClientId) ? _ewb.ClientId
            : IsReal(ts?.WhiteBooksClientId) ? ts!.WhiteBooksClientId!.Trim()
            : prod.ClientId;
        var clientSecret = IsReal(_ewb.ClientSecret) ? _ewb.ClientSecret
            : IsReal(ts?.WhiteBooksClientSecret) ? Unprotect(ts!.WhiteBooksClientSecret!)
            : prod.ClientSecret;

        // Taxpayer NIC EWB login: EWB section -> per-tenant -> e-Invoice prod.
        var username = IsReal(_ewb.Username) ? _ewb.Username
            : IsReal(ts?.WhiteBooksUsername) ? ts!.WhiteBooksUsername!.Trim()
            : prod.Username;
        var password = IsReal(_ewb.Password) ? _ewb.Password
            : IsReal(ts?.WhiteBooksPassword) ? Unprotect(ts!.WhiteBooksPassword!)
            : prod.Password;

        var gstin = tenant is null ? prod.Gstin : ResolveActiveGroupGstin(tenant);
        if (!IsReal(gstin)) gstin = prod.Gstin;

        var configured = _ewb.BaseConfigured && IsReal(clientId) && IsReal(clientSecret) && IsReal(gstin);
        return new Creds(clientId, clientSecret, _ewb.BaseUrl, gstin, prod.Email, username, password, configured);
    }

    private string Unprotect(string stored) => _protector.TryUnprotect(stored, out var p) ? p : stored;

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

    private string ResolveActiveGroupGstin(Tenant tenant)
    {
        if (_groupGstinResolved) return _groupGstinCache ?? tenant.GSTIN;
        try
        {
            if (_carol.ActiveCompanyId is byte coId)
            {
                var groups = _carol.CompanyGroupsAsync().GetAwaiter().GetResult();
                var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(coId));
                if (!string.IsNullOrWhiteSpace(group?.Gstin)) _groupGstinCache = group.Gstin;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EWB: could not resolve per-company GSTIN for {Tenant}; using tenant.GSTIN", tenant.TenantId);
        }
        _groupGstinResolved = true;
        return _groupGstinCache ?? tenant.GSTIN;
    }

    public bool IsConfigured => Resolve().Configured;
    public string SourceLabel => "WHITEBOOKS_EWB";
    public string ActiveGstin => Resolve().Gstin;

    public async Task<EWayBillProviderResult> GenerateAsync(object payload, CancellationToken cancellationToken = default)
    {
        var c = Resolve();
        EnsureConfigured(c);
        var token = await GetTokenAsync(c, cancellationToken);

        var url = AbsUrl(c, _ewb.GeneratePath);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload, options: Json) };
        AddCallHeaders(req, c, token);

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("WhiteBooks EWB generate HTTP {Status}: {Body}", (int)res.StatusCode, Truncate(body));
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"WhiteBooks EWB generate failed: HTTP {(int)res.StatusCode}. {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        if (!IsSuccess(doc.RootElement, out var data, out var err))
            throw new InvalidOperationException($"WhiteBooks EWB generate rejected: {err}");

        var ewbNo = FirstString(data, "ewayBillNo", "ewbNo", "EwbNo") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ewbNo))
            throw new InvalidOperationException($"WhiteBooks EWB generate returned no EWB number: {Truncate(body)}");
        var validUntil = ParseNicDateTime(FirstString(data, "validUpto", "validUpTo", "validUntil"));
        _logger.LogInformation("WhiteBooks EWB {Ewb} generated for {Gstin}", ewbNo, c.Gstin);
        return new EWayBillProviderResult(ewbNo, validUntil);
    }

    public async Task CancelAsync(string ewbNo, string reasonCode, string remarks, CancellationToken cancellationToken = default)
    {
        var c = Resolve();
        EnsureConfigured(c);
        var token = await GetTokenAsync(c, cancellationToken);

        var url = AbsUrl(c, _ewb.CancelPath);
        var payload = new { ewbNo = long.TryParse(ewbNo, out var n) ? n : 0, cancelRsnCode = reasonCode, cancelRmrk = remarks };
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload, options: Json) };
        AddCallHeaders(req, c, token);

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"WhiteBooks EWB cancel failed: HTTP {(int)res.StatusCode}. {Truncate(body)}");
        using var doc = JsonDocument.Parse(body);
        if (!IsSuccess(doc.RootElement, out _, out var err))
            throw new InvalidOperationException($"WhiteBooks EWB cancel rejected: {err}");
        _logger.LogInformation("WhiteBooks EWB {Ewb} cancelled", ewbNo);
    }

    public async Task UpdateVehicleAsync(object payload, CancellationToken cancellationToken = default)
    {
        var c = Resolve();
        EnsureConfigured(c);
        var token = await GetTokenAsync(c, cancellationToken);

        var url = AbsUrl(c, _ewb.UpdateVehiclePath);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload, options: Json) };
        AddCallHeaders(req, c, token);

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"WhiteBooks EWB vehicle update failed: HTTP {(int)res.StatusCode}. {Truncate(body)}");
        using var doc = JsonDocument.Parse(body);
        if (!IsSuccess(doc.RootElement, out _, out var err))
            throw new InvalidOperationException($"WhiteBooks EWB vehicle update rejected: {err}");
    }

    private async Task<string> GetTokenAsync(Creds c, CancellationToken cancellationToken)
    {
        if (TokenCache.TryGetValue(c.ClientId, out var cached) && DateTime.UtcNow < cached.ExpiresUtc)
            return cached.Token;
        var (token, expiry) = await AuthenticateAsync(c, cancellationToken);
        TokenCache[c.ClientId] = (token, expiry);
        return token;
    }

    private async Task<(string Token, DateTime ExpiresUtc)> AuthenticateAsync(Creds c, CancellationToken cancellationToken)
    {
        var url = AbsUrl(c, _ewb.AuthPath);
        if (IsReal(c.Email)) url += $"?email={Uri.EscapeDataString(c.Email.Trim())}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("client_id", c.ClientId);
        req.Headers.TryAddWithoutValidation("client_secret", c.ClientSecret);
        if (IsReal(c.Gstin)) req.Headers.TryAddWithoutValidation("gstin", c.Gstin);
        if (IsReal(c.Username)) req.Headers.TryAddWithoutValidation("username", c.Username);
        if (IsReal(c.Password)) req.Headers.TryAddWithoutValidation("password", c.Password);
        req.Headers.TryAddWithoutValidation("ip_address", GetLocalIp());

        using var res = await _http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"WhiteBooks EWB auth failed: HTTP {(int)res.StatusCode}. {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        if (!IsSuccess(doc.RootElement, out var data, out var err))
            throw new InvalidOperationException($"WhiteBooks EWB auth rejected: {err}");
        var token = FirstString(data, "authtoken", "AuthToken", "auth_token", "token")
            ?? throw new InvalidOperationException($"WhiteBooks EWB auth returned no token: {Truncate(body)}");
        _logger.LogInformation("WhiteBooks EWB auth OK for {Gstin} (tokenLen {Len})", c.Gstin, token.Length);
        return (token, DateTime.UtcNow.AddHours(6).AddMinutes(-5));
    }

    private string AbsUrl(Creds c, string path) => $"{c.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private void AddCallHeaders(HttpRequestMessage req, Creds c, string token)
    {
        req.Headers.TryAddWithoutValidation("client_id", c.ClientId);
        req.Headers.TryAddWithoutValidation("client_secret", c.ClientSecret);
        req.Headers.TryAddWithoutValidation("gstin", c.Gstin);
        if (IsReal(c.Username)) req.Headers.TryAddWithoutValidation("username", c.Username);
        req.Headers.TryAddWithoutValidation("ip_address", GetLocalIp());
        // `auth-token` (HYPHEN) — the e-Invoice path proved underscore is
        // silently dropped by WhiteBooks's edge, yielding NIC "Invalid Token".
        req.Headers.TryAddWithoutValidation("auth-token", token);
    }

    private static void EnsureConfigured(Creds c)
    {
        if (!c.Configured) throw new InvalidOperationException("WhiteBooks e-Way Bill API is not configured (missing credentials).");
        if (!IsReal(c.Gstin)) throw new InvalidOperationException("Taxpayer GSTIN is not configured for e-Way Bill.");
    }

    // WhiteBooks/NIC envelope is inconsistent: success is status_cd/status "1",
    // payload in `data`, errors in `error.message` or status_desc. Treat any of
    // those shapes uniformly.
    private static bool IsSuccess(JsonElement root, out JsonElement data, out string error)
    {
        data = default;
        error = string.Empty;
        if (root.ValueKind != JsonValueKind.Object) { error = "non-object response"; return false; }

        var ok = (root.TryGetProperty("status_cd", out var sc) && sc.ValueKind == JsonValueKind.String && sc.GetString() == "1")
              || (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "1")
              || (root.TryGetProperty("status", out var sti) && sti.ValueKind == JsonValueKind.Number && sti.GetInt32() == 1);

        if (root.TryGetProperty("data", out var d)) data = d;
        else data = root;

        if (!ok)
        {
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m))
                error = m.GetString() ?? "rejected";
            else if (root.TryGetProperty("status_desc", out var sd))
                error = sd.GetString() ?? "rejected";
            else error = "rejected";
        }
        return ok;
    }

    private static string? FirstString(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!obj.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        }
        return null;
    }

    private static DateTime ParseNicDateTime(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string[] formats = { "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy hh:mm:ss tt", "dd-MM-yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy" };
            if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
        }
        return DateTime.UtcNow.AddDays(1);
    }

    private static string GetLocalIp()
    {
        if (_cachedLocalIp is not null) return _cachedLocalIp;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            var ip = (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
            _cachedLocalIp = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip;
        }
        catch { _cachedLocalIp = "127.0.0.1"; }
        return _cachedLocalIp;
    }

    private static bool IsReal(string? v) => !string.IsNullOrWhiteSpace(v) && !v.TrimStart().StartsWith('[');
    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}

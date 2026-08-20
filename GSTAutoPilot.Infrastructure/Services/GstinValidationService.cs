using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Domain.Tax;
using GSTAutoPilot.Infrastructure.Persistence;
using GSTAutoPilot.Infrastructure.Services.WhiteBooksGst;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GSTAutoPilot.Infrastructure.Services;

public class GstinValidationService : IGstinValidationService
{
    private static readonly TimeSpan CacheWindow = TimeSpan.FromHours(24);

    private readonly TenantDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IWhiteBooksGstClient _gst;
    private readonly ILogger<GstinValidationService> _logger;

    public GstinValidationService(
        TenantDbContext db,
        IHttpContextAccessor httpContextAccessor,
        IWhiteBooksGstClient gst,
        ILogger<GstinValidationService> logger)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _gst = gst;
        _logger = logger;
    }

    public async Task<GstinValidationResponse> ValidateAsync(string gstin, CancellationToken cancellationToken = default)
    {
        var normalized = (gstin ?? string.Empty).Trim().ToUpperInvariant();
        var formatCheck = GstinFormat.Validate(normalized);
        if (!formatCheck.IsValid)
        {
            return new GstinValidationResponse
            {
                GSTIN = normalized,
                FormatValid = false,
                FormatError = formatCheck.Error,
                ValidatedOn = DateTime.UtcNow,
                Source = "FORMAT_CHECK",
            };
        }

        var cached = await _db.GSTINValidations.AsNoTracking()
            .Where(v => v.GSTIN == normalized)
            .OrderByDescending(v => v.ValidatedOn)
            .FirstOrDefaultAsync(cancellationToken);
        if (cached is not null && DateTime.UtcNow - cached.ValidatedOn < CacheWindow)
        {
            var fromCache = MapToResponse(cached);
            fromCache.FormatValid = true;
            fromCache.FromCache = true;
            return fromCache;
        }

        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved.");

        // Prefer the live WhiteBooks GST API (public search). Any failure falls
        // back to the deterministic stub so validation never hard-fails.
        GSTINValidation? real = null;
        if (_gst.IsConfigured)
        {
            try
            {
                var json = await _gst.SearchTaxpayerRawAsync(normalized, cancellationToken);
                real = ParseSearch(tenant.TenantId, normalized, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WhiteBooks GST search failed for {Gstin}; using stub", normalized);
            }
        }
        var record = real ?? BuildStubRecord(tenant.TenantId, normalized);
        _db.GSTINValidations.Add(record);
        await _db.SaveChangesAsync(cancellationToken);

        var response = MapToResponse(record);
        response.FormatValid = true;
        response.FromCache = false;
        return response;
    }

    public async Task<IReadOnlyList<GstinValidationResponse>> GetHistoryAsync(string gstin, CancellationToken cancellationToken = default)
    {
        var normalized = (gstin ?? string.Empty).Trim().ToUpperInvariant();
        var rows = await _db.GSTINValidations.AsNoTracking()
            .Where(v => v.GSTIN == normalized)
            .OrderByDescending(v => v.ValidatedOn)
            .ToListAsync(cancellationToken);
        return rows.Select(r =>
        {
            var resp = MapToResponse(r);
            resp.FormatValid = true;
            return resp;
        }).ToList();
    }

    public async Task<BulkValidateResponse> BulkValidateAsync(IEnumerable<string> gstins, CancellationToken cancellationToken = default)
    {
        var results = new List<GstinValidationResponse>();
        foreach (var g in gstins ?? Array.Empty<string>())
        {
            results.Add(await ValidateAsync(g, cancellationToken));
        }
        return new BulkValidateResponse
        {
            Total = results.Count,
            Valid = results.Count(r => r.FormatValid),
            Invalid = results.Count(r => !r.FormatValid),
            Results = results,
        };
    }

    // Map the WhiteBooks /public/search response (real GSTN data) into a record.
    private static GSTINValidation? ParseSearch(Guid tenantId, string gstin, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!(root.TryGetProperty("status_cd", out var sc) && sc.GetString() == "1")) return null;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

        string? Str(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var stsRaw = Str("sts") ?? "Active";
        var status = stsRaw.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? GSTINStatus.Cancelled
            : stsRaw.Contains("susp", StringComparison.OrdinalIgnoreCase) ? GSTINStatus.Suspended
            : GSTINStatus.Active;

        string? stateCd = null;
        if (data.TryGetProperty("pradr", out var pradr) && pradr.ValueKind == JsonValueKind.Object
            && pradr.TryGetProperty("addr", out var addr) && addr.ValueKind == JsonValueKind.Object
            && addr.TryGetProperty("stcd", out var st) && st.ValueKind == JsonValueKind.String)
            stateCd = st.GetString();

        DateTime? regDate = null;
        var rgdt = Str("rgdt");
        if (!string.IsNullOrWhiteSpace(rgdt)
            && DateTime.TryParseExact(rgdt, "dd/MM/yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var rd))
            regDate = rd;

        return new GSTINValidation
        {
            TenantId = tenantId,
            GSTIN = gstin,
            LegalName = Str("lgnm") ?? string.Empty,
            TradeName = Str("tradeNam") ?? string.Empty,
            State = stateCd ?? GstinFormat.GetStateName(gstin[..2]) ?? "Unknown",
            StateCode = gstin[..2],
            TaxpayerType = Str("dty") ?? "Regular",
            RegistrationDate = regDate,
            Status = status,
            FilingFrequency = string.Empty,   // not returned by search taxpayer
            LastFiledReturn = string.Empty,
            ComplianceScore = 0,              // not provided by GSTN search
            ValidatedOn = DateTime.UtcNow,
            Source = "WHITEBOOKS_GST",
        };
    }

    private static GSTINValidation BuildStubRecord(Guid tenantId, string normalizedGstin)
    {
        var stateCode = normalizedGstin[..2];
        var stateName = GstinFormat.GetStateName(stateCode) ?? "Unknown";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedGstin));
        var seed = (int)(BitConverter.ToUInt32(hash, 0) & 0x7FFFFFFF);
        var rng = new Random(seed);

        var pan = normalizedGstin.Substring(2, 10);
        var entity = normalizedGstin[12];
        var taxpayerType = entity switch
        {
            >= '1' and <= '9' => "Regular",
            'A' => "Composition",
            'B' => "Casual",
            _ => "Regular",
        };

        var status = (rng.Next(100) switch
        {
            < 5 => GSTINStatus.Cancelled,
            < 10 => GSTINStatus.Suspended,
            _ => GSTINStatus.Active,
        });

        var registrationYear = 2017 + rng.Next(0, 9);
        var registrationDate = new DateTime(registrationYear, rng.Next(1, 13), rng.Next(1, 28), 0, 0, 0, DateTimeKind.Utc);

        var lastReturnMonth = DateTime.UtcNow.AddMonths(-1);
        var lastReturn = $"GSTR-3B {lastReturnMonth:yyyyMM}";

        var complianceScore = status == GSTINStatus.Active
            ? 70 + rng.Next(0, 31)
            : 30 + rng.Next(0, 30);

        return new GSTINValidation
        {
            TenantId = tenantId,
            GSTIN = normalizedGstin,
            TradeName = $"{pan[..5]} Enterprises",
            LegalName = $"{pan[..5]} Enterprises Pvt Ltd",
            State = stateName,
            StateCode = stateCode,
            TaxpayerType = taxpayerType,
            RegistrationDate = registrationDate,
            Status = status,
            FilingFrequency = rng.Next(2) == 0 ? "Monthly" : "Quarterly",
            LastFiledReturn = lastReturn,
            ComplianceScore = complianceScore,
            ValidatedOn = DateTime.UtcNow,
            Source = "STUB",
        };
    }

    private static GstinValidationResponse MapToResponse(GSTINValidation r) => new()
    {
        ValidationId = r.ValidationId,
        GSTIN = r.GSTIN,
        FormatValid = true,
        TradeName = r.TradeName,
        LegalName = r.LegalName,
        State = r.State,
        StateCode = r.StateCode,
        TaxpayerType = r.TaxpayerType,
        RegistrationDate = r.RegistrationDate,
        Status = r.Status,
        FilingFrequency = r.FilingFrequency,
        LastFiledReturn = r.LastFiledReturn,
        ComplianceScore = r.ComplianceScore,
        ValidatedOn = r.ValidatedOn,
        Source = r.Source,
    };
}

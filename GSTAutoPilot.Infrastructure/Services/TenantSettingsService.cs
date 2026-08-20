using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using GSTAutoPilot.Infrastructure.Services.WhiteBooks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GSTAutoPilot.Infrastructure.Services;

public class TenantSettingsService : ITenantSettingsService
{
    private readonly MasterDbContext _master;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISecretProtector _protector;
    private readonly WhiteBooksOptions _wbOptions;

    public TenantSettingsService(
        MasterDbContext master,
        IHttpContextAccessor httpContextAccessor,
        ISecretProtector protector,
        IOptions<WhiteBooksOptions> wbOptions)
    {
        _master = master;
        _httpContextAccessor = httpContextAccessor;
        _protector = protector;
        _wbOptions = wbOptions.Value;
    }

    // X-Company-Id header (set by TenantMiddleware) — the active GST group
    // representative. null = "tenant default" scope. WhiteBooks + GST-API
    // creds resolve against this; SMTP and cosmetic settings always use the
    // tenant-default row regardless.
    private byte? ActiveCompanyId()
        => _httpContextAccessor.HttpContext?.Items["CompanyId"] is byte b ? b : null;

    // Per-company-aware read: try the override row for the active CoId
    // first, then fall back to the tenant-default row. Returns null when
    // neither exists. Used for WhiteBooks + GST-API creds.
    private async Task<TenantSettings?> ReadCredsRowAsync(Guid tenantId, CancellationToken ct)
    {
        var coId = ActiveCompanyId();
        if (coId.HasValue)
        {
            var perCompany = await _master.TenantSettings.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.CompanyId == coId, ct);
            if (perCompany is not null) return perCompany;
        }
        return await _master.TenantSettings.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.CompanyId == null, ct);
    }

    // Per-company-aware write: get-or-create the row exactly matching the
    // active scope. Saves go HERE so each GST registration owns its own
    // creds independently.
    private async Task<TenantSettings> GetOrCreateCredsRowAsync(Guid tenantId, CancellationToken ct)
    {
        var coId = ActiveCompanyId();
        var row = await _master.TenantSettings
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.CompanyId == coId, ct);
        if (row is null)
        {
            row = new TenantSettings { TenantId = tenantId, CompanyId = coId, CreatedOn = DateTime.UtcNow };
            _master.TenantSettings.Add(row);
        }
        return row;
    }

    // Tenant-default helpers (CompanyId IS NULL) — for SMTP + cosmetic
    // invoice settings, which are NOT per-GST.
    private Task<TenantSettings?> ReadTenantDefaultAsync(Guid tenantId, CancellationToken ct)
        => _master.TenantSettings.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.CompanyId == null, ct);

    private async Task<TenantSettings> GetOrCreateTenantDefaultAsync(Guid tenantId, CancellationToken ct)
    {
        var row = await _master.TenantSettings
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.CompanyId == null, ct);
        if (row is null)
        {
            row = new TenantSettings { TenantId = tenantId, CompanyId = null, CreatedOn = DateTime.UtcNow };
            _master.TenantSettings.Add(row);
        }
        return row;
    }

    public async Task<TenantSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var row = await ReadTenantDefaultAsync(tenant.TenantId, cancellationToken);
        return row is null
            ? new TenantSettingsDto()
            : Map(row);
    }

    public async Task<TenantSettingsDto> UpdateAsync(TenantSettingsDto dto, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var row = await GetOrCreateTenantDefaultAsync(tenant.TenantId, cancellationToken);
        row.ShowBankDetails = dto.ShowBankDetails;
        row.ShowSignature = dto.ShowSignature;
        row.LogoPath = dto.LogoPath;
        row.InvoiceFooterText = dto.InvoiceFooterText;
        row.TermsAndConditions = dto.TermsAndConditions;
        row.UpdatedOn = DateTime.UtcNow;
        await _master.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<ErpProfileDto> GetErpProfileAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var row = await _master.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenant.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant not found.");
        return new ErpProfileDto
        {
            SalesHeaderTable = string.IsNullOrWhiteSpace(row.SalesHeaderTable) ? "Bill_File_mas" : row.SalesHeaderTable,
            SalesDocId = row.SalesDocId,
            SalesLineTable = string.IsNullOrWhiteSpace(row.SalesLineTable) ? "Bill_File_trn" : row.SalesLineTable,
        };
    }

    public async Task<ErpProfileDto> UpdateErpProfileAsync(ErpProfileDto dto, CancellationToken cancellationToken = default)
    {
        var header = ValidateTableName(dto.SalesHeaderTable, nameof(dto.SalesHeaderTable));
        var line = ValidateTableName(dto.SalesLineTable, nameof(dto.SalesLineTable));

        var tenant = RequireTenant();
        var row = await _master.Tenants
            .FirstOrDefaultAsync(t => t.TenantId == tenant.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant not found.");
        row.SalesHeaderTable = header;
        row.SalesDocId = dto.SalesDocId;
        row.SalesLineTable = line;
        await _master.SaveChangesAsync(cancellationToken);
        return new ErpProfileDto
        {
            SalesHeaderTable = row.SalesHeaderTable,
            SalesDocId = row.SalesDocId,
            SalesLineTable = row.SalesLineTable,
        };
    }

    public async Task<SpProfileDto> GetSpProfileAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var row = await _master.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenant.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant not found.");
        return new SpProfileDto
        {
            OutwardSP = row.OutwardSP,
            InwardSP = row.InwardSP,
        };
    }

    public async Task<SpProfileDto> UpdateSpProfileAsync(SpProfileDto dto, CancellationToken cancellationToken = default)
    {
        // Blank clears the SP (falls back to table mapping); otherwise it must be
        // a safe [schema.]name — it's EXEC'd as a stored procedure by name.
        var outward = ValidateSpNameOrEmpty(dto.OutwardSP, nameof(dto.OutwardSP));
        var inward = ValidateSpNameOrEmpty(dto.InwardSP, nameof(dto.InwardSP));

        var tenant = RequireTenant();
        var row = await _master.Tenants
            .FirstOrDefaultAsync(t => t.TenantId == tenant.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant not found.");
        row.OutwardSP = outward;
        row.InwardSP = inward;
        await _master.SaveChangesAsync(cancellationToken);
        return new SpProfileDto { OutwardSP = row.OutwardSP, InwardSP = row.InwardSP };
    }

    // SP name is EXEC'd by name, so restrict it to a safe [schema.]identifier.
    // Empty/blank is allowed and stored as null (no SP -> table-mapping fallback).
    // Mirrors SpOutwardService.ValidateSpName.
    private static string? ValidateSpNameOrEmpty(string? name, string field)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$"))
            throw new ArgumentException($"{field} must be a valid stored procedure name (letters, digits, underscore; optional schema.name).", field);
        return trimmed;
    }

    // Mirrors the whitelist in CarolERPDbContext.ValidatedSalesTable — the name
    // is interpolated into raw SQL, so only bare SQL identifiers are allowed.
    private static string ValidateTableName(string? name, string field)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException($"{field} must be a valid table name (letters, digits, underscore).", field);
        return trimmed;
    }

    public async Task<WhiteBooksStatusDto> GetWhiteBooksAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var row = await ReadCredsRowAsync(tenant.TenantId, cancellationToken);
        return BuildWhiteBooksStatus(row, tenant.GSTIN);
    }

    public async Task<WhiteBooksStatusDto> SaveWhiteBooksAsync(WhiteBooksConfigCommand cmd, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.ClientId) || string.IsNullOrWhiteSpace(cmd.ClientSecret))
            throw new ArgumentException("Client ID and Client Secret are required.");
        if (string.IsNullOrWhiteSpace(cmd.Username))
            throw new ArgumentException("e-Invoice API username is required.");

        var tenant = RequireTenant();
        var row = await GetOrCreateCredsRowAsync(tenant.TenantId, cancellationToken);
        row.WhiteBooksClientId = cmd.ClientId.Trim();
        row.WhiteBooksClientSecret = cmd.ClientSecret.Trim();
        row.WhiteBooksUsername = cmd.Username.Trim();
        // Password is write-only: keep the stored one when the field is left blank on edit.
        if (!string.IsNullOrWhiteSpace(cmd.Password))
            row.WhiteBooksPassword = cmd.Password.Trim();
        row.WhiteBooksUseSandbox = cmd.UseSandbox;
        row.WhiteBooksEnabled = true;
        row.UpdatedOn = DateTime.UtcNow;
        // GSTIN is per-tenant identity (lives on Tenants master row) — update
        // it here when the user edits it from the e-Invoice card so a single
        // value drives both API products.
        var effectiveGstin = await UpdateTenantGstinAsync(tenant, cmd.Gstin, cancellationToken);
        await _master.SaveChangesAsync(cancellationToken);
        return BuildWhiteBooksStatus(row, effectiveGstin);
    }

    public async Task DisableWhiteBooksAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        // Disable operates on the active scope only. If user is on Group 2
        // and disables, Group 1's row stays enabled.
        var coId = ActiveCompanyId();
        var row = await _master.TenantSettings.FirstOrDefaultAsync(t => t.TenantId == tenant.TenantId && t.CompanyId == coId, cancellationToken);
        if (row is null) return;
        row.WhiteBooksEnabled = false;
        row.UpdatedOn = DateTime.UtcNow;
        await _master.SaveChangesAsync(cancellationToken);
    }

    public async Task<WhiteBooksStatusDto> SetWhiteBooksEnvironmentAsync(bool useSandbox, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var row = await GetOrCreateCredsRowAsync(tenant.TenantId, cancellationToken);
        row.WhiteBooksUseSandbox = useSandbox;
        // Enable the integration when switching to sandbox even with no prod
        // creds yet — sandbox always has working defaults.
        if (useSandbox) row.WhiteBooksEnabled = true;
        row.UpdatedOn = DateTime.UtcNow;
        await _master.SaveChangesAsync(cancellationToken);
        return BuildWhiteBooksStatus(row, tenant.GSTIN);
    }

    public WhiteBooksSandboxInfoDto GetWhiteBooksSandboxInfo()
    {
        var s = _wbOptions.Sandbox;
        return new WhiteBooksSandboxInfoDto
        {
            Username = s.Username,
            ClientId = MaskClientId(s.ClientId) ?? string.Empty,
            Gstin = s.Gstin,
            Email = s.Email,
            IsConfigured = !string.IsNullOrWhiteSpace(s.ClientId)
                && !string.IsNullOrWhiteSpace(s.ClientSecret)
                && !string.IsNullOrWhiteSpace(s.Username),
        };
    }

    public async Task<SmtpStatusDto> GetSmtpAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var row = await ReadTenantDefaultAsync(tenant.TenantId, cancellationToken);
        return BuildSmtpStatus(row);
    }

    public async Task<SmtpStatusDto> SaveSmtpAsync(SmtpConfigCommand cmd, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Host)) throw new ArgumentException("SMTP host is required.");
        if (string.IsNullOrWhiteSpace(cmd.FromEmail)) throw new ArgumentException("From email is required.");
        if (cmd.Port is <= 0 or > 65535) throw new ArgumentException("SMTP port must be between 1 and 65535.");

        var tenant = RequireTenant();
        var row = await GetOrCreateTenantDefaultAsync(tenant.TenantId, cancellationToken);
        row.SmtpHost = cmd.Host.Trim();
        row.SmtpPort = cmd.Port;
        row.SmtpUsername = cmd.Username?.Trim();
        row.SmtpFromName = cmd.FromName?.Trim();
        row.SmtpFromEmail = cmd.FromEmail.Trim();
        row.SmtpEnableSsl = cmd.EnableSsl;
        // Password is write-only and encrypted at rest; blank keeps the stored one.
        if (!string.IsNullOrWhiteSpace(cmd.Password))
            row.SmtpPassword = _protector.Protect(cmd.Password.Trim());
        row.UpdatedOn = DateTime.UtcNow;
        await _master.SaveChangesAsync(cancellationToken);
        return BuildSmtpStatus(row);
    }

    public async Task<SmtpConfig> ResolveSmtpAsync(SmtpConfigCommand cmd, CancellationToken cancellationToken = default)
    {
        var password = cmd.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            var tenant = RequireTenant();
            var row = await ReadTenantDefaultAsync(tenant.TenantId, cancellationToken);
            password = _protector.TryUnprotect(row?.SmtpPassword, out var p) ? p : string.Empty;
        }
        return new SmtpConfig(cmd.Host.Trim(), cmd.Port, (cmd.Username ?? string.Empty).Trim(),
            password ?? string.Empty, (cmd.FromName ?? string.Empty).Trim(), cmd.FromEmail.Trim(), cmd.EnableSsl);
    }

    public async Task<SmtpConfig> GetSmtpConfigAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var row = await ReadTenantDefaultAsync(tenant.TenantId, cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.SmtpHost) || string.IsNullOrWhiteSpace(row.SmtpFromEmail))
            throw new InvalidOperationException("Email (SMTP) is not configured. Set it in Settings → Email Configuration.");
        var password = _protector.TryUnprotect(row.SmtpPassword, out var p) ? p : string.Empty;
        return new SmtpConfig(row.SmtpHost!, row.SmtpPort, row.SmtpUsername ?? string.Empty, password,
            row.SmtpFromName ?? string.Empty, row.SmtpFromEmail!, row.SmtpEnableSsl);
    }

    public async Task<WhiteBooksGstStatusDto> GetGstApiAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var row = await ReadCredsRowAsync(tenant.TenantId, cancellationToken);
        return BuildGstStatus(row, tenant.GSTIN);
    }

    public async Task<WhiteBooksGstStatusDto> SaveGstApiAsync(WhiteBooksGstConfigCommand cmd, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.ClientId) || string.IsNullOrWhiteSpace(cmd.ClientSecret))
            throw new ArgumentException("GST API Client ID and Client Secret are required.");

        var tenant = RequireTenant();
        var row = await GetOrCreateCredsRowAsync(tenant.TenantId, cancellationToken);
        row.WhiteBooksGstClientId = cmd.ClientId.Trim();
        row.WhiteBooksGstClientSecret = _protector.Protect(cmd.ClientSecret.Trim());
        if (!string.IsNullOrWhiteSpace(cmd.Username))
            row.WhiteBooksGstUsername = cmd.Username.Trim();
        // Password is write-only: blank on edit keeps the stored one.
        if (!string.IsNullOrWhiteSpace(cmd.Password))
            row.WhiteBooksGstPassword = _protector.Protect(cmd.Password.Trim());
        row.WhiteBooksGstEnabled = true;
        row.UpdatedOn = DateTime.UtcNow;
        var effectiveGstin = await UpdateTenantGstinAsync(tenant, cmd.Gstin, cancellationToken);
        await _master.SaveChangesAsync(cancellationToken);
        return BuildGstStatus(row, effectiveGstin);
    }

    public async Task DisableGstApiAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var coId = ActiveCompanyId();
        var row = await _master.TenantSettings.FirstOrDefaultAsync(t => t.TenantId == tenant.TenantId && t.CompanyId == coId, cancellationToken);
        if (row is null) return;
        row.WhiteBooksGstEnabled = false;
        row.UpdatedOn = DateTime.UtcNow;
        await _master.SaveChangesAsync(cancellationToken);
    }

    private static WhiteBooksGstStatusDto BuildGstStatus(TenantSettings? row, string? gstin) => new()
    {
        Enabled = row?.WhiteBooksGstEnabled ?? false,
        HasCredentials = !string.IsNullOrWhiteSpace(row?.WhiteBooksGstClientId) && !string.IsNullOrWhiteSpace(row?.WhiteBooksGstClientSecret),
        ClientId = MaskClientId(row?.WhiteBooksGstClientId),
        Username = string.IsNullOrWhiteSpace(row?.WhiteBooksGstUsername) ? null : row!.WhiteBooksGstUsername,
        HasPassword = !string.IsNullOrWhiteSpace(row?.WhiteBooksGstPassword),
        Gstin = string.IsNullOrWhiteSpace(gstin) ? null : gstin,
    };

    private static SmtpStatusDto BuildSmtpStatus(TenantSettings? row) => new()
    {
        Host = row?.SmtpHost,
        Port = row?.SmtpPort ?? 587,
        Username = row?.SmtpUsername,
        FromName = row?.SmtpFromName,
        FromEmail = row?.SmtpFromEmail,
        EnableSsl = row?.SmtpEnableSsl ?? true,
        HasPassword = !string.IsNullOrWhiteSpace(row?.SmtpPassword),
        IsConfigured = !string.IsNullOrWhiteSpace(row?.SmtpHost) && !string.IsNullOrWhiteSpace(row?.SmtpFromEmail),
    };

    private static WhiteBooksStatusDto BuildWhiteBooksStatus(TenantSettings? row, string? gstin)
    {
        var hasCreds = !string.IsNullOrWhiteSpace(row?.WhiteBooksClientId) && !string.IsNullOrWhiteSpace(row?.WhiteBooksClientSecret);
        var sandbox = row?.WhiteBooksUseSandbox ?? true;
        return new WhiteBooksStatusDto
        {
            Enabled = row?.WhiteBooksEnabled ?? false,
            UseSandbox = sandbox,
            HasCredentials = hasCreds,
            Environment = sandbox ? "Sandbox" : "Production",
            ClientId = MaskClientId(row?.WhiteBooksClientId),
            Username = string.IsNullOrWhiteSpace(row?.WhiteBooksUsername) ? null : row!.WhiteBooksUsername,
            HasPassword = !string.IsNullOrWhiteSpace(row?.WhiteBooksPassword),
            Gstin = string.IsNullOrWhiteSpace(gstin) ? null : gstin,
        };
    }

    // Updates the Tenants master row's GSTIN when the user edits it from either
    // the e-Invoice or GST API card. No-op when blank (treat blank as "leave it").
    // Returns the effective GSTIN to use in the response DTO.
    private async Task<string?> UpdateTenantGstinAsync(Tenant currentTenant, string? gstin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gstin)) return currentTenant.GSTIN;
        var trimmed = gstin.Trim().ToUpperInvariant();
        if (trimmed.Length != 15)
            throw new ArgumentException($"GSTIN must be 15 characters (got {trimmed.Length}).");
        if (string.Equals(currentTenant.GSTIN, trimmed, StringComparison.OrdinalIgnoreCase))
            return currentTenant.GSTIN;
        var tenantRow = await _master.Tenants.FirstOrDefaultAsync(t => t.TenantId == currentTenant.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant row not found.");
        tenantRow.GSTIN = trimmed;
        // The HttpContext cache reflects the OLD GSTIN — patch it so the rest
        // of this request sees the new value without a round-trip.
        currentTenant.GSTIN = trimmed;
        return trimmed;
    }

    private static string? MaskClientId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return id.Length <= 4 ? "••••" : $"{id[..2]}••••{id[^2..]}";
    }

    internal async Task<TenantSettings?> GetEntityAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return await _master.TenantSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken);
    }

    private Tenant RequireTenant()
        => _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved; X-Tenant-Id header is required.");

    private static TenantSettingsDto Map(TenantSettings t) => new()
    {
        ShowBankDetails = t.ShowBankDetails,
        ShowSignature = t.ShowSignature,
        LogoPath = t.LogoPath,
        InvoiceFooterText = t.InvoiceFooterText,
        TermsAndConditions = t.TermsAndConditions,
    };
}

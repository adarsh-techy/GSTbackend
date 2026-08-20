namespace GSTAutoPilot.Domain.Entities;

public class TenantSettings
{
    public int SettingId { get; set; }
    public Guid TenantId { get; set; }
    // Optional scoping to a specific company (GST group rep CoId) inside the
    // tenant. NULL = "tenant default" — used as fallback when there's no
    // per-company row, and as the only row for single-GST tenants.
    //
    // Multi-GST tenants (e.g. KSCC main coir + KSCC mattress) need separate
    // WhiteBooks / GST-API portal credentials because each GST registration
    // is filed at its own portal account. Per-company rows hold those
    // overrides; cosmetic + SMTP settings continue to be read from the
    // tenant-default row regardless of which company is active.
    public byte? CompanyId { get; set; }
    public bool ShowBankDetails { get; set; } = true;
    public bool ShowSignature { get; set; } = true;
    public string? LogoPath { get; set; }
    public string? InvoiceFooterText { get; set; }
    public string? TermsAndConditions { get; set; }

    // Per-tenant WhiteBooks GSP e-Invoice credentials (entered via Settings →
    // API Config). When enabled + populated these override appsettings.
    public string? WhiteBooksClientId { get; set; }
    public string? WhiteBooksClientSecret { get; set; }
    // Taxpayer e-Invoice (NIC) API user for this GSTIN. When populated these
    // override the appsettings/user-secrets WhiteBooksEInvoice fallback.
    public string? WhiteBooksUsername { get; set; }
    public string? WhiteBooksPassword { get; set; }
    public bool WhiteBooksUseSandbox { get; set; } = true;
    public bool WhiteBooksEnabled { get; set; }

    // WhiteBooks GST API (returns / GSTR-2B / GSTIN search) — a SEPARATE
    // product from the e-Invoice API above, with its own GSP credentials.
    // Secret is stored encrypted at rest (ASP.NET Data Protection).
    public string? WhiteBooksGstClientId { get; set; }
    public string? WhiteBooksGstClientSecret { get; set; }
    public bool WhiteBooksGstEnabled { get; set; }
    // Taxpayer GST-portal API user for the RETURNS API — usually DIFFERENT
    // from the e-Invoice user (e.g. Flooratex: GST=FLOORATEX2020, e-Invoice=
    // API_FLOORATEX2026). Used to drive the OTP session for GSTR-2B fetch.
    public string? WhiteBooksGstUsername { get; set; }
    public string? WhiteBooksGstPassword { get; set; }

    // SMTP / email config for sending signed e-Invoice JSON to buyers.
    // SmtpPassword is stored encrypted at rest (ASP.NET Data Protection).
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string? SmtpFromName { get; set; }
    public string? SmtpFromEmail { get; set; }
    public bool SmtpEnableSsl { get; set; } = true;

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedOn { get; set; } = DateTime.UtcNow;
}

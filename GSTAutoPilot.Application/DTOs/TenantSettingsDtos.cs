namespace GSTAutoPilot.Application.DTOs;

public class TenantSettingsDto
{
    public bool ShowBankDetails { get; set; } = true;
    public bool ShowSignature { get; set; } = true;
    public string? LogoPath { get; set; }
    public string? InvoiceFooterText { get; set; }
    public string? TermsAndConditions { get; set; }
}

// Per-tenant CarolERP schema mapping (which tables hold this customer's sales
// + the DocId that flags a sales document). Lives on the Tenant row.
public class ErpProfileDto
{
    public string SalesHeaderTable { get; set; } = "Bill_File_mas";
    public int? SalesDocId { get; set; } = 205;
    public string SalesLineTable { get; set; } = "Bill_File_trn";
}

// Per-tenant stored-procedure data source. When a direction's SP name is set,
// the app runs it (EXEC <sp> @GstNo,@StartDate,@EndDate) instead of the table-
// mapping engine for that direction; blank means fall back to table mapping.
// Both live on the Tenant row (OutwardSP / InwardSP).
public class SpProfileDto
{
    public string? OutwardSP { get; set; }
    public string? InwardSP { get; set; }
}

// Save payload for the WhiteBooks API Config tab.
public class WhiteBooksConfigCommand
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    // Taxpayer e-Invoice (NIC) API user for the GSTIN. Password may be left
    // blank on edit to keep the stored one.
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseSandbox { get; set; } = true;
    // Taxpayer GSTIN — when supplied, also updates the Tenants master row so
    // every API call (e-Invoice + GST returns) uses the same identity.
    public string? Gstin { get; set; }
}

// WhiteBooks GST API (returns / GSTR-2B / GSTIN) config — separate from e-Invoice.
public class WhiteBooksGstConfigCommand
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    // GST portal taxpayer user/password — DIFFERENT from the e-Invoice user.
    // Password is write-only: blank on edit keeps the stored value.
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Gstin { get; set; }
}

public class WhiteBooksGstStatusDto
{
    public bool Enabled { get; set; }
    public bool HasCredentials { get; set; }
    public string? ClientId { get; set; }  // masked
    public string BaseUrl { get; set; } = "https://api.whitebooks.in";
    public string? Username { get; set; }  // identifier, not secret
    public bool HasPassword { get; set; }
    public string? Gstin { get; set; }     // from Tenants master row
}

// Status returned to the UI — never includes the secret or password.
public class WhiteBooksStatusDto
{
    public bool Enabled { get; set; }
    public bool UseSandbox { get; set; }
    public bool HasCredentials { get; set; }
    public string Environment { get; set; } = "Sandbox";
    public string? ClientId { get; set; }  // masked
    public string? Username { get; set; }  // identifier, not secret
    public bool HasPassword { get; set; }
    public string? Gstin { get; set; }     // from Tenants master row
}

// Read-only view of the shared sandbox account (BVMGSP / EINS...). Safe to
// expose — these are WhiteBooks's published test credentials, not secrets.
public class WhiteBooksSandboxInfoDto
{
    public string Username { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;  // masked
    public string Gstin { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsConfigured { get; set; }
}

// Lightweight environment toggle (Use Sandbox vs Production) without touching
// production credentials.
public class WhiteBooksEnvironmentCommand
{
    public bool UseSandbox { get; set; } = true;
}

namespace GSTAutoPilot.Application.DTOs;

// Light-weight item for the header tenant selector. Master DB has more on
// Tenant, but the UI only needs the bare minimum to populate a dropdown.
public class TenantSummaryDto
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string Flavor { get; set; } = "Default";
    public bool IsActive { get; set; } = true;
}

// Onboarding wizard: create a new tenant (client). All identity/connection
// values are supplied here — nothing about the tenant is hardcoded in the app.
public class CreateTenantRequest
{
    public string Name { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string AppDbConnection { get; set; } = string.Empty;     // tenant's app DB (TenantDbContext)
    public string CarolErpConnection { get; set; } = string.Empty;  // tenant's CarolERP DB
    public string CarolErpFlavor { get; set; } = "Default";         // "Default" | "KSCC"

    // Primary data source: per-tenant stored procedures (SP owns all GST logic).
    // Set these for an SP-based client (e.g. KSCC). Blank => legacy table mapping
    // below is used instead. See SpProfileDto.
    public string? OutwardSP { get; set; }
    public string? InwardSP { get; set; }

    // Legacy table-mapping source — only used when the matching SP is blank.
    public string SalesHeaderTable { get; set; } = "Bill_File_mas";
    public int? SalesDocId { get; set; }
    public string SalesLineTable { get; set; } = "Bill_File_trn";
}

public class CreateTenantResponse
{
    public Guid TenantId { get; set; }
}

public class TestConnectionRequest
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Kind { get; set; } = "app"; // "app" (tenant DB) | "carolerp"
}

public class TestConnectionResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
}

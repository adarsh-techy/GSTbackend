namespace GSTAutoPilot.Domain.Entities;

public class Tenant
{
    public Guid TenantId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string GSTIN { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public string? CarolERPConnection { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    // Optional per-tenant stored procedures that replace the table-mapping
    // engine as the data source. Each runs EXEC <sp> @Gstin, @FromDate, @ToDate
    // and returns line-level GST rows (see the SP contract). When a direction's
    // SP name is set it is used exclusively; when it's null/empty the app
    // returns a "SP not configured" warning for that direction (no table
    // fallback). Resolved per direction independently.
    public string? OutwardSP { get; set; }
    public string? InwardSP { get; set; }

    // Per-tenant CarolERP schema profile. Different CarolERP installations put
    // sales in different tables under different DocIds (e.g. KSCC =
    // Bill_File_mas/205, Flooratex = Bill_Mas/51). Defaults preserve the
    // original KSCC behaviour.
    public string SalesHeaderTable { get; set; } = "Bill_File_mas";
    public int? SalesDocId { get; set; } = 205;
    public string SalesLineTable { get; set; } = "Bill_File_trn";

    // CarolERP schema-flavor for this tenant. CarolERP installs drift on a
    // few column names (notably company.GstNo vs GSTNumber, Documents.Sanction
    // vs SanctionRequired). The flavor tells reads which physical column to
    // target. "Default" matches Flooratex; "KSCC" matches the Coir Corp style.
    public string CarolErpFlavor { get; set; } = "Default";
}

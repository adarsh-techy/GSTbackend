namespace GSTAutoPilot.Domain.Entities;

// Customs Bill of Entry for imported goods. CarolERP doesn't record the IGST
// paid at customs (the import purchase bills carry only the goods value), so
// this is captured manually in GSTAutoPilot — mirroring how the GST portal
// pulls Bill-of-Entry data from ICEGATE into GSTR-3B Table 4(A)(1) "Import of
// goods", with manual entry as the fallback. Lives in the tenant DB.
public class BillOfEntry
{
    public int BoEId { get; set; }
    public Guid TenantId { get; set; }

    // Filing period this BoE's ITC is claimed in (YYYYMM).
    public string Period { get; set; } = string.Empty;

    // Bill of Entry number + date from the customs document.
    public string BoENumber { get; set; } = string.Empty;
    public DateTime BoEDate { get; set; }

    // ICEGATE port code (e.g. "INNSA1") and overseas supplier name.
    public string? PortCode { get; set; }
    public string? SupplierName { get; set; }
    // Usually blank for foreign suppliers; kept for SEZ / bonded cases.
    public string? SupplierGSTIN { get; set; }

    // Assessable value at customs and the IGST + Compensation Cess paid.
    public decimal AssessableValue { get; set; }
    public decimal IGSTAmount { get; set; }
    public decimal CessAmount { get; set; }

    public string? Remarks { get; set; }

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedOn { get; set; } = DateTime.UtcNow;
}

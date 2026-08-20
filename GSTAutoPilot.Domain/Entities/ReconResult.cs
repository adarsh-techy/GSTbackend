namespace GSTAutoPilot.Domain.Entities;

public class ReconResult
{
    public Guid ReconId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string SupplierGSTIN { get; set; } = string.Empty;
    public string? SupplierName { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public decimal GSTR2BAmount { get; set; }
    public decimal BooksAmount { get; set; }
    public decimal Difference { get; set; }
    public string Status { get; set; } = string.Empty;
    public string AIRemarks { get; set; } = string.Empty;
    public string FilingPeriod { get; set; } = string.Empty;
    // Recon section: "B2B" (supplier invoices), "CDNR" (supplier credit/debit
    // notes) or "IMPG" (import of goods).
    public string Section { get; set; } = ReconSectionType.B2B;
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}

public static class ReconSectionType
{
    public const string B2B = "B2B";
    public const string CDNR = "CDNR";
    public const string IMPG = "IMPG";
}

public static class ReconStatus
{
    public const string Matched = "Matched";
    public const string Mismatch = "Mismatch";
    public const string Missing = "Missing";
    public const string NotIn2B = "NotIn2B";
}

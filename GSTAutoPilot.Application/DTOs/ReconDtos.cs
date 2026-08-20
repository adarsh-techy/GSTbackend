namespace GSTAutoPilot.Application.DTOs;

public class ReconRunResponse
{
    public string FilingPeriod { get; set; } = string.Empty;
    public int RowsProcessed { get; set; }
    public ReconSummary Summary { get; set; } = new();
    public DateTime RanOn { get; set; }
}

public class ReconSummary
{
    public int Matched { get; set; }
    public int Mismatch { get; set; }
    public int Missing { get; set; }
    public int NotIn2B { get; set; }
    public int Total => Matched + Mismatch + Missing + NotIn2B;
}

public class ReconReportResponse
{
    public string FilingPeriod { get; set; } = string.Empty;
    public ReconSummary Summary { get; set; } = new();
    public List<ReconRowResponse> Rows { get; set; } = new();
}

public class ReconRowResponse
{
    public Guid ReconId { get; set; }
    public string SupplierGSTIN { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string InvoiceNo { get; set; } = string.Empty;
    public decimal GSTR2BAmount { get; set; }
    public decimal BooksAmount { get; set; }
    public decimal Difference { get; set; }
    public string Status { get; set; } = string.Empty;
    // "B2B" (supplier invoices) or "IMPG" (import of goods).
    public string Section { get; set; } = "B2B";
    public string AIRemarks { get; set; } = string.Empty;
    public DateTime CreatedOn { get; set; }
}

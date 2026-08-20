namespace GSTAutoPilot.Application.DTOs;

public class Gstr2bFetchResponse
{
    public string FilingPeriod { get; set; } = string.Empty;
    public int RecordsFetched { get; set; }
    public DateTime FetchedOn { get; set; }
    public string Source { get; set; } = "STORED";
    public List<Gstr2bRecordResponse> Records { get; set; } = new();
}

public class Gstr2bRecordResponse
{
    public Guid GSTR2BId { get; set; }
    public string SupplierGSTIN { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string InvoiceNo { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal IGSTAmount { get; set; }
    public decimal CGSTAmount { get; set; }
    public decimal SGSTAmount { get; set; }
    public string FilingPeriod { get; set; } = string.Empty;
    // "B2B" (supplier invoices) or "IMPG" (import of goods).
    public string RecordType { get; set; } = "B2B";
}

namespace GSTAutoPilot.Application.DTOs;

public class ImsInvoiceDto
{
    public string Id { get; set; } = string.Empty;
    public string SupplierGstin { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string InvoiceNo { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public string RecordType { get; set; } = "B2B"; // B2B, B2BA, CDNR, CDNRA, DE, SEZWP, SEZWOP
    public decimal TaxableAmount { get; set; }
    public decimal IgstAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal CessAmount { get; set; }
    public decimal TotalTaxAmount => IgstAmount + CgstAmount + SgstAmount + CessAmount;
    public decimal TotalAmount => TaxableAmount + TotalTaxAmount;

    // Status on GST portal: "No Action", "Accepted", "Rejected", "Pending"
    public string PortalStatus { get; set; } = "No Action";

    // User's chosen action: "No Action", "Accept", "Reject", "Pending", "Reset"
    public string Action { get; set; } = "No Action";

    public DateTime? Gstr1FilingDate { get; set; }
    public string? Gstr1FilingPeriod { get; set; }
    public bool IsItcEligible { get; set; } = true;
}

public class ImsInwardFetchResponse
{
    public string FilingPeriod { get; set; } = string.Empty;
    public int TotalRecords { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public int PendingCount { get; set; }
    public int NoActionCount { get; set; }
    public string Source { get; set; } = "LIVE_GSTN";
    public List<ImsInvoiceDto> Records { get; set; } = new();
}

public class ImsActionSubmitRequest
{
    public string FilingPeriod { get; set; } = string.Empty;
    public List<ImsActionItemDto> Actions { get; set; } = new();
}

public class ImsActionItemDto
{
    public string SupplierGstin { get; set; } = string.Empty;
    public string InvoiceNo { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public string RecordType { get; set; } = "B2B";
    public string Action { get; set; } = "Accept"; // Accept, Reject, Pending, Reset
}

public class ImsActionSubmitResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int ProcessedCount { get; set; }
    public string? ReferenceId { get; set; }
}

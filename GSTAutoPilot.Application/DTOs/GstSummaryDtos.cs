namespace GSTAutoPilot.Application.DTOs;

public class GstSummaryResponse
{
    public string Period { get; set; } = string.Empty;
    public string TenantGSTIN { get; set; } = string.Empty;
    public OutputGstSection OutputGST { get; set; } = new();
    public ItcFromGstr2BSection ItcFromGSTR2B { get; set; } = new();
    public ReconSummary ReconSummary { get; set; } = new();
    public NetTaxPayableSection NetTaxPayable { get; set; } = new();
    public CarryForwardSection CarryForward { get; set; } = new();
    public string AIRemarks { get; set; } = string.Empty;
}

public class CarryForwardSection
{
    public decimal IGST { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal TotalCarryForward { get; set; }
    public string Remarks { get; set; } = string.Empty;
}

public class OutputGstSection
{
    public decimal TaxableAmount { get; set; }
    public decimal IGST { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal TotalGST { get; set; }
    public int InvoiceCount { get; set; }
}

public class ItcFromGstr2BSection
{
    public decimal TotalITC { get; set; }
    public decimal MatchedITC { get; set; }
    public decimal MismatchedITC { get; set; }
    public decimal MissingITC { get; set; }
    // Import IGST from customs Bills of Entry (auto-eligible, no 2B recon
    // needed). Already INCLUDED in TotalITC and EligibleITC; shown separately.
    public decimal ImportIgst { get; set; }
    public decimal EligibleITC { get; set; }
    // ITC on matched invoices that GSTR-2B marks unavailable (itcavl "N" — PoS
    // rule, section 16(4) time-bar, etc.). Excluded from EligibleITC so it isn't
    // claimed, and surfaced here so the reason is visible.
    public decimal IneligibleITC { get; set; }
}

public class NetTaxPayableSection
{
    public decimal IGST { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal Total { get; set; }
}

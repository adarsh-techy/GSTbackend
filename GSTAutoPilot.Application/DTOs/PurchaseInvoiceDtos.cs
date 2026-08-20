namespace GSTAutoPilot.Application.DTOs;

public class PurchaseInvoiceResponse
{
    public Guid PurchaseInvoiceId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string SupplierGSTIN { get; set; } = string.Empty;
    public string InvoiceNo { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal IGSTAmount { get; set; }
    public decimal CGSTAmount { get; set; }
    public decimal SGSTAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal GSTRate { get; set; }
    public bool IsITCEligible { get; set; }
    public DateTime CreatedOn { get; set; }

    // Document category from the inward SP's Bill_Cat column:
    //   "Purchase"   -> reconciles against GSTR-2B B2B
    //   "CreditNote" -> GSTR-2B CDNR, REDUCES ITC (signed negative in recon)
    //   "DebitNote"  -> GSTR-2B CDNR, increases ITC (positive)
    // Defaults to "Purchase" when the SP doesn't emit the column (back-compat).
    public string BillCategory { get; set; } = "Purchase";
}

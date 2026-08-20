namespace GSTAutoPilot.Domain.Entities;

public class PurchaseInvoice
{
    public Guid PurchaseInvoiceId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
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
    public bool IsITCEligible { get; set; } = true;
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}

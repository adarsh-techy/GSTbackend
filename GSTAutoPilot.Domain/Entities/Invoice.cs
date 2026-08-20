namespace GSTAutoPilot.Domain.Entities;

public class Invoice : BaseEntity
{
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public string PartyName { get; set; } = string.Empty;
    public string PartyGSTIN { get; set; } = string.Empty;
    public string PlaceOfSupply { get; set; } = string.Empty;
    public decimal TaxableValue { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal IGST { get; set; }
    public decimal TotalAmount { get; set; }

    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
}

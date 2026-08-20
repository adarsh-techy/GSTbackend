namespace GSTAutoPilot.Infrastructure.CarolERP.Entities;

public class CarolPurchaseMas
{
    public int BillId { get; set; }
    // DocId flags the document type inside Bill_Mas (sales vs purchase vs
    // notes). Used to apply a per-tenant purchase DocType filter.
    public short? DocId { get; set; }
    public DateTime BillDate { get; set; }
    public string? InvNo { get; set; }
    public int? BillNumber { get; set; }
    public string? Suffix { get; set; }
    public short AccountId { get; set; }
    public decimal TotalAmt { get; set; }
    public decimal ExchRate { get; set; }
    public string? GstNo { get; set; }
    public byte? StateId { get; set; }
    public byte GstReverse { get; set; }
    public string? AcName { get; set; }
    // Approval flag — 1 = approved (sanctioned), 0 = not yet approved. Reads
    // filter on Sanctioned=1 only when the row's DocType has Documents.Sanction=1.
    public byte Sanctioned { get; set; }
}

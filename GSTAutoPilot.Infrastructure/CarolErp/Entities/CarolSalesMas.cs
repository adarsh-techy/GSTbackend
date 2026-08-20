namespace GSTAutoPilot.Infrastructure.CarolERP.Entities;

public class CarolSalesMas
{
    public int BillId { get; set; }
    public short? DocId { get; set; }
    public DateTime BillDate { get; set; }
    public string? InvNo { get; set; }
    public int? BillNumber { get; set; }
    public string? Suffix { get; set; }
    public short AccountId { get; set; }
    public decimal TotalAmt { get; set; }
    public decimal ExchRate { get; set; }
    public string? SupplyType { get; set; }
    public string? IRN { get; set; }
    public long? AckNo { get; set; }
    public string? EwbNo { get; set; }
    public string? Status { get; set; }
    public string? SignedQRCode { get; set; }
    // Approval flag — 1 = approved (sanctioned), 0 = not yet approved. Reads
    // filter on Sanctioned=1 only when the row's DocType has Documents.Sanction=1.
    public byte Sanctioned { get; set; }
}

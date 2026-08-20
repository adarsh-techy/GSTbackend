namespace GSTAutoPilot.Infrastructure.CarolERP.Entities;

public class CarolSalesTrn
{
    public int BillFileSl { get; set; }
    public int BillId { get; set; }
    public short? ItemId { get; set; }
    public short? SpecId { get; set; }
    public short? SizeId { get; set; }
    public int? DesignId { get; set; }
    public decimal Quantity { get; set; }
    public decimal? Rate { get; set; }
    public decimal Amount { get; set; }
    public decimal IgstRate { get; set; }
    public decimal IgstAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal DiscAmt { get; set; }
}

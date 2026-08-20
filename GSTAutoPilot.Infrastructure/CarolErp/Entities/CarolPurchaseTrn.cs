namespace GSTAutoPilot.Infrastructure.CarolERP.Entities;

public class CarolPurchaseTrn
{
    public int BillInpSl { get; set; }
    public int BillId { get; set; }
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
    public decimal CgstRate { get; set; }
    public decimal? CGSTAmt { get; set; }
    public decimal SgstRate { get; set; }
    public decimal? SGSTAmt { get; set; }
    public decimal IgstRate { get; set; }
    public decimal? IGSTAmt { get; set; }
}

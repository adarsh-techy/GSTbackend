namespace GSTAutoPilot.Infrastructure.CarolERP.Entities;

public class CarolAccount
{
    public short AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string? GstNo { get; set; }
    public byte? CountryId { get; set; }
    public byte? StateId { get; set; }
}

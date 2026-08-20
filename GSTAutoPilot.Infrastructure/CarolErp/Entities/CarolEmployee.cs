namespace GSTAutoPilot.Infrastructure.CarolERP.Entities;

public class CarolEmployee
{
    public short EmplId { get; set; }
    public string EmplCode { get; set; } = string.Empty;
    public string? EmplName { get; set; }
    public string? Password { get; set; }
    public short? MasId { get; set; }
    public byte? Active { get; set; }
}

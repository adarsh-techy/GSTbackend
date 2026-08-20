namespace GSTAutoPilot.Infrastructure.CarolERP.Entities;

public class CarolItem
{
    public short ItemId { get; set; }
    public string? ItemName { get; set; }
    public short? HsnId { get; set; }
}

public class CarolHsn
{
    public short HsnId { get; set; }
    public string? HsnCode { get; set; }
}

public class CarolSpecification
{
    public short SpecId { get; set; }
    public string? SpecName { get; set; }
}

public class CarolItemSize
{
    public short SizeId { get; set; }
    public string? SizeName { get; set; }
}

public class CarolProduct
{
    public int ProductId { get; set; }
    public string? ProductName { get; set; }
    public short? ItemId { get; set; }
    public short? SpecId { get; set; }
    public short? SizeId { get; set; }
    public int? DesignId { get; set; }
}

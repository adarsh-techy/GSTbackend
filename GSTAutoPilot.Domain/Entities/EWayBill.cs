namespace GSTAutoPilot.Domain.Entities;

public class EWayBill
{
    public Guid EWBId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public string EWBNumber { get; set; } = string.Empty;
    // Human invoice number from the bill this EWB was generated against —
    // surfaced on the standalone e-Way Bills list so the user can identify
    // which bill the EWB belongs to without cross-referencing InvoiceId
    // (which is a synthetic GUID for CarolERP-sourced tenants).
    public string? InvoiceNo { get; set; }
    public DateTime GeneratedDate { get; set; }
    public DateTime ValidUntil { get; set; }
    public string FromGSTIN { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string ToGSTIN { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public string TransporterGSTIN { get; set; } = string.Empty;
    public string TransporterName { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public decimal Distance { get; set; }
    public string Mode { get; set; } = EWayBillMode.Road;
    public string Status { get; set; } = EWayBillStatus.Active;
    public DateTime? CancelledOn { get; set; }
    public string? CancelReason { get; set; }
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}

public static class EWayBillStatus
{
    public const string Active = "Active";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
}

public static class EWayBillMode
{
    public const string Road = "Road";
    public const string Rail = "Rail";
    public const string Air = "Air";
    public const string Ship = "Ship";
}

namespace GSTAutoPilot.Application.DTOs;

public class GenerateEWayBillRequest
{
    public string? FromAddress { get; set; }
    public string? ToAddress { get; set; }
    public string? TransporterGSTIN { get; set; }
    public string? TransporterName { get; set; }
    public string? VehicleNumber { get; set; }
    public decimal Distance { get; set; }
    public string? Mode { get; set; }
}

public class CancelEWayBillRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class UpdateVehicleRequest
{
    public string VehicleNumber { get; set; } = string.Empty;
}

public class EWayBillResponse
{
    public Guid EWBId { get; set; }
    public Guid InvoiceId { get; set; }
    public string EWBNumber { get; set; } = string.Empty;
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
    public string Mode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? CancelledOn { get; set; }
    public string? CancelReason { get; set; }
    public DateTime CreatedOn { get; set; }
    public string Source { get; set; } = "STUB";
}

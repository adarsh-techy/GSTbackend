namespace GSTAutoPilot.Domain.Entities;

public class IRNRecord
{
    public Guid IRNId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public int? BillId { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public string IRNNumber { get; set; } = string.Empty;
    public string QRCode { get; set; } = string.Empty;
    public string AcknowledgementNo { get; set; } = string.Empty;
    public DateTime AcknowledgementDate { get; set; }
    public string SignedInvoice { get; set; } = string.Empty;
    public string Status { get; set; } = IRNStatus.Generated;
    public string Source { get; set; } = "STUB";
    public DateTime? CancelledOn { get; set; }
    public string? CancelReason { get; set; }
    public string? CancelRemarks { get; set; }
    public DateTime? EmailSentOn { get; set; }
    public string? EmailSentTo { get; set; }
    public DateTime? JsonDownloadedOn { get; set; }
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}

public static class IRNStatus
{
    // Persisted values.
    public const string Generated = "Generated";
    public const string Cancelled = "Cancelled";
    // Computed lifecycle states surfaced to the UI (not stored).
    public const string Cancellable = "Cancellable";
    public const string Locked = "Locked";
    public const string Stub = "Stub";
}

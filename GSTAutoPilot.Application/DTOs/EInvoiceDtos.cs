namespace GSTAutoPilot.Application.DTOs;

public class IRNResponse
{
    public Guid IRNId { get; set; }
    public Guid InvoiceId { get; set; }
    public int? BillId { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public string IRNNumber { get; set; } = string.Empty;
    public string QRCode { get; set; } = string.Empty;
    public string AcknowledgementNo { get; set; } = string.Empty;
    public DateTime AcknowledgementDate { get; set; }
    public string SignedInvoice { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;          // persisted: Generated | Cancelled
    public DateTime? CancelledOn { get; set; }
    public string? CancelReason { get; set; }
    public string? CancelRemarks { get; set; }
    public DateTime? EmailSentOn { get; set; }
    public string? EmailSentTo { get; set; }
    public DateTime? JsonDownloadedOn { get; set; }
    public DateTime CreatedOn { get; set; }
    public string Source { get; set; } = "STUB";

    // Computed lifecycle fields (not persisted).
    public string LifecycleStatus { get; set; } = string.Empty;  // Generated->Cancellable|Locked, or Cancelled
    public bool IsCancellable { get; set; }
    public bool IsStub { get; set; }
    public double AgeHours { get; set; }
    public string TimeRemaining { get; set; } = string.Empty;
}

public class CancelIrnRequest
{
    // "1".."4" per NIC (1 Duplicate, 2 Data Entry Mistake, 3 Order Cancelled, 4 Others).
    public string Reason { get; set; } = string.Empty;
    public string? Remarks { get; set; }
}

public class EmailJsonRequest
{
    public string ToEmail { get; set; } = string.Empty;
    public string? CcEmail { get; set; }
    public string? Remarks { get; set; }
}

// A downloadable artifact (signed JSON / QR PNG) returned to the controller.
public record EInvoiceFile(byte[] Bytes, string FileName, string ContentType);

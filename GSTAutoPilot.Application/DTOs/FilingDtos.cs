namespace GSTAutoPilot.Application.DTOs;

public enum FilingType
{
    Gstr1 = 1,
    Gstr3b = 2,
}

public enum FilingStatus
{
    Locked = 1,
    Filed = 2,
    // Saved AND submitted (locked) on GSTN; awaiting file-with-OTP.
    Submitted = 3,
    // Saved to GSTN but rejected with validation errors — cannot be submitted
    // until the underlying data is corrected and saved again.
    SaveFailed = 4,
}

// Result of pushing a locked return to GSTN (retsave -> retsubmit).
public class GstnSubmitResponse
{
    public Guid FilingId { get; set; }
    public FilingStatus Status { get; set; }
    // GSTN's reference id correlating this save/submit with the later file.
    public string? ReferenceId { get; set; }
    // True when the return is locked on GSTN and ready to file with an OTP.
    public bool ReadyToFile { get; set; }
    // GSTN's raw validation report when the save was rejected.
    public string? ErrorReportJson { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class FileWithEvcCommand
{
    // The EVC OTP sent to the authorised signatory by /authentication/otpforevc.
    public string Otp { get; set; } = string.Empty;
    // Challan identification number, when GSTR-3B tax was paid by challan.
    // Recorded against the filing; GSTN itself takes payment detail via the
    // return payload, not this call.
    public string? Cin { get; set; }
}

public class FilingResponse
{
    public Guid FilingId { get; set; }
    public FilingType Type { get; set; }
    public string Period { get; set; } = string.Empty;
    public FilingStatus Status { get; set; }
    public string? AckNo { get; set; }
    public DateTime? FiledOn { get; set; }
    public DateTime CreatedOn { get; set; }
    public string? ReferenceId { get; set; }
    public DateTime? SubmittedOn { get; set; }
    public string? FiledBy { get; set; }
}

public class FilingDetailResponse : FilingResponse
{
    public string PayloadJson { get; set; } = string.Empty;
    public string? ErrorReportJson { get; set; }
}

public class MarkFiledCommand
{
    public string AckNo { get; set; } = string.Empty;
    public DateTime? FiledOn { get; set; }
}

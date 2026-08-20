namespace GSTAutoPilot.Domain.Entities;

public class Gstr1Filing
{
    public Guid FilingId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Period { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public string? AckNo { get; set; }
    public DateTime? FiledOn { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    // GSTN's reference id from retsave/retsubmit. Required to correlate the
    // save -> submit -> file sequence; without it a partial filing cannot be
    // resumed or traced on the portal.
    public string? ReferenceId { get; set; }
    // GSTN's validation report from the last retsave, when it rejected rows.
    public string? ErrorReportJson { get; set; }
    // When the return was locked on GSTN (retsubmit), distinct from FiledOn.
    public DateTime? SubmittedOn { get; set; }
    // Which of our users performed the filing — required for audit.
    public string? FiledBy { get; set; }
}

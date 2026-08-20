namespace GSTAutoPilot.Application.DTOs;

// Pre-file validation result for a return. CanFile is false when any Error-level
// issue exists; warnings are advisory and do not block ("Continue Anyway").
public class ReturnValidationResult
{
    public string Period { get; set; } = string.Empty;     // YYYYMM
    public string ReturnType { get; set; } = string.Empty; // "GSTR1"
    public int InvoicesChecked { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public bool CanFile => ErrorCount == 0;
    // Per-invoice issues, most-severe first. Capped (see IssuesTruncated); the
    // ErrorCount / WarningCount totals are always the true full counts.
    public List<ValidationIssue> Issues { get; set; } = new();
    public bool IssuesTruncated { get; set; }
}

public class ValidationIssue
{
    public string Severity { get; set; } = "Error"; // "Error" | "Warning"
    public string Code { get; set; } = string.Empty; // MISSING_HSN, INVALID_GSTIN, ...
    public string Message { get; set; } = string.Empty;
    public string? InvoiceNo { get; set; }
    public string? Section { get; set; }
}

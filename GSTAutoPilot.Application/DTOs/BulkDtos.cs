namespace GSTAutoPilot.Application.DTOs;

// One invoice a bulk run would act on. The run is driven from the browser one
// item at a time (so it stops when the user stops, and nothing keeps firing at
// the portal after a tab closes), and this is the work list it iterates.
public class BulkCandidate
{
    public int BillId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public string PartyName { get; set; } = string.Empty;
    public string PartyGSTIN { get; set; } = string.Empty;
    public decimal InvoiceValue { get; set; }

    // Recipient for the e-Invoice email, from the ERP account master. Null when
    // the buyer has no email on file — those rows are listed as blocked rather
    // than silently dropped, because "nothing happened" and "nothing needed to
    // happen" must not look the same.
    public string? PartyEmail { get; set; }

    // Set when this row cannot be processed; null when it is ready to go.
    public string? BlockedReason { get; set; }
}

public class BulkCandidatesResponse
{
    public string Operation { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    // Ready to process.
    public List<BulkCandidate> Ready { get; set; } = new();
    // Found, but something stops them (no buyer email, no IRN yet, ...).
    public List<BulkCandidate> Blocked { get; set; } = new();

    // The ceiling this operation is paced at, so the UI can show an honest
    // estimate before the user starts a long run.
    public int RateLimitMax { get; set; }
    public int RateLimitPeriodSeconds { get; set; }
    // Slots left in the current window right now.
    public int RateLimitRemaining { get; set; }
}

// Which returns are still unfiled for a period, for the "file all pending
// returns" wizard. GSTR-1 must be filed before GSTR-3B, so order matters.
public class PendingReturnsResponse
{
    public string Period { get; set; } = string.Empty;
    public List<PendingReturn> Returns { get; set; } = new();
    // True when a GSTN OTP session is live, so the wizard can carry one session
    // across both returns instead of asking twice.
    public bool HasGstnSession { get; set; }
    public bool GstnConfigured { get; set; }
}

public class PendingReturn
{
    public FilingType Type { get; set; }
    public string Period { get; set; } = string.Empty;
    // Locked / Submitted / SaveFailed / Filed, or null when nothing exists yet.
    public string? Status { get; set; }
    public Guid? FilingId { get; set; }
    public string? AckNo { get; set; }
    // False once the return is Filed — the wizard skips those.
    public bool NeedsAction { get; set; }
    // The step the wizard should take next: lock | submit | file | done.
    public string NextStep { get; set; } = "lock";
}

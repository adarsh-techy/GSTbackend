namespace GSTAutoPilot.Infrastructure.Services;

// Section 17(5) of the CGST Act blocks input tax credit on certain expenses
// (motor vehicles, food & beverages, club/health memberships, employee travel
// benefits, works-contract for immovable property, goods for personal use, …).
//
// CarolERP DocType 930 (Bill_General) journals book these to named expense
// accounts. When a pattern below matches a 930 line's expense Account name
// (case-insensitive substring), that line's ITC is EXCLUDED from GSTR-3B Table 4.
//
// Defaults to EMPTY — i.e. no exclusion, no behaviour change — until the tenant
// (with their CA) reviews the actual chart of accounts and lists the blocked
// ones. Getting this wrong under-claims real ITC, so it is opt-in by design.
public class Sec175Options
{
    public const string SectionName = "Sec175";

    // Case-insensitive substrings matched against the 930 expense account name.
    // e.g. ["Club Membership", "Motor Car", "Staff Welfare - Food"].
    public List<string> BlockedAccountPatterns { get; set; } = new();
}

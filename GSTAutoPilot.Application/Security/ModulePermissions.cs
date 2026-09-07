namespace GSTAutoPilot.Application.Security;

/// <summary>
/// Catalogue of module permission keys. A UserRole row stores the granted keys as a
/// comma-separated list; "Admin" implicitly holds every key and cannot be restricted.
/// Keys must stay in sync with client/src/auth/permissions.ts.
/// </summary>
public static class ModulePermissions
{
    public const string Dashboard = "dashboard";
    public const string Invoices = "invoices";
    public const string EInvoice = "einvoice";
    public const string EWayBill = "ewaybill";
    public const string Gstr1 = "gstr1";
    public const string Gstr2b = "gstr2b";
    public const string Gstr3b = "gstr3b";
    public const string BillOfEntry = "boe";
    public const string Reconciliation = "recon";
    public const string Filings = "filings";
    public const string Onboarding = "onboarding";
    public const string Users = "users";
    public const string Settings = "settings";

    /// <summary>Every key an admin may hand out to a non-admin user.</summary>
    public static readonly IReadOnlyList<string> Assignable = new[]
    {
        Dashboard, Invoices, EInvoice, EWayBill,
        Gstr1, Gstr2b, Gstr3b, BillOfEntry,
        Reconciliation, Filings,
    };

    /// <summary>Keys reserved for the Admin role; never stored on a non-admin row.</summary>
    public static readonly IReadOnlyList<string> AdminOnly = new[] { Onboarding, Users, Settings };

    /// <summary>Sensible starting set when an admin adds a user without ticking anything.</summary>
    public static readonly IReadOnlyList<string> Default = new[] { Dashboard };

    public static readonly IReadOnlyList<string> All =
        Assignable.Concat(AdminOnly).ToList();

    public static bool IsAdmin(string? role) =>
        string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps only known, assignable keys — order preserved, duplicates dropped.
    /// </summary>
    public static List<string> Sanitize(IEnumerable<string>? requested)
    {
        if (requested is null) return new List<string>();
        var allowed = new HashSet<string>(Assignable, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var key in requested)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var trimmed = key.Trim();
            if (!allowed.Contains(trimmed)) continue;
            var canonical = Assignable.First(a => string.Equals(a, trimmed, StringComparison.OrdinalIgnoreCase));
            if (seen.Add(canonical)) result.Add(canonical);
        }
        return result;
    }

    /// <summary>Effective permissions for a stored row — Admin always resolves to everything.</summary>
    /// <param name="permissionsStored">
    /// False when the deployment has no Permissions column to read from. There is then no
    /// per-user grant to honour, so access falls back to the role model the schema does
    /// support: admins get everything, everyone else gets the assignable modules. Passing
    /// the stored CSV through unchanged would instead resolve to an empty list and lock
    /// every non-admin out of the entire application.
    /// </param>
    public static List<string> Effective(string? role, string? storedCsv, bool permissionsStored = true) =>
        IsAdmin(role) ? All.ToList()
        : permissionsStored ? Parse(storedCsv)
        : Assignable.ToList();

    public static List<string> Parse(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new List<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string ToCsv(IEnumerable<string> keys) => string.Join(',', keys);
}

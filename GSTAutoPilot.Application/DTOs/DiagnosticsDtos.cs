namespace GSTAutoPilot.Application.DTOs;

// Health snapshot of the tenant's stored-procedure data sources, returned by
// GET /api/diagnostics/sp-profile. Each direction is exercised live against the
// tenant's CarolERP DB so the UI can show a green/amber/red signal.
public class SpDiagnosticsDto
{
    public string? TenantName { get; set; }
    public SpDirectionDiagnostics Outward { get; set; } = new();
    public SpDirectionDiagnostics Inward { get; set; } = new();
}

public class SpDirectionDiagnostics
{
    public string? SpName { get; set; }
    public bool Configured { get; set; }
    // Whether we actually invoked the SP this request.
    public bool Tested { get; set; }
    // Whether the invocation returned without error.
    public bool Ok { get; set; }
    // Total DISTINCT invoices the SP returned across the last 24 months.
    public int InvoiceCount { get; set; }
    // Number of periods (yyyyMM) that had at least one invoice.
    public int PeriodCount { get; set; }
    public string? Error { get; set; }
    // "NotConfigured" | "Green" (configured, ran, has data) |
    // "Amber" (configured, ran, no data) | "Red" (configured, threw).
    public string Status { get; set; } = "NotConfigured";
}

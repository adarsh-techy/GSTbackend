namespace GSTAutoPilot.Infrastructure.Services;

// Tuning knobs for the CarolERP read path. Every value here changes only how
// often this application re-reads the ERP — nothing here writes to, migrates,
// or otherwise alters any database.
public class PerformanceOptions
{
    public const string SectionName = "Performance";

    // How long a period's outward invoice set is reused across requests.
    // Only the ERP-derived part is cached: e-invoice status and IRN are always
    // re-read from the tenant DB per request, so a freshly generated IRN still
    // appears immediately. Set to 0 to disable the cache entirely.
    public int OutwardCacheSeconds { get; set; } = 90;

    // How long the ERP period list (the month dropdown) is reused. Invoice
    // counts per month change slowly, so this can be far longer than the
    // invoice cache. 0 disables it.
    public int PeriodsCacheSeconds { get; set; } = 300;

    // How far back the stored-procedure period scan goes. The SP returns
    // line-level rows which this app reads only to count distinct bills, so
    // every extra month is a month of rows fetched and thrown away — this is
    // the single biggest lever on the cost of the period selector.
    // Only affects the SP path; the table-mapping reader aggregates in SQL.
    public int PeriodMonthsBack { get; set; } = 12;
}

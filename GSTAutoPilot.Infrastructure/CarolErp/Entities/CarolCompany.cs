namespace GSTAutoPilot.Infrastructure.CarolERP.Entities;

// CarolERP `company` master. Restricted to columns that exist on BOTH the
// Flooratex- and KSCC-flavor installs; drift'd columns (GstNo vs GSTNumber,
// the extended bank/email/PAN block that's Flooratex-only) are read via raw
// SQL helpers on CarolERPDbContext when needed.
public class CarolCompany
{
    public byte CoId { get; set; }
    public string? CoName { get; set; }
    public string? CoAddr1 { get; set; }
    public string? CoAddr2 { get; set; }
    public string? CoAddr3 { get; set; }
    public string? TelNo { get; set; }
    // StateId drifts in column TYPE between installs (Flooratex tinyint vs
    // KSCC smallint). int? safely accommodates both; SQL state codes (01-37)
    // fit easily.
    public int? StateId { get; set; }
}

namespace GSTAutoPilot.Application.Configuration;

// Statutory GST rule values that occasionally change by notification and are
// therefore externalised to config (appsettings "GstRules"), with the current
// legal values baked in as defaults so a missing/blank config is still correct.
public sealed class GstRulesOptions
{
    public const string SectionName = "GstRules";

    public B2clThresholdConfig B2CLThreshold { get; set; } = new();

    // Inter-state B2C invoice-value threshold above which the invoice is
    // reported B2CL (invoice-wise) instead of B2CS (rate-wise). Notification
    // 12/2024-CT (10 Jul 2024) cut it from Rs 2,50,000 to Rs 1,00,000 for
    // supplies on or after 1 Aug 2024.
    public sealed class B2clThresholdConfig
    {
        public decimal PreAug2024 { get; set; } = 250_000m;
        public decimal PostAug2024 { get; set; } = 100_000m;
    }
}

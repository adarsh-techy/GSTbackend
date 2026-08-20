namespace GSTAutoPilot.Infrastructure.Services;

// Resolves a line's COMBINED GST rate (%). CarolERP intra-state lines store the
// IGST percentage as 0 (the tax sits in CGST/SGST), and credit-note lines
// (Bill_DrCr_Items) carry no rate at all — so when the stored rate is 0 but tax
// is present we derive the rate from the amounts and snap it to the nearest
// standard GST slab. Used for per-line rate bucketing in the GSTN JSON + HSN.
public static class GstRateHelper
{
    private static readonly decimal[] Slabs = { 0m, 0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m };

    public static decimal Effective(decimal storedRate, decimal taxable, decimal igst, decimal cgst, decimal sgst)
    {
        if (storedRate > 0m) return storedRate;
        if (taxable == 0m) return 0m;
        var derived = (igst + cgst + sgst) / taxable * 100m;
        var nearest = Slabs.OrderBy(s => Math.Abs(s - derived)).First();
        // Snap to a standard slab when close; otherwise keep the derived value.
        return Math.Abs(nearest - derived) <= 0.5m ? nearest : decimal.Round(derived, 2);
    }
}

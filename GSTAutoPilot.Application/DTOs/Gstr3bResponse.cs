namespace GSTAutoPilot.Application.DTOs;

public class Gstr3bResponse
{
    public string Period { get; set; } = string.Empty;
    public OutwardSuppliesSection Section3_1_OutwardSupplies { get; set; } = new();
    public ItcSection Table4_Itc { get; set; } = new();
    public ExemptInwardSection Table5_ExemptInward { get; set; } = new();
    public TaxLiabilitySummary NetTaxPayable { get; set; } = new();
    public CarryForwardSection CarryForward { get; set; } = new();
}

// GSTR-3B Table 5 — exempt, nil-rated & non-GST inward supplies. Grouped by the
// supplier's 2-digit state code ("" = unregistered/unknown) so the JSON builder
// can split each into inter-state vs intra-state against the seller's state.
public class ExemptInwardSection
{
    public Dictionary<string, decimal> ExemptNilByState { get; set; } = new();
    public Dictionary<string, decimal> NonGstByState { get; set; } = new();
}

// One value+tax line, used for the GSTR-3B 3.1 sub-rows.
public class Gstr3bLine
{
    public decimal TaxableValue { get; set; }
    public decimal IGST { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal TotalGst => IGST + CGST + SGST;
}

public class OutwardSuppliesSection
{
    public int InvoiceCount { get; set; }
    // Aggregate of the OUTWARD rows only (3.1 a+b+c+e); 3.1(d) reverse-charge is
    // an inward liability and is kept separate below.
    public decimal TaxableValue { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal IGST { get; set; }
    public decimal TotalGstCollected => CGST + SGST + IGST;

    // GSTR-3B Table 3.1 breakdown.
    public Gstr3bLine TaxableOutward { get; set; } = new();        // 3.1(a)
    public Gstr3bLine ZeroRated { get; set; } = new();             // 3.1(b) exports / SEZ
    public Gstr3bLine NilRatedExempt { get; set; } = new();        // 3.1(c)
    public Gstr3bLine ReverseChargeInward { get; set; } = new();   // 3.1(d) liability
    public Gstr3bLine NonGstOutward { get; set; } = new();         // 3.1(e)
}

public class ItcSection
{
    public int PurchaseCount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal IGST { get; set; }
    // Import IGST from customs Bills of Entry (Table 4(A)(1)). Already INCLUDED
    // in IGST above; surfaced separately for transparency.
    public decimal ImportIgst { get; set; }
    // ITC on inward supplies liable to reverse charge (Table 4(A)(3)). Already
    // INCLUDED in the CGST/SGST/IGST above; surfaced separately. Mirrors the
    // 3.1(d) liability so RCM is cash-neutral when fully creditable.
    public decimal ReverseChargeCGST { get; set; }
    public decimal ReverseChargeSGST { get; set; }
    public decimal ReverseChargeIGST { get; set; }
    // Sec 17(5) blocked ITC, per head. EXCLUDED from CGST/SGST/IGST above (those
    // are net ITC = 4C). Reported in Table 4A(5) gross and reversed in Table
    // 4B(1) per Circular 170/02/2022, so net ITC is unchanged.
    public decimal BlockedIgst { get; set; }
    public decimal BlockedCgst { get; set; }
    public decimal BlockedSgst { get; set; }
    public decimal TotalItcAvailable => CGST + SGST + IGST;
    public string Note { get; set; } = "ITC per books (CarolERP inward): 4A5 all-other + 4A1 import + 4A3 reverse-charge. Reconcile against GSTR-2B on the dashboard/Recon screen. 4B(1) carries Sec 17(5) blocked ITC (Circular 170); 4B(2) & 4D not modeled yet.";
}

public class TaxLiabilitySummary
{
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal IGST { get; set; }
    public decimal Total => CGST + SGST + IGST;
}

// Net-payable-over-time series (GSTR-3B basis: CarolERP outward/inward + BoE,
// no GSTR-2B-fetch dependency), oldest period first.
public class Gstr3bTrendPoint
{
    public string Period { get; set; } = string.Empty;
    public decimal OutputTax { get; set; }
    public decimal Itc { get; set; }
    public decimal NetPayable { get; set; }
}

public class Gstr3bTrendResponse
{
    public List<Gstr3bTrendPoint> Points { get; set; } = new();
}

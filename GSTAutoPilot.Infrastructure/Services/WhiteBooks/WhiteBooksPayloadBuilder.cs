using System.Globalization;
using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Infrastructure.Services.WhiteBooks;

// Maps an InvoiceResponse (CarolERP-sourced) + company master into the NIC
// e-Invoice JSON schema (v1.1) that WhiteBooks proxies to GSTN. Only the
// mandatory blocks are populated. KSCC is export-heavy, so when the buyer has
// no GSTIN we mark the supply EXPWOP (export without payment) and the buyer as
// URP (unregistered person) per the NIC spec.
internal static class WhiteBooksPayloadBuilder
{
    // useSandboxDefaults=true substitutes seller address/PIN with values that
    // match the sandbox GSTIN's state — the company's real address typically
    // doesn't match the test GSTIN's state, which fails NIC validation 3039.
    public static object Build(InvoiceResponse invoice, CompanyDto company, string sellerGstin, bool useSandboxDefaults = false)
    {
        var isExport = string.Equals(invoice.PartyGSTIN, "Export", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(invoice.PartyGSTIN)
            || !LooksLikeGstin(invoice.PartyGSTIN);
        // EXPWP = export WITH payment of IGST; EXPWOP = export WITHOUT IGST.
        // Sending EXPWOP with non-zero IGST trips NIC 2235.
        var supplyType = isExport
            ? (invoice.IGST > 0 ? "EXPWP" : "EXPWOP")
            : "B2B";
        var buyerGstin = LooksLikeGstin(invoice.PartyGSTIN) ? invoice.PartyGSTIN : "URP";
        var sellerStateCode = StateCode(sellerGstin);
        // For exports POS is "96" (other territory / outside India) per NIC.
        var pos = isExport ? "96" : (LooksLikeGstin(buyerGstin) ? StateCode(buyerGstin) : sellerStateCode);

        var items = new List<object>();
        var slNo = 1;
        if (invoice.Lines.Count > 0)
        {
            foreach (var l in invoice.Lines)
            {
                var assAmt = Round(l.TaxableValue);
                // NIC 2235 enforces IgstAmt == AssAmt * GstRt / 100 (to 2dp).
                // Stored line IGSTs sometimes drift from this by a few paise.
                // Recompute from rate so the math matches NIC's expectation.
                var igstAmt = isExport && l.GstRate > 0 ? Round(assAmt * l.GstRate / 100m) : Round(l.IGST);
                var cgstAmt = isExport ? 0m : Round(l.CGST);
                var sgstAmt = isExport ? 0m : Round(l.SGST);
                var totItem = isExport ? Round(assAmt + igstAmt) : Round(l.Total);
                items.Add(new
                {
                    SlNo = slNo.ToString(CultureInfo.InvariantCulture),
                    PrdDesc = string.IsNullOrWhiteSpace(l.Description) ? $"Item {slNo}" : Trim(l.Description, 300),
                    IsServc = "N",
                    HsnCd = string.IsNullOrWhiteSpace(l.HSNCode) ? "00000000" : l.HSNCode,
                    Qty = (double)l.Quantity,
                    Unit = "NOS",
                    UnitPrice = (double)Round(l.Rate),
                    TotAmt = (double)assAmt,
                    AssAmt = (double)assAmt,
                    GstRt = (double)l.GstRate,
                    IgstAmt = (double)igstAmt,
                    CgstAmt = (double)cgstAmt,
                    SgstAmt = (double)sgstAmt,
                    TotItemVal = (double)totItem,
                });
                slNo++;
            }
        }
        else
        {
            // Header-only invoice: synthesize a single summary line.
            items.Add(new
            {
                SlNo = "1",
                PrdDesc = "Goods",
                IsServc = "N",
                HsnCd = "00000000",
                Qty = 1.0,
                Unit = "NOS",
                UnitPrice = (double)Round(invoice.TaxableValue),
                TotAmt = (double)Round(invoice.TaxableValue),
                AssAmt = (double)Round(invoice.TaxableValue),
                GstRt = 0.0,
                IgstAmt = (double)Round(invoice.IGST),
                CgstAmt = (double)Round(invoice.CGST),
                SgstAmt = (double)Round(invoice.SGST),
                TotItemVal = (double)Round(invoice.TotalAmount),
            });
        }

        return new
        {
            Version = "1.1",
            TranDtls = new { TaxSch = "GST", SupTyp = supplyType, RegRev = "N", IgstOnIntra = "N" },
            DocDtls = new
            {
                Typ = "INV",
                No = invoice.InvoiceNumber,
                Dt = invoice.InvoiceDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            },
            SellerDtls = useSandboxDefaults ? new
            {
                Gstin = sellerGstin,
                LglNm = string.IsNullOrWhiteSpace(company.CompanyName) ? "Sandbox Seller" : company.CompanyName,
                Addr1 = "Sandbox Test Address",
                Loc = SandboxLocForState(sellerStateCode),
                Pin = SandboxPinForState(sellerStateCode),
                Stcd = sellerStateCode,
            } : new
            {
                Gstin = sellerGstin,
                LglNm = string.IsNullOrWhiteSpace(company.CompanyName) ? "Seller" : company.CompanyName,
                Addr1 = Trim(company.Address1 ?? "NotSpecified", 100),
                Loc = Trim(company.Address2 ?? company.Address1 ?? "NotSpecified", 50),
                Pin = ParsePin(company.PinCode, sellerStateCode),
                Stcd = sellerStateCode,
            },
            BuyerDtls = new
            {
                Gstin = buyerGstin,
                LglNm = string.IsNullOrWhiteSpace(invoice.PartyName) ? "Buyer" : invoice.PartyName,
                Pos = pos,
                // NIC enforces min length 3 on Addr1 and Loc — placeholder "NA"
                // (2 chars) fails with 5002 on every export bill where
                // PlaceOfSupply is empty. Use a 3+ char default.
                Addr1 = Trim(string.IsNullOrWhiteSpace(invoice.PlaceOfSupply) ? (isExport ? "Foreign" : "NotSpecified") : invoice.PlaceOfSupply, 100),
                Loc = Trim(string.IsNullOrWhiteSpace(invoice.PlaceOfSupply) ? (isExport ? "Foreign" : "NotSpecified") : invoice.PlaceOfSupply, 50),
                Pin = isExport ? 999999 : 999999,
                Stcd = pos,
            },
            ValDtls = BuildValDtls(invoice, isExport),
            ItemList = items,
            // NIC requires ExpDtls for EXPWOP / EXPWP supply types. Without it
            // NIC sandbox returns a generic 5002 on the export shipment fields.
            // RefClm = "N" means no refund claim (default for EXPWOP).
            ExpDtls = isExport ? new
            {
                ShipBNo = "NA",
                ShipBDt = invoice.InvoiceDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                Port = "INMUN1",
                RefClm = "N",
                ForCur = "USD",
                CntCode = "US",
            } : null,
        };
    }

    // ValDtls must equal the sum of line-level amounts (else NIC 2270). For
    // exports we recompute IGST per line from rate, so totals also recompute.
    private static object BuildValDtls(InvoiceResponse invoice, bool isExport)
    {
        if (!isExport)
        {
            return new
            {
                AssVal = (double)Round(invoice.TaxableValue),
                IgstVal = (double)Round(invoice.IGST),
                CgstVal = (double)Round(invoice.CGST),
                SgstVal = (double)Round(invoice.SGST),
                TotInvVal = (double)Round(invoice.TotalAmount),
            };
        }
        var assVal = invoice.Lines.Count > 0
            ? Round(invoice.Lines.Sum(l => Round(l.TaxableValue)))
            : Round(invoice.TaxableValue);
        var igstVal = invoice.Lines.Count > 0
            ? Round(invoice.Lines.Sum(l => l.GstRate > 0 ? Round(Round(l.TaxableValue) * l.GstRate / 100m) : 0m))
            : Round(invoice.IGST);
        return new
        {
            AssVal = (double)assVal,
            IgstVal = (double)igstVal,
            CgstVal = 0.0,
            SgstVal = 0.0,
            TotInvVal = (double)Round(assVal + igstVal),
        };
    }

    private static bool LooksLikeGstin(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length != 15) return false;
        return char.IsDigit(s[0]) && char.IsDigit(s[1]);
    }

    // State code = first two digits of the party GSTIN. No tenant-specific
    // fallback: a missing/malformed GSTIN is a configuration error, not a cue to
    // assume any particular state — fail loud so it's fixed rather than filing a
    // wrong-state payload for whichever tenant this is.
    private static string StateCode(string? gstin)
        => !string.IsNullOrWhiteSpace(gstin) && gstin.Length >= 2 && char.IsDigit(gstin[0]) && char.IsDigit(gstin[1])
            ? gstin[..2]
            : throw new InvalidOperationException(
                "Cannot derive the state code: the GSTIN is missing or malformed. Configure a valid 15-character GSTIN for this tenant/company before generating e-invoice / e-way bill payloads.");

    private static int ParsePin(string? pin, string stateCode)
    {
        var digits = new string((pin ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length == 6 && int.TryParse(digits, out var p) ? p : 999999;
    }

    private static decimal Round(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    // Canonical city for a state code — used in sandbox mode where the
    // company's real address may not match the sandbox GSTIN's state.
    private static string SandboxLocForState(string stateCode) => stateCode switch
    {
        "29" => "Bengaluru",
        "32" => "Kochi",
        "27" => "Mumbai",
        "07" => "New Delhi",
        _ => "TestCity",
    };

    // Valid PIN for the state's region.
    private static int SandboxPinForState(string stateCode) => stateCode switch
    {
        "29" => 560001,
        "32" => 682001,
        "27" => 400001,
        "07" => 110001,
        _ => 110001,
    };
}

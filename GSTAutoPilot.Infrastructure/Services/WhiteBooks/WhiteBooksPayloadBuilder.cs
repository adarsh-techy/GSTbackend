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
                Pin = ResolveSellerPin(company, sellerStateCode),
                Stcd = sellerStateCode,
            },
            BuyerDtls = new
            {
                Gstin = buyerGstin,
                LglNm = string.IsNullOrWhiteSpace(invoice.PartyName) ? "Buyer" : invoice.PartyName,
                Pos = pos,
                // The buyer's real address from the CarolERP customer master when
                // it is there, falling back to the place of supply. NIC enforces a
                // minimum length of 3 on Addr1 and Loc — the old "NA" placeholder
                // (2 chars) failed with 5002 on export bills with no place of
                // supply — so any default has to be 3 characters or more.
                Addr1 = Trim(FirstNonBlank(
                    invoice.PartyAddress1,
                    invoice.PlaceOfSupply,
                    isExport ? "Foreign" : "NotSpecified"), 100),
                Loc = Trim(FirstNonBlank(
                    invoice.PartyAddress2,
                    invoice.PartyAddress1,
                    invoice.PlaceOfSupply,
                    isExport ? "Foreign" : "NotSpecified"), 50),
                // NIC rejects a recipient PIN of 999999 on a B2B invoice (2274).
                // An export buyer is outside India and 999999 is the value NIC
                // expects there, so it is kept for exports only.
                Pin = isExport ? 999999 : ParsePin(invoice.PartyPinCode, pos),
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
            var assVal = Round(invoice.TaxableValue);
            var igst = Round(invoice.IGST);
            var cgst = Round(invoice.CGST);
            var sgst = Round(invoice.SGST);
            var totInv = Round(invoice.TotalAmount);

            // NIC recomputes the invoice total as
            //     AssVal + taxes + OthChrg - Discount + RndOffAmt
            // and refuses anything that does not balance (2189). KSCC's totals
            // frequently do not: on the CC/SB series the ERP's total is lower than
            // taxable + tax by consistently 10% of the taxable value — an
            // invoice-level reduction that the outward stored procedure does not
            // report in any column of its own (Discount and RoundOff both come
            // through as zero). 273 of 616 invoices for April 2026 are affected.
            //
            // The amount is therefore derived from the arithmetic itself: whatever
            // is missing from the total has to be declared, or the government will
            // not accept the invoice. A material gap is a discount; a rupee or two
            // either way is a rounding adjustment, which NIC expects in RndOffAmt.
            // RndOffAmt is signed the opposite way to the gap because it ADDS to
            // the total where Discount subtracts.
            var gap = Round(assVal + igst + cgst + sgst - totInv);
            var discount = gap > RoundingTolerance ? gap : 0m;
            var rndOff = discount == 0m ? -gap : 0m;

            return new
            {
                AssVal = (double)assVal,
                IgstVal = (double)igst,
                CgstVal = (double)cgst,
                SgstVal = (double)sgst,
                Discount = (double)discount,
                RndOffAmt = (double)rndOff,
                TotInvVal = (double)totInv,
            };
        }
        var expAssVal = invoice.Lines.Count > 0
            ? Round(invoice.Lines.Sum(l => Round(l.TaxableValue)))
            : Round(invoice.TaxableValue);
        var expIgstVal = invoice.Lines.Count > 0
            ? Round(invoice.Lines.Sum(l => l.GstRate > 0 ? Round(Round(l.TaxableValue) * l.GstRate / 100m) : 0m))
            : Round(invoice.IGST);
        // Exports recompute the total from their own parts, so it balances by
        // construction and needs no discount or rounding entry.
        return new
        {
            AssVal = (double)expAssVal,
            IgstVal = (double)expIgstVal,
            CgstVal = 0.0,
            SgstVal = 0.0,
            TotInvVal = (double)Round(expAssVal + expIgstVal),
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

    /// <summary>
    /// The seller PIN, taken from the dedicated field when it is set and otherwise
    /// recovered from the free-text address lines.
    /// </summary>
    /// <remarks>
    /// NIC rejects a supplier PIN of 999999 outright (error 2276), so every
    /// e-invoice failed for KSCC: the company record has no PinCode, yet the real
    /// PIN is sitting in the address as "ALAPPUZHA - 688 001, KERALA, INDIA".
    /// Indian PINs are commonly written with a space after the third digit, so
    /// "688 001" has to be recognised as well as "688001".
    /// </remarks>
    private static int ResolveSellerPin(CompanyDto company, string stateCode)
    {
        var explicitPin = ParsePin(company.PinCode, stateCode);
        if (explicitPin != 999999) return explicitPin;

        foreach (var line in new[] { company.Address2, company.Address3, company.Address1 })
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Three digits, an optional single space, then three digits. A leading
            // zero is not a valid Indian PIN, which also keeps this off house
            // numbers such as "P.B. No. 191".
            var m = System.Text.RegularExpressions.Regex.Match(line, @"\b([1-9]\d{2})\s?(\d{3})\b");
            if (m.Success && int.TryParse(m.Groups[1].Value + m.Groups[2].Value, out var fromAddress))
            {
                return fromAddress;
            }
        }
        return 999999;
    }

    // Above this, a shortfall in the invoice total is treated as a discount; at or
    // below it, as a rounding adjustment. NIC itself tolerates ±1 on the total.
    private const decimal RoundingTolerance = 2m;

    private static string FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? string.Empty;

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

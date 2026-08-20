using System.Globalization;
using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Infrastructure.Services.EwbApi;

// Maps an InvoiceResponse (CarolERP-sourced) + company master + transport
// details into the NIC e-Way Bill JSON (genewaybill, v1.03) that WhiteBooks
// proxies to the EWB portal. Mirrors WhiteBooksPayloadBuilder (e-Invoice): only
// mandatory blocks are populated, exports map the buyer to URP / state 96.
internal static class EWayBillPayloadBuilder
{
    // transMode: 1=Road, 2=Rail, 3=Air, 4=Ship (NIC codes).
    private static string TransModeCode(string? mode) => mode switch
    {
        EWayBillModeRail => "2",
        EWayBillModeAir => "3",
        EWayBillModeShip => "4",
        _ => "1",
    };
    private const string EWayBillModeRail = "Rail";
    private const string EWayBillModeAir = "Air";
    private const string EWayBillModeShip = "Ship";

    public static object Build(
        InvoiceResponse invoice,
        CompanyDto company,
        string sellerGstin,
        decimal distanceKm,
        string mode,
        string? transporterId,
        string? transporterName,
        string? vehicleNumber)
    {
        var isExport = !LooksLikeGstin(invoice.PartyGSTIN);
        var buyerGstin = LooksLikeGstin(invoice.PartyGSTIN) ? invoice.PartyGSTIN! : "URP";
        var fromStateCode = StateCodeInt(sellerGstin);
        // Export / unregistered buyer -> "other territory" state 96, PIN 999999.
        var toStateCode = isExport ? 96 : (LooksLikeGstin(buyerGstin) ? StateCodeInt(buyerGstin) : fromStateCode);
        var toPin = isExport ? 999999 : 999999;

        var items = new List<object>();
        if (invoice.Lines.Count > 0)
        {
            foreach (var l in invoice.Lines)
            {
                var ass = Round(l.TaxableValue);
                var igst = isExport && l.GstRate > 0 ? Round(ass * l.GstRate / 100m) : Round(l.IGST);
                items.Add(new
                {
                    productName = string.IsNullOrWhiteSpace(l.Description) ? "Item" : Trim(l.Description, 100),
                    productDesc = string.IsNullOrWhiteSpace(l.Description) ? "Item" : Trim(l.Description, 100),
                    hsnCode = HsnInt(l.HSNCode),
                    quantity = (double)l.Quantity,
                    qtyUnit = "NOS",
                    taxableAmount = (double)ass,
                    sgstRate = isExport ? 0d : (double)HalfRate(l.GstRate, l.CGST, l.SGST, isCgstSgst: true),
                    cgstRate = isExport ? 0d : (double)HalfRate(l.GstRate, l.CGST, l.SGST, isCgstSgst: true),
                    igstRate = isExport || l.IGST > 0 ? (double)l.GstRate : 0d,
                    cessRate = 0d,
                });
                _ = igst; // line-level tax totals roll up in the value block below
            }
        }
        else
        {
            items.Add(new
            {
                productName = "Goods",
                productDesc = "Goods",
                hsnCode = 0,
                quantity = 1.0,
                qtyUnit = "NOS",
                taxableAmount = (double)Round(invoice.TaxableValue),
                sgstRate = 0d,
                cgstRate = 0d,
                igstRate = 0d,
                cessRate = 0d,
            });
        }

        var assVal = Round(invoice.TaxableValue);
        var igstVal = isExport
            ? (invoice.Lines.Count > 0 ? Round(invoice.Lines.Sum(l => l.GstRate > 0 ? Round(Round(l.TaxableValue) * l.GstRate / 100m) : 0m)) : Round(invoice.IGST))
            : Round(invoice.IGST);
        var cgstVal = isExport ? 0m : Round(invoice.CGST);
        var sgstVal = isExport ? 0m : Round(invoice.SGST);
        var totInv = isExport ? Round(assVal + igstVal) : Round(invoice.TotalAmount);

        var dist = (int)Math.Max(0m, Math.Round(distanceKm, MidpointRounding.AwayFromZero));

        return new
        {
            supplyType = "O",
            subSupplyType = "1",       // Supply
            docType = "INV",
            docNo = invoice.InvoiceNumber,
            docDate = invoice.InvoiceDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),

            fromGstin = sellerGstin,
            fromTrdName = string.IsNullOrWhiteSpace(company.CompanyName) ? "Seller" : Trim(company.CompanyName, 100),
            fromAddr1 = Trim(company.Address1 ?? "NotSpecified", 120),
            fromAddr2 = Trim(company.Address2 ?? "", 120),
            fromPlace = Trim(company.Address3 ?? company.Address2 ?? "NotSpecified", 50),
            fromPincode = ParsePin(company.PinCode),
            fromStateCode,
            actFromStateCode = fromStateCode,

            toGstin = buyerGstin,
            toTrdName = string.IsNullOrWhiteSpace(invoice.PartyName) ? "Buyer" : Trim(invoice.PartyName, 100),
            toAddr1 = Trim(string.IsNullOrWhiteSpace(invoice.PlaceOfSupply) ? (isExport ? "Foreign" : "NotSpecified") : invoice.PlaceOfSupply, 120),
            toAddr2 = "",
            toPlace = Trim(string.IsNullOrWhiteSpace(invoice.PlaceOfSupply) ? (isExport ? "Foreign" : "NotSpecified") : invoice.PlaceOfSupply, 50),
            toPincode = toPin,
            toStateCode,
            actToStateCode = toStateCode,

            transactionType = 1,       // Regular
            totalValue = (double)assVal,
            cgstValue = (double)cgstVal,
            sgstValue = (double)sgstVal,
            igstValue = (double)igstVal,
            cessValue = 0d,
            totInvValue = (double)totInv,

            transporterId = transporterId ?? "",
            transporterName = transporterName ?? "",
            transDocNo = "",
            transDocDate = "",
            transMode = TransModeCode(mode),
            transDistance = dist.ToString(CultureInfo.InvariantCulture),
            vehicleNo = (vehicleNumber ?? "").Replace(" ", "").ToUpperInvariant(),
            vehicleType = "R",         // R=Regular

            itemList = items,
        };
    }

    private static decimal HalfRate(decimal gstRate, decimal cgst, decimal sgst, bool isCgstSgst)
        => isCgstSgst ? Round(gstRate / 2m) : 0m;

    private static bool LooksLikeGstin(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length != 15) return false;
        return char.IsDigit(s[0]) && char.IsDigit(s[1]);
    }

    private static int StateCodeInt(string? gstin)
        => !string.IsNullOrWhiteSpace(gstin) && gstin.Length >= 2 && int.TryParse(gstin[..2], out var sc) ? sc : 32;

    private static int ParsePin(string? pin)
    {
        var digits = new string((pin ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length == 6 && int.TryParse(digits, out var p) ? p : 999999;
    }

    private static int HsnInt(string? hsn)
    {
        var digits = new string((hsn ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var h) ? h : 0;
    }

    private static decimal Round(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}

using System.Globalization;
using System.Text.Json;
using GSTAutoPilot.Domain.Entities;

namespace GSTAutoPilot.Infrastructure.Services;

// Parses the NIC GSTR-2B JSON (as returned by the WhiteBooks GSP /gstr2b/all
// call) into flat GSTR2B records. One record per invoice / credit-note / import
// row, with the line items summed.
//
// The NIC payload nests everything under `docdata`, which WhiteBooks wraps in a
// `data` (and sometimes `data.gstr2b`) envelope, so we locate `docdata` rather
// than assuming a fixed path. Per-section parsing is defensive: a malformed
// supplier/invoice is skipped, not fatal, so one bad row can't lose the rest.
//
//   docdata.b2b[]  -> { ctin, trdnm, inv[] { inum, dt, items[]{ txval,igst,cgst,sgst } } }
//   docdata.cdnr[] -> { ctin, trdnm, nt[]  { ntnum, dt, typ(C/D), items[]{...} } }
//   docdata.impg[] -> { benum, bedt, portcd, txval, igst }
public static class Gstr2bJsonParser
{
    public static List<GSTR2B> Parse(string json, Guid tenantId, string filingPeriod, DateTime fetchedOn)
    {
        var result = new List<GSTR2B>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        using var doc = JsonDocument.Parse(json);
        if (!TryFindDocData(doc.RootElement, out var docdata)) return result;

        // Supplier invoices + their amendments (b2ba carries the revised values
        // for a prior period, so it reconciles like b2b).
        ParseInvoiceSection(docdata, "b2b", Gstr2bRecordType.B2B, tenantId, filingPeriod, fetchedOn, result);
        ParseInvoiceSection(docdata, "b2ba", Gstr2bRecordType.B2BA, tenantId, filingPeriod, fetchedOn, result);
        ParseNoteSection(docdata, "cdnr", Gstr2bRecordType.CDNR, tenantId, filingPeriod, fetchedOn, result);
        ParseNoteSection(docdata, "cdnra", Gstr2bRecordType.CDNRA, tenantId, filingPeriod, fetchedOn, result);
        ParseImpg(docdata, "impg", tenantId, filingPeriod, fetchedOn, result);
        ParseImpg(docdata, "impgsez", tenantId, filingPeriod, fetchedOn, result); // SEZ imports -> IMPG
        ParseIsd(docdata, tenantId, filingPeriod, fetchedOn, result);
        return result;
    }

    // Number of files the 2B is split across (multi-file pull). Looks for a
    // numeric `fc` / `filecount` anywhere in the envelope; defaults to 1.
    public static int ExtractFileCount(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 1;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var queue = new Queue<JsonElement>();
            queue.Enqueue(doc.RootElement);
            var budget = 5000;
            while (queue.Count > 0 && budget-- > 0)
            {
                var el = queue.Dequeue();
                if (el.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in el.EnumerateObject())
                    {
                        if ((prop.NameEquals("fc") || prop.NameEquals("filecount") || prop.NameEquals("file_count"))
                            && TryInt(prop.Value, out var fc) && fc >= 1)
                            return Math.Min(fc, 50);
                        if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                            queue.Enqueue(prop.Value);
                    }
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray())
                        if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                            queue.Enqueue(item);
                }
            }
        }
        catch (JsonException) { /* fall through */ }
        return 1;
    }

    private static bool TryInt(JsonElement v, out int n)
    {
        n = 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out n)) return true;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return true;
        return false;
    }

    // GSTR-2B per-document ITC availability. GSTN carries `itcavl` ("Y"/"N")
    // (some payloads spell it `itc_avl`) on each invoice/note, with `rsn` giving
    // the reason when unavailable — e.g. PoS-rule supplies or section 16(4)
    // time-barred credit. Absent => available. Returns (eligible, reasonOrNull).
    private static (bool Eligible, string? Reason) ItcEligibility(JsonElement doc)
    {
        var avl = Str(doc, "itcavl");
        if (string.IsNullOrEmpty(avl)) avl = Str(doc, "itc_avl");
        var eligible = !string.Equals(avl, "N", StringComparison.OrdinalIgnoreCase);
        if (eligible) return (true, null);
        var reason = Str(doc, "rsn");
        return (false, string.IsNullOrWhiteSpace(reason) ? "Ineligible per GSTR-2B" : reason);
    }

    // b2b / b2ba — supplier invoices grouped by ctin, with an inv[] array.
    private static void ParseInvoiceSection(JsonElement docdata, string section, string recordType, Guid tenantId, string period, DateTime fetchedOn, List<GSTR2B> sink)
    {
        if (!docdata.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var supplier in arr.EnumerateArray())
        {
            var ctin = Str(supplier, "ctin");
            var trdnm = Str(supplier, "trdnm");
            if (!supplier.TryGetProperty("inv", out var invs) || invs.ValueKind != JsonValueKind.Array) continue;
            foreach (var inv in invs.EnumerateArray())
            {
                var (txval, igst, cgst, sgst) = SumItems(inv);
                var (eligible, reason) = ItcEligibility(inv);
                sink.Add(new GSTR2B
                {
                    TenantId = tenantId,
                    SupplierGSTIN = ctin,
                    SupplierName = trdnm,
                    InvoiceNo = Str(inv, "inum"),
                    InvoiceDate = NicDate(Str(inv, "dt")),
                    TaxableAmount = txval,
                    IGSTAmount = igst,
                    CGSTAmount = cgst,
                    SGSTAmount = sgst,
                    FilingPeriod = period,
                    FetchedOn = fetchedOn,
                    RecordType = recordType,
                    IsItcEligible = eligible,
                    ItcIneligibleReason = reason,
                });
            }
        }
    }

    // cdnr / cdnra — supplier credit/debit notes grouped by ctin, with an nt[] array.
    private static void ParseNoteSection(JsonElement docdata, string section, string recordType, Guid tenantId, string period, DateTime fetchedOn, List<GSTR2B> sink)
    {
        if (!docdata.TryGetProperty(section, out var cdnr) || cdnr.ValueKind != JsonValueKind.Array) return;
        foreach (var supplier in cdnr.EnumerateArray())
        {
            var ctin = Str(supplier, "ctin");
            var trdnm = Str(supplier, "trdnm");
            if (!supplier.TryGetProperty("nt", out var notes) || notes.ValueKind != JsonValueKind.Array) continue;
            foreach (var note in notes.EnumerateArray())
            {
                var (txval, igst, cgst, sgst) = SumItems(note);
                // typ "C" (credit) reduces ITC; store amounts negative so the
                // 2B totals net correctly. "D" (debit) stays positive.
                var sign = string.Equals(Str(note, "typ"), "C", StringComparison.OrdinalIgnoreCase) ? -1m : 1m;
                var (eligible, reason) = ItcEligibility(note);
                sink.Add(new GSTR2B
                {
                    TenantId = tenantId,
                    SupplierGSTIN = ctin,
                    SupplierName = trdnm,
                    InvoiceNo = Str(note, "ntnum"),
                    InvoiceDate = NicDate(Str(note, "dt")),
                    TaxableAmount = sign * txval,
                    IGSTAmount = sign * igst,
                    CGSTAmount = sign * cgst,
                    SGSTAmount = sign * sgst,
                    FilingPeriod = period,
                    FetchedOn = fetchedOn,
                    RecordType = recordType,
                    IsItcEligible = eligible,
                    ItcIneligibleReason = reason,
                });
            }
        }
    }

    // isd / isda — Input Service Distributor credit. Each distributor (ctin) has
    // a document list carrying distributed igst/cgst/sgst (no taxable value).
    private static void ParseIsd(JsonElement docdata, Guid tenantId, string period, DateTime fetchedOn, List<GSTR2B> sink)
    {
        foreach (var section in new[] { "isd", "isda" })
        {
            if (!docdata.TryGetProperty(section, out var isd) || isd.ValueKind != JsonValueKind.Array) continue;
            foreach (var dist in isd.EnumerateArray())
            {
                var ctin = Str(dist, "ctin");
                var trdnm = Str(dist, "trdnm");
                var docs = FirstArray(dist, "doclist", "docs", "isddocs", "inv");
                if (docs is not { } list) continue;
                foreach (var d in list.EnumerateArray())
                {
                    sink.Add(new GSTR2B
                    {
                        TenantId = tenantId,
                        SupplierGSTIN = ctin,
                        SupplierName = string.IsNullOrWhiteSpace(trdnm) ? "ISD" : trdnm,
                        InvoiceNo = Str(d, "docnum") is { Length: > 0 } dn ? dn : Str(d, "inum"),
                        InvoiceDate = NicDate(Str(d, "docdt") is { Length: > 0 } dt ? dt : Str(d, "dt")),
                        TaxableAmount = Dec(d, "txval"),
                        IGSTAmount = IsdAmt(d, "igst"),
                        CGSTAmount = IsdAmt(d, "cgst"),
                        SGSTAmount = IsdAmt(d, "sgst"),
                        FilingPeriod = period,
                        FetchedOn = fetchedOn,
                        RecordType = Gstr2bRecordType.ISD,
                    });
                }
            }
        }
    }

    // ISD docs carry the distributed credit as either `igst` or `itc_igst`
    // (payload variants). Prefer the itc_-prefixed value; never sum both.
    private static decimal IsdAmt(JsonElement d, string baseName)
    {
        var itc = Dec(d, "itc_" + baseName);
        return itc != 0m ? itc : Dec(d, baseName);
    }

    private static JsonElement? FirstArray(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array)
                return v;
        return null;
    }

    private static void ParseImpg(JsonElement docdata, string section, Guid tenantId, string period, DateTime fetchedOn, List<GSTR2B> sink)
    {
        if (!docdata.TryGetProperty(section, out var impg) || impg.ValueKind != JsonValueKind.Array) return;
        foreach (var row in impg.EnumerateArray())
        {
            sink.Add(new GSTR2B
            {
                TenantId = tenantId,
                SupplierGSTIN = "IMPORT",
                SupplierName = Str(row, "portcd") is { Length: > 0 } p ? $"Import ({p})" : "Import of goods",
                InvoiceNo = Str(row, "benum"),
                InvoiceDate = NicDate(Str(row, "bedt")),
                TaxableAmount = Dec(row, "txval"),
                IGSTAmount = Dec(row, "igst"),
                CGSTAmount = 0m,
                SGSTAmount = 0m,
                FilingPeriod = period,
                FetchedOn = fetchedOn,
                RecordType = Gstr2bRecordType.IMPG,
            });
        }
    }

    // Sum the `items` array of an invoice / note. Each item carries
    // txval/igst/cgst/sgst directly in GSTR-2B (unlike GSTR-2A's itm_det nesting,
    // which we also tolerate).
    private static (decimal Txval, decimal Igst, decimal Cgst, decimal Sgst) SumItems(JsonElement invOrNote)
    {
        decimal txval = 0, igst = 0, cgst = 0, sgst = 0;
        if (!invOrNote.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return (txval, igst, cgst, sgst);
        foreach (var raw in items.EnumerateArray())
        {
            var item = raw.TryGetProperty("itm_det", out var det) ? det : raw;
            txval += Dec(item, "txval");
            igst += Dec(item, "igst");
            cgst += Dec(item, "cgst");
            sgst += Dec(item, "sgst");
        }
        return (txval, igst, cgst, sgst);
    }

    // Locate the `docdata` object anywhere in the envelope (root / data /
    // data.gstr2b / status-wrapped). Breadth-first, capped so a pathological
    // payload can't spin.
    private static bool TryFindDocData(JsonElement root, out JsonElement docdata)
    {
        var queue = new Queue<JsonElement>();
        queue.Enqueue(root);
        var budget = 5000;
        while (queue.Count > 0 && budget-- > 0)
        {
            var el = queue.Dequeue();
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.NameEquals("docdata") && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        docdata = prop.Value;
                        return true;
                    }
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        queue.Enqueue(prop.Value);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        queue.Enqueue(item);
            }
        }
        docdata = default;
        return false;
    }

    private static string Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty
               : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : string.Empty)
            : string.Empty;

    private static decimal Dec(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return 0m;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)) return s;
        return 0m;
    }

    // NIC dates are dd-mm-yyyy; tolerate a couple of variants, default to today.
    private static DateTime NicDate(string raw)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string[] formats = { "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "yyyy-MM-dd" };
            if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
        }
        return DateTime.UtcNow.Date;
    }
}

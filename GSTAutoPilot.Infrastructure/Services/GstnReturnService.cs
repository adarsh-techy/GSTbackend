using System.Text.Json;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Exceptions;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

// Builds GSTN portal-schema GSTR-1 / GSTR-3B JSON from the same computed data
// the on-screen reports use. Keys are emitted via SnakeCaseLower so PascalCase
// properties land as the GSTN keys (Ctin->ctin, InvTyp->inv_typ, HsnSc->hsn_sc).
public class GstnReturnService : IGstnReturnService
{
    public static readonly JsonSerializerOptions GstnJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly IInvoiceService _invoiceService;
    private readonly IGstr3bService _gstr3bService;
    private readonly CarolERPDbContext _carol;
    private readonly TenantDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public GstnReturnService(
        IInvoiceService invoiceService,
        IGstr3bService gstr3bService,
        CarolERPDbContext carol,
        TenantDbContext db,
        IHttpContextAccessor httpContextAccessor)
    {
        _invoiceService = invoiceService;
        _gstr3bService = gstr3bService;
        _carol = carol;
        _db = db;
        _httpContextAccessor = httpContextAccessor;
    }

    public string Serialize(object gstnModel) => JsonSerializer.Serialize(gstnModel, GstnJsonOptions);

    public async Task<Gstr1Json> BuildGstr1Async(int year, int month, CancellationToken cancellationToken = default)
    {
        var gstin = await ResolveSellerGstinAsync(cancellationToken);
        var sellerState = StateCode(gstin) ?? "00";
        var invoices = await _invoiceService.ListAsync(year, month, cancellationToken);
        var tables = await _invoiceService.GetGstr1TablesAsync(year, month, cancellationToken);

        var json = new Gstr1Json { Gstin = gstin, Fp = $"{month:D2}{year:D4}" };

        // Every invoice must end up in exactly one table. Each branch below
        // records what it took so the tie-out at the end can prove nothing was
        // left behind; reference identity, because BillId is only unique within
        // one of the SP's two source tables.
        var reported = new HashSet<InvoiceResponse>(ReferenceEqualityComparer.Instance);
        List<InvoiceResponse> Take(Func<InvoiceResponse, bool> predicate)
        {
            var taken = invoices.Where(predicate).ToList();
            foreach (var inv in taken) reported.Add(inv);
            return taken;
        }

        // B2B — grouped by counter-party GSTIN, invoice-wise, items by rate.
        var b2b = Take(i => i.Section == "B2B" && IsGstin(i.PartyGSTIN))
            .GroupBy(i => i.PartyGSTIN.Trim().ToUpperInvariant())
            .Select(g => new Gstr1B2bCtin
            {
                Ctin = g.Key,
                Inv = g.Select(inv => new Gstr1B2bInv
                {
                    Inum = inv.InvoiceNumber,
                    Idt = Fmt(inv.InvoiceDate),
                    Val = R(inv.TotalAmount),
                    Pos = StateCode(inv.PartyGSTIN) ?? sellerState,
                    Itms = BuildItems(inv),
                }).ToList(),
            }).ToList();
        if (b2b.Count > 0) json.B2b = b2b;

        // B2CL — inter-state unregistered, invoice-wise, grouped by place of supply.
        var b2cl = Take(i => i.Section == "B2CL")
            .GroupBy(i => UnregPos(i, sellerState))
            .Select(g => new Gstr1B2clPos
            {
                Pos = g.Key,
                Inv = g.Select(inv => new Gstr1B2clInv
                {
                    Inum = inv.InvoiceNumber,
                    Idt = Fmt(inv.InvoiceDate),
                    Val = R(inv.TotalAmount),
                    Itms = BuildItems(inv),
                }).ToList(),
            }).ToList();
        if (b2cl.Count > 0) json.B2cl = b2cl;

        // B2CS — rate-wise summary keyed by (supply type, place of supply, rate),
        // net of intra-state B2C credit/debit notes (GSTN folds those into b2cs
        // rather than cdnur, which is inter-state only).
        var b2csAgg = new Dictionary<(string Ty, string Pos, decimal Rt), Gstr1B2cs>();
        void AddB2cs(InvoiceResponse inv, decimal sign, bool intraOnly)
        {
            foreach (var line in EffectiveLines(inv))
            {
                var inter = line.IGST != 0m;
                if (intraOnly && inter) continue;
                var ty = inter ? "INTER" : "INTRA";
                var pos = inter ? UnregPos(inv, sellerState) : sellerState;
                var key = (ty, pos, line.Rate);
                if (!b2csAgg.TryGetValue(key, out var row))
                {
                    row = new Gstr1B2cs { SplyTy = ty, Pos = pos, Rt = line.Rate };
                    b2csAgg[key] = row;
                }
                row.Txval += sign * line.Txval; row.Iamt += sign * line.IGST;
                row.Camt += sign * line.CGST; row.Samt += sign * line.SGST;
            }
        }
        foreach (var inv in Take(i => i.Section == "B2CS"))
            AddB2cs(inv, 1m, intraOnly: false);
        // (Unregistered CDN notes also fold into b2cs / cdnur below; b2cs is
        // finalized after that pass.)

        // CDNR — credit/debit notes to registered parties.
        var cdnr = Take(i => i.Section == "CDN" && IsGstin(i.PartyGSTIN))
            .GroupBy(i => i.PartyGSTIN.Trim().ToUpperInvariant())
            .Select(g => new Gstr1CdnrCtin
            {
                Ctin = g.Key,
                Nt = g.Select(inv => new Gstr1CdnrNote
                {
                    Ntty = inv.GstCategory == GstDocumentCatalog.SalesDebitNote ? "D" : "C",
                    NtNum = inv.InvoiceNumber,
                    NtDt = Fmt(inv.InvoiceDate),
                    Val = R(Math.Abs(inv.TotalAmount)),
                    Pos = StateCode(inv.PartyGSTIN) ?? sellerState,
                    Itms = BuildItems(inv, absolute: true),
                }).ToList(),
            }).ToList();
        if (cdnr.Count > 0) json.Cdnr = cdnr;

        // CDNUR — credit/debit notes to UNREGISTERED parties (no GSTIN), routed by
        // TAX SHAPE (party labels like "Export" are unreliable — a note carrying
        // CGST/SGST is domestic intra-state regardless of label):
        //   intra-state (CGST/SGST) -> net b2cs;
        //   inter-state (IGST)      -> cdnur EXPWP (export party) / B2CL (domestic);
        //   no tax + export party   -> cdnur EXPWOP;
        //   no tax + domestic       -> skipped (immaterial nil note).
        var cdnur = new List<Gstr1Cdnur>();
        foreach (var inv in Take(i => i.Section == "CDN" && !IsGstin(i.PartyGSTIN)))
        {
            var sign = inv.GstCategory == GstDocumentCatalog.CreditNote ? -1m : 1m;
            if (inv.CGST != 0m || inv.SGST != 0m)
            {
                AddB2cs(inv, sign, intraOnly: true); // domestic intra-state
                continue;
            }
            var export = IsExportParty(inv);
            if (inv.IGST != 0m)
                cdnur.Add(MakeCdnur(inv, export ? "EXPWP" : "B2CL", export ? "96" : UnregPos(inv, sellerState)));
            else if (export)
                cdnur.Add(MakeCdnur(inv, "EXPWOP", "96"));
        }
        if (b2csAgg.Count > 0) json.B2cs = b2csAgg.Values.Select(Round).ToList();
        if (cdnur.Count > 0) json.Cdnur = cdnur;

        // EXP — exports, grouped by with/without payment of tax.
        var exp = Take(i => i.Section == "Export")
            .GroupBy(i => i.IGST != 0m ? "WPAY" : "WOPAY")
            .Select(g => new Gstr1ExpGroup
            {
                ExpTyp = g.Key,
                Inv = g.Select(inv => new Gstr1ExpInv
                {
                    Inum = inv.InvoiceNumber,
                    Idt = Fmt(inv.InvoiceDate),
                    Val = R(inv.TotalAmount),
                    Itms = EffectiveLines(inv).Select(l => new Gstr1ExpItem
                    {
                        Txval = R(l.Txval), Rt = l.Rate, Iamt = R(l.IGST), Csamt = 0m,
                    }).ToList(),
                }).ToList(),
            }).ToList();
        if (exp.Count > 0) json.Exp = exp;

        // HSN summary (Table 12), split into B2B / B2C sub-tables (mandatory from
        // the May-2025 tax period). Each sub-table is numbered from 1.
        if (tables.Hsn.Count > 0)
        {
            static Gstr1HsnData Map(Gstr1HsnRow h, int n) => new()
            {
                Num = n,
                HsnSc = h.HSNCode,
                Desc = h.Description,
                Uqc = string.IsNullOrWhiteSpace(h.UQC) ? "OTH" : h.UQC.Split('-')[0],
                Qty = h.Quantity,
                Txval = h.TaxableValue,
                Iamt = h.IGST,
                Camt = h.CGST,
                Samt = h.SGST,
                Csamt = h.Cess,
                Rt = h.Rate,
            };
            var nb = 1; var nc = 1;
            json.Hsn = new Gstr1HsnBlock
            {
                HsnB2b = tables.Hsn.Where(h => h.SupplyType == "B2B").Select(h => Map(h, nb++)).ToList(),
                HsnB2c = tables.Hsn.Where(h => h.SupplyType != "B2B").Select(h => Map(h, nc++)).ToList(),
            };
        }

        // Documents issued (Table 13) — broken into the actual contiguous serial
        // ranges of the issued document numbers (per number prefix).
        var docDet = new List<Gstr1DocDet>();
        var docNumByType = new Dictionary<string, int> { ["Invoices for outward supply"] = 1, ["Debit Notes"] = 4, ["Credit Notes"] = 5 };
        foreach (var d in tables.DocsIssued.Where(d => d.Count > 0))
        {
            var nums = invoices
                .Where(i => DocBucket(i) == d.DocType)
                .Select(i => i.InvoiceNumber)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            var ranges = BuildDocRanges(nums);
            if (ranges.Count == 0) continue;
            docDet.Add(new Gstr1DocDet
            {
                DocNum = docNumByType.TryGetValue(d.DocType, out var dn) ? dn : 1,
                Docs = ranges,
            });
        }
        if (docDet.Count > 0) json.DocIssue = new Gstr1DocIssue { DocDet = docDet };

        AssertEveryInvoiceReported(invoices, reported);
        return json;
    }

    // Tie-out: refuse to hand back a GSTR-1 that doesn't account for every
    // invoice in the period. A return that is quietly short of turnover files
    // clean, understates output tax, and shows up later as a GSTR-1 vs 3B
    // mismatch — so this throws rather than logs.
    //
    // The one legitimate omission is an unregistered credit/debit note carrying
    // no tax at all: cdnur has nowhere to put it and it moves no money, so the
    // builder drops it on purpose (see the CDNUR block).
    private static void AssertEveryInvoiceReported(
        IReadOnlyList<InvoiceResponse> invoices, HashSet<InvoiceResponse> reported)
    {
        const int MaxNamed = 20;

        var missing = invoices
            .Where(i => !reported.Contains(i))
            .Where(i => !IsIntentionallySkipped(i))
            .ToList();
        if (missing.Count == 0) return;

        var names = missing
            .Take(MaxNamed)
            .Select(i => string.IsNullOrWhiteSpace(i.InvoiceNumber) ? $"Bill {i.BillId}" : i.InvoiceNumber)
            .ToList();
        throw new Gstr1UnreportedInvoicesException(
            names,
            missing.Count,
            decimal.Round(missing.Sum(i => i.TaxableValue), 2),
            decimal.Round(missing.Sum(i => i.IGST + i.CGST + i.SGST), 2));
    }

    // A nil-value note to an unregistered party — deliberately not reported.
    private static bool IsIntentionallySkipped(InvoiceResponse inv)
        => inv.Section == "CDN"
        && !IsGstin(inv.PartyGSTIN)
        && inv.IGST == 0m && inv.CGST == 0m && inv.SGST == 0m
        && !IsExportParty(inv);

    public async Task<Gstr3bJson> BuildGstr3bAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        var gstin = await ResolveSellerGstinAsync(cancellationToken);
        var sellerState = StateCode(gstin) ?? "00";
        var r = await _gstr3bService.ComputeAsync(year, month, cancellationToken);
        var invoices = await _invoiceService.ListAsync(year, month, cancellationToken);
        var o = r.Section3_1_OutwardSupplies;
        var itc = r.Table4_Itc;

        // 4A5 "all other ITC" = net ITC minus import (4A1) minus reverse-charge
        // (4A3), reported GROSS of Sec 17(5) blocked credit (blocked is added
        // back here and reversed in 4B(1) below per Circular 170/02/2022, so the
        // 4C net is unchanged).
        var othIgst = R(itc.IGST - itc.ImportIgst - itc.ReverseChargeIGST + itc.BlockedIgst);
        var othCgst = R(itc.CGST - itc.ReverseChargeCGST + itc.BlockedCgst);
        var othSgst = R(itc.SGST - itc.ReverseChargeSGST + itc.BlockedSgst);

        // Table 3.2 — inter-state supplies to UNREGISTERED persons, by place of
        // supply (B2CL + inter-state B2CS). Composition/UIN aren't distinguishable
        // from the source, so they stay empty.
        var unregByPos = new Dictionary<string, Gstr3bPosSupply>();
        foreach (var inv in invoices.Where(i => (i.Section == "B2CL" || i.Section == "B2CS") && i.IGST != 0m))
        {
            var pos = UnregPos(inv, sellerState);
            if (!unregByPos.TryGetValue(pos, out var row))
            {
                row = new Gstr3bPosSupply { Pos = pos };
                unregByPos[pos] = row;
            }
            row.Txval += inv.TaxableValue; row.Iamt += inv.IGST;
        }
        foreach (var row in unregByPos.Values) { row.Txval = R(row.Txval); row.Iamt = R(row.Iamt); }

        // Table 4D(2): ineligible ITC under section 16(4) & PoS rules — the
        // GSTR-2B rows the portal flagged unavailable (itcavl "N") for this
        // period, summed per head. Informational: NOT part of the 4C credit
        // ledger. Zero until a real 2B carrying ineligible flags is fetched.
        var period = $"{year:D4}{month:D2}";
        var inelig = await _db.GSTR2BRecords.AsNoTracking()
            .Where(g => g.FilingPeriod == period && !g.IsItcEligible)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Igst = g.Sum(x => x.IGSTAmount),
                Cgst = g.Sum(x => x.CGSTAmount),
                Sgst = g.Sum(x => x.SGSTAmount),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Table 5 — exempt/nil-rated & non-GST inward, split inter/intra against
        // the seller's state (unknown supplier state => intra-state). Emitted only
        // when non-zero. Table 5.1 (interest/late fee) is a filing-time figure, so
        // it's left null (omitted) here.
        static (decimal Inter, decimal Intra) SplitByState(Dictionary<string, decimal> byState, string seller)
        {
            decimal inter = 0m, intra = 0m;
            foreach (var kv in byState)
            {
                if (!string.IsNullOrEmpty(kv.Key) && kv.Key != seller) inter += kv.Value; else intra += kv.Value;
            }
            return (inter, intra);
        }
        var (exInter, exIntra) = SplitByState(r.Table5_ExemptInward.ExemptNilByState, sellerState);
        var (ngInter, ngIntra) = SplitByState(r.Table5_ExemptInward.NonGstByState, sellerState);
        var isupRows = new List<Gstr3bInwardSupDetail>();
        if (exInter != 0m || exIntra != 0m)
            isupRows.Add(new Gstr3bInwardSupDetail { Ty = "GST", Inter = R(exInter), Intra = R(exIntra) });
        if (ngInter != 0m || ngIntra != 0m)
            isupRows.Add(new Gstr3bInwardSupDetail { Ty = "NONGST", Inter = R(ngInter), Intra = R(ngIntra) });
        var inwardSup = isupRows.Count > 0 ? new Gstr3bInwardSup { IsupDetails = isupRows } : null;

        return new Gstr3bJson
        {
            Gstin = gstin,
            RetPeriod = $"{month:D2}{year:D4}",
            SupDetails = new Gstr3bSupDetails
            {
                OsupDet = new Gstr3bTaxBlock { Txval = o.TaxableOutward.TaxableValue, Iamt = o.TaxableOutward.IGST, Camt = o.TaxableOutward.CGST, Samt = o.TaxableOutward.SGST },
                OsupZero = new Gstr3bZeroBlock { Txval = o.ZeroRated.TaxableValue, Iamt = o.ZeroRated.IGST },
                OsupNilExmp = new Gstr3bNilBlock { Txval = o.NilRatedExempt.TaxableValue },
                IsupRev = new Gstr3bTaxBlock { Txval = o.ReverseChargeInward.TaxableValue, Iamt = o.ReverseChargeInward.IGST, Camt = o.ReverseChargeInward.CGST, Samt = o.ReverseChargeInward.SGST },
                OsupNongst = new Gstr3bNilBlock { Txval = o.NonGstOutward.TaxableValue },
            },
            InterSup = new Gstr3bInterSup { UnregDetails = unregByPos.Values.OrderBy(p => p.Pos).ToList() },
            ItcElg = new Gstr3bItcElg
            {
                ItcAvl = new List<Gstr3bItcRow>
                {
                    new() { Ty = "IMPG", Iamt = itc.ImportIgst },
                    new() { Ty = "IMPS" },
                    new() { Ty = "ISRC", Iamt = itc.ReverseChargeIGST, Camt = itc.ReverseChargeCGST, Samt = itc.ReverseChargeSGST },
                    new() { Ty = "ISD" },
                    new() { Ty = "OTH", Iamt = othIgst, Camt = othCgst, Samt = othSgst },
                },
                // 4B reversals. 4B(1) "RUL" = as per rules 38/42/43 & section
                // 17(5) — carries the blocked ITC added back into 4A(5) above.
                // 4B(2) "OTH" = other (temporary, e.g. rule 37 / 180-day) — not
                // tracked from the books, reported nil.
                ItcRev = new List<Gstr3bItcRow>
                {
                    new() { Ty = "RUL", Iamt = R(itc.BlockedIgst), Camt = R(itc.BlockedCgst), Samt = R(itc.BlockedSgst) },
                    new() { Ty = "OTH" },
                },
                // 4C net = 4A available − 4B reversed. Blocked ITC cancels out
                // (added in 4A5, removed in 4B1), so net equals the eligible ITC.
                ItcNet = new Gstr3bItcNet { Iamt = itc.IGST, Camt = itc.CGST, Samt = itc.SGST },
                // 4D other details (informational, not part of the credit ledger).
                // 4D(1) "RUL" = ITC reclaimed (reversed under 4B(2) earlier) — not
                // tracked. 4D(2) "OTH" = ineligible ITC u/s 16(4) & PoS rules,
                // drawn from GSTR-2B rows the portal flagged unavailable.
                ItcInelg = new List<Gstr3bItcRow>
                {
                    new() { Ty = "RUL" },
                    new() { Ty = "OTH", Iamt = R(inelig?.Igst ?? 0m), Camt = R(inelig?.Cgst ?? 0m), Samt = R(inelig?.Sgst ?? 0m) },
                },
            },
            InwardSup = inwardSup, // Table 5 (null => omitted)
        };
    }

    // ----- helpers -----

    // One rate-grouped line of an invoice, in INR.
    private sealed record Eff(decimal Rate, decimal Txval, decimal IGST, decimal CGST, decimal SGST, decimal Cess);

    private static IEnumerable<Eff> EffectiveLines(InvoiceResponse inv)
    {
        if (inv.Lines.Count > 0)
        {
            // Group by the COMBINED rate, derived from amounts when the stored
            // line rate is 0 (intra-state / credit-note lines carry no rate).
            return inv.Lines
                .Select(l => (Rate: GstRateHelper.Effective(l.GstRate, l.TaxableValue, l.IGST, l.CGST, l.SGST), L: l))
                .GroupBy(x => x.Rate)
                .Select(g => new Eff(
                    g.Key,
                    R(g.Sum(x => x.L.TaxableValue)),
                    R(g.Sum(x => x.L.IGST)),
                    R(g.Sum(x => x.L.CGST)),
                    R(g.Sum(x => x.L.SGST)),
                    R(g.Sum(x => x.L.Cess))));
        }
        // Header-only fallback: one line at the implied rate (no cess data).
        var rate = GstRateHelper.Effective(0m, inv.TaxableValue, inv.IGST, inv.CGST, inv.SGST);
        return new[] { new Eff(rate, R(inv.TaxableValue), R(inv.IGST), R(inv.CGST), R(inv.SGST), 0m) };
    }

    private static List<Gstr1Item> BuildItems(InvoiceResponse inv, bool absolute = false)
    {
        var num = 1;
        decimal s(decimal v) => absolute ? Math.Abs(v) : v;
        return EffectiveLines(inv).Select(l => new Gstr1Item
        {
            Num = num++,
            ItmDet = new Gstr1ItemDet { Rt = l.Rate, Txval = s(l.Txval), Iamt = s(l.IGST), Camt = s(l.CGST), Samt = s(l.SGST), Csamt = s(l.Cess) },
        }).ToList();
    }

    private static Gstr1B2cs Round(Gstr1B2cs r)
    {
        r.Txval = R(r.Txval); r.Iamt = R(r.Iamt); r.Camt = R(r.Camt); r.Samt = R(r.Samt);
        return r;
    }

    // Break a set of document numbers into Table-13 serial ranges: parse the
    // trailing digits, group by the (prefix) before them, and emit one range per
    // run of CONSECUTIVE numbers (a gap starts a new range — we don't infer
    // cancellations, so cancel=0 / net_issue=totnum). Non-numeric numbers are
    // lumped into a single trailing range.
    private static List<Gstr1DocRange> BuildDocRanges(IEnumerable<string> docNumbers)
    {
        var parsed = docNumbers.Select(ParseDocNo).ToList();
        var ranges = new List<Gstr1DocRange>();
        var idx = 1;

        foreach (var grp in parsed.Where(p => p.Num.HasValue)
                     .GroupBy(p => p.Prefix)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = grp.OrderBy(p => p.Num!.Value).ToList();
            var runStart = 0;
            for (var i = 1; i <= ordered.Count; i++)
            {
                var brk = i == ordered.Count || ordered[i].Num!.Value != ordered[i - 1].Num!.Value + 1;
                if (!brk) continue;
                var total = i - runStart;
                ranges.Add(new Gstr1DocRange
                {
                    Num = idx++,
                    From = ordered[runStart].Full,
                    To = ordered[i - 1].Full,
                    Totnum = total,
                    Cancel = 0,
                    NetIssue = total,
                });
                runStart = i;
            }
        }

        var unparsed = parsed.Where(p => !p.Num.HasValue)
            .Select(p => p.Full)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unparsed.Count > 0)
        {
            ranges.Add(new Gstr1DocRange
            {
                Num = idx,
                From = unparsed.First(),
                To = unparsed.Last(),
                Totnum = unparsed.Count,
                Cancel = 0,
                NetIssue = unparsed.Count,
            });
        }
        return ranges;
    }

    // Split a document number into its (prefix, trailing-number). "8" -> ("", 8),
    // "INV/2026/001" -> ("INV/2026/", 1). Non-numeric trailing -> Num null.
    private static (string Prefix, long? Num, string Full) ParseDocNo(string raw)
    {
        var s = raw.Trim();
        var i = s.Length;
        while (i > 0 && char.IsDigit(s[i - 1])) i--;
        var digits = s[i..];
        if (digits.Length is 0 or > 18 || !long.TryParse(digits, out var n)) return (s, null, s);
        return (s[..i], n, s);
    }

    private static string DocBucket(InvoiceResponse i)
        => i.GstCategory == GstDocumentCatalog.CreditNote ? "Credit Notes"
         : i.GstCategory == GstDocumentCatalog.SalesDebitNote ? "Debit Notes"
         : "Invoices for outward supply";

    private async Task<string> ResolveSellerGstinAsync(CancellationToken ct)
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant;
        var gstin = tenant?.GSTIN ?? string.Empty;
        if (_carol.ActiveCompanyId is byte coId)
        {
            var groups = await _carol.CompanyGroupsAsync(ct);
            var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(coId));
            if (!string.IsNullOrWhiteSpace(group?.Gstin)) gstin = group!.Gstin;
        }
        return gstin.Trim().ToUpperInvariant();
    }

    // InvoiceService labels foreign/export parties' GSTIN as the literal
    // "Export" (PartyGstinLabel), so that's how we detect export-origin notes.
    private static bool IsExportParty(InvoiceResponse inv)
        => string.Equals(inv.PartyGSTIN, "Export", StringComparison.OrdinalIgnoreCase);

    private static Gstr1Cdnur MakeCdnur(InvoiceResponse inv, string typ, string pos) => new()
    {
        Ntty = inv.GstCategory == GstDocumentCatalog.SalesDebitNote ? "D" : "C",
        NtNum = inv.InvoiceNumber,
        NtDt = Fmt(inv.InvoiceDate),
        Typ = typ,
        Pos = pos,
        Val = R(Math.Abs(inv.TotalAmount)),
        Itms = BuildItems(inv, absolute: true),
    };

    // Place of supply for an unregistered / B2C row: the buyer's GSTIN state if
    // present, else the buyer's own state (CarolERP Account.StateId, plumbed as
    // PosStateCode), else the seller's state as a last resort. The middle step
    // is the fix for inter-state B2C, where there is no buyer GSTIN and POS used
    // to collapse to the seller's state.
    private static string UnregPos(InvoiceResponse inv, string sellerState)
        => StateCode(inv.PartyGSTIN)
           ?? (string.IsNullOrEmpty(inv.PosStateCode) ? null : inv.PosStateCode)
           ?? sellerState;

    private static decimal R(decimal v) => decimal.Round(v, 2);
    private static string Fmt(DateTime d) => d.ToString("dd-MM-yyyy");
    private static bool IsGstin(string? s) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length == 15;
    private static string? StateCode(string? gstin)
    {
        var g = gstin?.Trim();
        if (g is null || g.Length < 2 || !char.IsDigit(g[0]) || !char.IsDigit(g[1])) return null;
        return g[..2];
    }
}

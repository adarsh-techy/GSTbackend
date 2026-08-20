using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Domain.Tax;
using Microsoft.Extensions.Options;

namespace GSTAutoPilot.Infrastructure.Services;

public class Gstr3bService : IGstr3bService
{
    private readonly CarolDocumentReader _reader;
    private readonly IBillOfEntryService _billOfEntry;
    private readonly SpOutwardService _spOutward;
    private readonly SpInwardService _spInward;
    private readonly IReadOnlyList<string> _blockedPatterns;

    public Gstr3bService(CarolDocumentReader reader, IBillOfEntryService billOfEntry, SpOutwardService spOutward, SpInwardService spInward, IOptions<Sec175Options> sec175)
    {
        _reader = reader;
        _billOfEntry = billOfEntry;
        _spOutward = spOutward;
        _spInward = spInward;
        _blockedPatterns = (sec175.Value.BlockedAccountPatterns ?? new())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
    }

    public async Task<Gstr3bResponse> ComputeAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        // When per-tenant SPs are the data source (as GSTR-1 and recon already
        // do), compute 3B from them — the Document-Mapping reader returns nothing
        // for an SP-only tenant like KSCC. Requires BOTH directions on the SP;
        // partial-SP tenants (none today) fall through to the reader path.
        if (_spOutward.IsConfigured && _spInward.IsConfigured)
            return await ComputeFromSpAsync(year, month, cancellationToken);

        var period = $"{year:D4}{month:D2}";
        var outBundles = await _reader.ReadOutwardAsync(year, month, cancellationToken);

        // 3.1 outward breakdown. Sales credit notes (e.g. KSCC 910/1) net DOWN
        // output tax via a -1 sign. Classification:
        //   (b) zero-rated  = ExportSales category
        //   (c) nil/exempt  = taxable value but no tax (and not export)
        //   (a) taxable     = everything else outward
        var a = new Acc();      // 3.1(a)
        var zero = new Acc();   // 3.1(b)
        var nil = new Acc();    // 3.1(c)
        foreach (var b in outBundles)
        {
            var sign = GstDocumentCatalog.ReducesOutputTax(b.Header.GstCategory) ? -1m : 1m;
            var (t, i, c, s) = Figures(b);
            if (b.Header.GstCategory == GstDocumentCatalog.ExportSales) zero.Add(sign, t, i, c, s);
            else if (i == 0m && c == 0m && s == 0m) nil.Add(sign, t, 0m, 0m, 0m);
            else a.Add(sign, t, i, c, s);
        }

        // Classify inward documents:
        //   credit notes (ReducesItc)            -> net ITC DOWN (4A5, -1 sign),
        //                                           regardless of the RCM flag;
        //   reverse-charge (GstReverse != 0)      -> 3.1(d) liability + 4A3 ITC;
        //   everything else                       -> regular ITC (4A5).
        var allInward = await _reader.ReadInwardAsync(year, month, cancellationToken);
        var rcm = new Acc();
        var itcCount = 0;
        decimal itcTaxable = 0m, itcIgst = 0m, itcCgst = 0m, itcSgst = 0m;
        decimal blockedItc = 0m;
        // Sec 17(5) blocked ITC split by head, so it can be shown as a Table
        // 4B(1) reversal (Circular 170) instead of being silently dropped.
        decimal blockedIgst = 0m, blockedCgst = 0m, blockedSgst = 0m;
        // Table 5: exempt/nil-rated inward, grouped by supplier state so the JSON
        // builder can split inter/intra against the seller state.
        var exemptNilByState = new Dictionary<string, decimal>();
        foreach (var b in allInward)
        {
            var (t, i, c, s) = Figures(b);
            // Table 5 — exempt/nil-rated inward: has taxable value but no GST, is
            // not reverse-charge and not a credit/debit note. Carries no ITC, so
            // it's recorded here in addition to (not instead of) its ITC-branch
            // handling below, which nets zero tax anyway.
            if (!GstDocumentCatalog.ReducesItc(b.Header.GstCategory) && b.Header.GstReverse == 0
                && i == 0m && c == 0m && s == 0m && t != 0m)
            {
                var st = InwardSupplierState(b.Header);
                exemptNilByState[st] = exemptNilByState.GetValueOrDefault(st) + t;
            }
            if (GstDocumentCatalog.ReducesItc(b.Header.GstCategory))
            {
                itcTaxable -= t; itcIgst -= i; itcCgst -= c; itcSgst -= s; itcCount++;
            }
            else if (b.Header.GstReverse != 0)
            {
                rcm.Add(1m, t, i, c, s);
            }
            else
            {
                // Sec 17(5): move ITC booked to blocked-credit expense accounts
                // out of eligible ITC (930 journals only; no-op when no patterns
                // are configured). Captured per head (original minus eligible) so
                // it surfaces as the 4B(1) reversal rather than vanishing from 4A.
                var (oi, oc, os) = (i, c, s);
                (t, i, c, s) = EligibleFigures(b, t, i, c, s, ref blockedItc);
                blockedIgst += oi - i; blockedCgst += oc - c; blockedSgst += os - s;
                itcTaxable += t; itcIgst += i; itcCgst += c; itcSgst += s; itcCount++;
            }
        }
        // 4A3: ITC on reverse-charge inward — mirrors the 3.1(d) liability.
        itcTaxable += rcm.Tax; itcIgst += rcm.I; itcCgst += rcm.C; itcSgst += rcm.S;

        var outward = new OutwardSuppliesSection
        {
            InvoiceCount = outBundles.Count,
            TaxableValue = decimal.Round(a.Tax + zero.Tax + nil.Tax, 2),
            CGST = decimal.Round(a.C + zero.C, 2),
            SGST = decimal.Round(a.S + zero.S, 2),
            IGST = decimal.Round(a.I + zero.I, 2),
            TaxableOutward = a.ToLine(),
            ZeroRated = zero.ToLine(),
            NilRatedExempt = nil.ToLine(),
            ReverseChargeInward = rcm.ToLine(),
            NonGstOutward = new Gstr3bLine(),
        };

        // 4A1: import IGST paid at customs (manual Bills of Entry). CarolERP
        // import bills carry no IGST, so there's no overlap to double-count.
        var boe = await _billOfEntry.GetPeriodTotalsAsync(period, cancellationToken);

        var note = "ITC per books (CarolERP inward): 4A5 all-other + 4A1 import + 4A3 reverse-charge. Reconcile against GSTR-2B on the dashboard/Recon screen. 4B(1) carries Sec 17(5) blocked ITC (Circular 170); 4B(2) temporary reversals & 4D not modeled.";
        if (boe.Count > 0)
            note += $" Includes import IGST Rs.{boe.IGSTAmount:N2} from {boe.Count} Bill(s) of Entry (Table 4A1).";
        if (blockedItc > 0m)
            note += $" Rs.{blockedItc:N2} ITC on Sec 17(5) blocked-credit expense accounts reported in 4A5 and reversed in 4B(1).";

        var itc = new ItcSection
        {
            PurchaseCount = itcCount + rcm.Count + boe.Count,
            TaxableValue = decimal.Round(itcTaxable + boe.AssessableValue, 2),
            CGST = decimal.Round(itcCgst, 2),
            SGST = decimal.Round(itcSgst, 2),
            IGST = decimal.Round(itcIgst + boe.IGSTAmount, 2),
            ImportIgst = decimal.Round(boe.IGSTAmount, 2),
            ReverseChargeCGST = decimal.Round(rcm.C, 2),
            ReverseChargeSGST = decimal.Round(rcm.S, 2),
            ReverseChargeIGST = decimal.Round(rcm.I, 2),
            BlockedIgst = decimal.Round(blockedIgst, 2),
            BlockedCgst = decimal.Round(blockedCgst, 2),
            BlockedSgst = decimal.Round(blockedSgst, 2),
            Note = note,
        };

        // Output side of net payable = outward (3.1 a/b/c) + reverse-charge
        // liability (3.1 d); ITC side already includes the matching 4A3 RCM
        // credit, so RCM nets out when fully creditable.
        var calc = GstNetPayableCalculator.Compute(
            decimal.Round(outward.IGST + rcm.I, 2), decimal.Round(outward.CGST + rcm.C, 2), decimal.Round(outward.SGST + rcm.S, 2),
            itc.IGST, itc.CGST, itc.SGST);

        var netTax = new TaxLiabilitySummary
        {
            IGST = decimal.Round(calc.NetIGST, 2),
            CGST = decimal.Round(calc.NetCGST, 2),
            SGST = decimal.Round(calc.NetSGST, 2),
        };
        var carry = new CarryForwardSection
        {
            IGST = decimal.Round(calc.CarryIGST, 2),
            CGST = decimal.Round(calc.CarryCGST, 2),
            SGST = decimal.Round(calc.CarrySGST, 2),
        };
        carry.TotalCarryForward = carry.IGST + carry.CGST + carry.SGST;
        carry.Remarks = carry.TotalCarryForward > 0m
            ? "Excess ITC carried forward to next period"
            : "No ITC carry-forward this period";

        return new Gstr3bResponse
        {
            Period = $"{year:D4}-{month:D2}",
            Section3_1_OutwardSupplies = outward,
            Table4_Itc = itc,
            Table5_ExemptInward = new ExemptInwardSection
            {
                ExemptNilByState = exemptNilByState.ToDictionary(kv => kv.Key, kv => decimal.Round(kv.Value, 2)),
            },
            NetTaxPayable = netTax,
            CarryForward = carry,
        };
    }

    // SP-based GSTR-3B: 3.1 from the outward SP invoice list, Table 4 ITC + Table
    // 5 from the inward SP purchase list, 4A1 import from Bills of Entry. The SP
    // contract exposes no reverse-charge or Sec 17(5) flags, so 3.1(d) RCM
    // liability, 4A3 RCM ITC and 4B(1) blocked reversal are 0 on this path
    // (simpler than the reader path, but real vs. all-zeros with no mappings).
    private async Task<Gstr3bResponse> ComputeFromSpAsync(int year, int month, CancellationToken cancellationToken)
    {
        var period = $"{year:D4}{month:D2}";
        var invoices = await _spOutward.ListAsync(year, month, cancellationToken);
        var purchases = await _spInward.ListAsync(year, month, cancellationToken);
        var boe = await _billOfEntry.GetPeriodTotalsAsync(period, cancellationToken);

        // 3.1 outward. Credit notes (Section "CDN") net output tax DOWN.
        var a = new Acc(); var zero = new Acc(); var nil = new Acc();
        foreach (var inv in invoices)
        {
            var sign = string.Equals(inv.Section, "CDN", StringComparison.OrdinalIgnoreCase) ? -1m : 1m;
            var (t, i, c, s) = (inv.TaxableValue, inv.IGST, inv.CGST, inv.SGST);
            if (string.Equals(inv.Section, "Export", StringComparison.OrdinalIgnoreCase)) zero.Add(sign, t, i, c, s);
            else if (i == 0m && c == 0m && s == 0m) nil.Add(sign, t, 0m, 0m, 0m);
            else a.Add(sign, t, i, c, s);
        }

        var outward = new OutwardSuppliesSection
        {
            InvoiceCount = invoices.Count,
            TaxableValue = decimal.Round(a.Tax + zero.Tax + nil.Tax, 2),
            CGST = decimal.Round(a.C + zero.C, 2),
            SGST = decimal.Round(a.S + zero.S, 2),
            IGST = decimal.Round(a.I + zero.I, 2),
            TaxableOutward = a.ToLine(),
            ZeroRated = zero.ToLine(),
            NilRatedExempt = nil.ToLine(),
            ReverseChargeInward = new Gstr3bLine(),
            NonGstOutward = new Gstr3bLine(),
        };

        // Table 4 ITC (4A5 all-other) + Table 5 (exempt/nil inward). Credit notes
        // reduce ITC, debit notes increase it; exempt/nil purchases (taxable, no
        // GST) carry no ITC and go to Table 5. Portal-ineligible rows are excluded.
        decimal itcIgst = 0m, itcCgst = 0m, itcSgst = 0m, itcTaxable = 0m; int itcCount = 0;
        var exemptNilByState = new Dictionary<string, decimal>();
        foreach (var p in purchases)
        {
            var isCredit = p.BillCategory == "CreditNote";
            var isNote = isCredit || p.BillCategory == "DebitNote";
            var (t, i, c, s) = (p.TaxableAmount, p.IGSTAmount, p.CGSTAmount, p.SGSTAmount);
            if (!isNote && i == 0m && c == 0m && s == 0m && t != 0m)
            {
                var st = StateFromGstin(p.SupplierGSTIN);
                exemptNilByState[st] = exemptNilByState.GetValueOrDefault(st) + t;
                continue;
            }
            if (!p.IsITCEligible) continue; // portal-ineligible: not claimable
            var sign = isCredit ? -1m : 1m;
            itcTaxable += sign * t; itcIgst += sign * i; itcCgst += sign * c; itcSgst += sign * s; itcCount++;
        }

        var itc = new ItcSection
        {
            PurchaseCount = itcCount + boe.Count,
            TaxableValue = decimal.Round(itcTaxable + boe.AssessableValue, 2),
            CGST = decimal.Round(itcCgst, 2),
            SGST = decimal.Round(itcSgst, 2),
            IGST = decimal.Round(itcIgst + boe.IGSTAmount, 2),
            ImportIgst = decimal.Round(boe.IGSTAmount, 2),
            Note = "ITC from the inward SP (4A5 all-other) + 4A1 import from Bills of Entry. The SP exposes no reverse-charge/Sec 17(5) flags, so 4A3 RCM and 4B(1) blocked reversals are not modeled on this path. Reconcile against GSTR-2B on the Recon screen.",
        };
        if (boe.Count > 0)
            itc.Note += $" Includes import IGST Rs.{boe.IGSTAmount:N2} from {boe.Count} Bill(s) of Entry.";

        var calc = GstNetPayableCalculator.Compute(outward.IGST, outward.CGST, outward.SGST, itc.IGST, itc.CGST, itc.SGST);
        var netTax = new TaxLiabilitySummary { IGST = decimal.Round(calc.NetIGST, 2), CGST = decimal.Round(calc.NetCGST, 2), SGST = decimal.Round(calc.NetSGST, 2) };
        var carry = new CarryForwardSection { IGST = decimal.Round(calc.CarryIGST, 2), CGST = decimal.Round(calc.CarryCGST, 2), SGST = decimal.Round(calc.CarrySGST, 2) };
        carry.TotalCarryForward = carry.IGST + carry.CGST + carry.SGST;
        carry.Remarks = carry.TotalCarryForward > 0m ? "Excess ITC carried forward to next period" : "No ITC carry-forward this period";

        return new Gstr3bResponse
        {
            Period = $"{year:D4}-{month:D2}",
            Section3_1_OutwardSupplies = outward,
            Table4_Itc = itc,
            Table5_ExemptInward = new ExemptInwardSection
            {
                ExemptNilByState = exemptNilByState.ToDictionary(kv => kv.Key, kv => decimal.Round(kv.Value, 2)),
            },
            NetTaxPayable = netTax,
            CarryForward = carry,
        };
    }

    // 2-digit GST state code from a GSTIN string; "" when not a valid GSTIN.
    private static string StateFromGstin(string? gstin)
    {
        var g = (gstin ?? string.Empty).Trim().ToUpperInvariant();
        return g.Length == 15 && g.All(char.IsLetterOrDigit) ? g[..2] : string.Empty;
    }

    public async Task<Gstr3bTrendResponse> ComputeTrendAsync(string anchorPeriod, int months, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(anchorPeriod) || anchorPeriod.Length != 6
            || !int.TryParse(anchorPeriod.AsSpan(0, 4), out var year)
            || !int.TryParse(anchorPeriod.AsSpan(4, 2), out var month) || month < 1 || month > 12)
        {
            throw new ArgumentException("period must be in YYYYMM format (e.g. 202604).", nameof(anchorPeriod));
        }
        months = Math.Clamp(months, 1, 12);

        var anchor = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var response = new Gstr3bTrendResponse();
        // Oldest -> newest so the chart reads left to right.
        for (var i = months - 1; i >= 0; i--)
        {
            var p = anchor.AddMonths(-i);
            var g = await ComputeAsync(p.Year, p.Month, cancellationToken);
            response.Points.Add(new Gstr3bTrendPoint
            {
                Period = $"{p.Year:D4}{p.Month:D2}",
                OutputTax = g.Section3_1_OutwardSupplies.TotalGstCollected,
                Itc = g.Table4_Itc.TotalItcAvailable,
                NetPayable = g.NetTaxPayable.Total,
            });
        }
        return response;
    }

    // Taxable + tax figures for a bundle (taxable, igst, cgst, sgst).
    private static (decimal Tax, decimal Igst, decimal Cgst, decimal Sgst) Figures(CarolDocBundle b)
        => (Taxable(b),
            b.Lines.Sum(l => l.IgstAmount),
            b.Lines.Sum(l => l.CgstAmount),
            b.Lines.Sum(l => l.SgstAmount));

    // Supplier's 2-digit GST state code from the inward bill's GSTIN; "" when
    // there's no valid GSTIN (unregistered supplier — treated as intra-state).
    private static string InwardSupplierState(CarolDocHeader header)
    {
        var g = (header.GstNo ?? string.Empty).Trim().ToUpperInvariant();
        return g.Length == 15 && g.All(char.IsLetterOrDigit) ? g[..2] : string.Empty;
    }

    // Sec 17(5) exclusion: for GeneralExpense (930) journals, remove ITC on lines
    // booked to blocked-credit expense accounts (matched on the line's account
    // name, which the 930 reader emits as the line Description). Returns the
    // eligible figures and adds the removed ITC to blockedItc. No-op (returns the
    // input unchanged) when no patterns are configured or the document isn't a
    // general-expense journal.
    private (decimal Tax, decimal Igst, decimal Cgst, decimal Sgst) EligibleFigures(
        CarolDocBundle b, decimal tax, decimal igst, decimal cgst, decimal sgst, ref decimal blockedItc)
    {
        if (_blockedPatterns.Count == 0 || b.Header.GstCategory != GstDocumentCatalog.GeneralExpense)
            return (tax, igst, cgst, sgst);

        foreach (var l in b.Lines)
        {
            if (!IsBlocked(l.Description)) continue;
            tax -= l.TaxableInr; igst -= l.IgstAmount; cgst -= l.CgstAmount; sgst -= l.SgstAmount;
            blockedItc += l.IgstAmount + l.CgstAmount + l.SgstAmount;
        }
        return (tax, igst, cgst, sgst);
    }

    private bool IsBlocked(string? accountName)
        => !string.IsNullOrWhiteSpace(accountName)
           && _blockedPatterns.Any(p => accountName.Contains(p, StringComparison.OrdinalIgnoreCase));

    // Signed accumulator for a GSTR-3B 3.1 sub-row.
    private sealed class Acc
    {
        public decimal Tax, I, C, S;
        public int Count;
        public void Add(decimal sign, decimal tax, decimal i, decimal c, decimal s)
        {
            Tax += sign * tax; I += sign * i; C += sign * c; S += sign * s; Count++;
        }
        public Gstr3bLine ToLine() => new()
        {
            TaxableValue = decimal.Round(Tax, 2),
            IGST = decimal.Round(I, 2),
            CGST = decimal.Round(C, 2),
            SGST = decimal.Round(S, 2),
        };
    }

    // Taxable value for a bundle: line total when lines exist, else the header
    // amount in INR (header-only / not-yet-implemented line schema fallback).
    private static decimal Taxable(CarolDocBundle b)
    {
        var lineTaxable = b.Lines.Sum(l => l.TaxableInr);
        if (lineTaxable != 0m) return lineTaxable;
        // Journal/expense categories legitimately have bills with no GST lines —
        // don't fall back to the header total (it would inflate the ITC base).
        if (GstDocumentCatalog.LinesOnlyTaxable(b.Header.GstCategory)) return 0m;
        var rate = b.Header.ExchRate == 0 ? 1m : b.Header.ExchRate;
        return b.Header.TotalAmt * rate;
    }
}

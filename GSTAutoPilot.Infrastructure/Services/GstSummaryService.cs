using System.Globalization;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Domain.Tax;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class GstSummaryService : IGstSummaryService
{
    private readonly TenantDbContext _db;
    private readonly CarolERPDbContext _carol;
    private readonly CarolDocumentReader _reader;
    private readonly SpOutwardService _spOutward;
    private readonly IReconService _reconService;
    private readonly IBillOfEntryService _billOfEntry;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public GstSummaryService(
        TenantDbContext db,
        CarolERPDbContext carol,
        CarolDocumentReader reader,
        SpOutwardService spOutward,
        IReconService reconService,
        IBillOfEntryService billOfEntry,
        IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _carol = carol;
        _reader = reader;
        _spOutward = spOutward;
        _reconService = reconService;
        _billOfEntry = billOfEntry;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<GstSummaryResponse> GetSummaryAsync(string period, CancellationToken cancellationToken = default)
    {
        if (!TryParsePeriod(period, out var year, out var month))
        {
            throw new ArgumentException("period must be in YYYYMM format (e.g. 202604).", nameof(period));
        }

        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved.");

        // Fast check: Only run full recon if results have never been computed for this period
        var hasReconResults = await _db.ReconResults.AsNoTracking()
            .AnyAsync(r => r.FilingPeriod == period, cancellationToken);
        if (!hasReconResults)
        {
            await _reconService.RunAsync(period, cancellationToken);
        }

        var periodStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = periodStart.AddMonths(1);

        var output = await ComputeOutputGstAsync(year, month, cancellationToken);

        // B2B section only — IMPG (import) IGST is credited via the BoE fold
        // below, so including IMPG here would double-count it.
        var twoB = await _db.GSTR2BRecords.AsNoTracking()
            .Where(g => g.FilingPeriod == period && g.RecordType != Gstr2bRecordType.IMPG)
            .ToListAsync(cancellationToken);

        var recon = await _db.ReconResults.AsNoTracking()
            .Where(r => r.FilingPeriod == period)
            .ToListAsync(cancellationToken);

        // Only fetch book rows if there are matched rows needing ITC verification
        var hasMatched = recon.Any(r => r.Status == ReconStatus.Matched);
        var bookRows = hasMatched
            ? await ((ReconService)_reconService).FetchBookRowsAsync(periodStart, periodEnd, cancellationToken)
            : Array.Empty<ReconService.BookRow>();

        var twoBByKey = twoB
            .GroupBy(t => MakeKey(t.SupplierGSTIN, t.InvoiceNo))
            .ToDictionary(g => g.Key, g => g.First());
        var booksByKey = bookRows
            .GroupBy(b => MakeKey(b.SupplierGSTIN, b.InvoiceNo))
            .ToDictionary(g => g.Key, g => g.First());

        // Import IGST from customs Bills of Entry — auto-eligible ITC (Table
        // 4A1 / 2B IMPG), separate from the 2B-vs-books supplier recon.
        var boe = await _billOfEntry.GetPeriodTotalsAsync(period, cancellationToken);

        var itc = new ItcFromGstr2BSection
        {
            TotalITC = twoB.Sum(GstFromTwoB) + boe.IGSTAmount,
            ImportIgst = decimal.Round(boe.IGSTAmount, 2),
        };

        decimal eligibleIGST = 0m, eligibleCGST = 0m, eligibleSGST = 0m;
        var reconSummary = new ReconSummary();

        foreach (var row in recon)
        {
            var key = MakeKey(row.SupplierGSTIN, row.InvoiceNo);
            switch (row.Status)
            {
                case ReconStatus.Matched:
                    reconSummary.Matched++;
                    twoBByKey.TryGetValue(key, out var matchedTwoB);
                    if (matchedTwoB is not null)
                    {
                        itc.MatchedITC += GstFromTwoB(matchedTwoB);
                    }
                    booksByKey.TryGetValue(key, out var matchedBook);
                    // Claim ITC only when BOTH sides allow it: the books flag it
                    // eligible AND GSTR-2B does not mark it unavailable (itcavl
                    // "N" — PoS rule, section 16(4) time-bar, etc.).
                    if (matchedBook is not null && matchedBook.IsITCEligible
                        && (matchedTwoB?.IsItcEligible ?? true))
                    {
                        eligibleIGST += matchedBook.IGSTAmount;
                        eligibleCGST += matchedBook.CGSTAmount;
                        eligibleSGST += matchedBook.SGSTAmount;
                    }
                    else if (matchedTwoB is { IsItcEligible: false })
                    {
                        // Matched, but GSTN says the credit is not available.
                        itc.IneligibleITC += GstFromTwoB(matchedTwoB);
                    }
                    break;
                case ReconStatus.Mismatch:
                    reconSummary.Mismatch++;
                    if (twoBByKey.TryGetValue(key, out var mismatchTwoB))
                    {
                        itc.MismatchedITC += GstFromTwoB(mismatchTwoB);
                    }
                    break;
                case ReconStatus.Missing:
                    reconSummary.Missing++;
                    if (twoBByKey.TryGetValue(key, out var missingTwoB))
                    {
                        itc.MissingITC += GstFromTwoB(missingTwoB);
                    }
                    break;
                case ReconStatus.NotIn2B:
                    reconSummary.NotIn2B++;
                    break;
            }
        }

        // Import IGST is fully creditable without supplier recon — add to the
        // eligible pool so net payable and EligibleITC both reflect it.
        eligibleIGST += boe.IGSTAmount;

        itc.EligibleITC = decimal.Round(eligibleIGST + eligibleCGST + eligibleSGST, 2);
        itc.TotalITC = decimal.Round(itc.TotalITC, 2);
        itc.MatchedITC = decimal.Round(itc.MatchedITC, 2);
        itc.MismatchedITC = decimal.Round(itc.MismatchedITC, 2);
        itc.MissingITC = decimal.Round(itc.MissingITC, 2);
        itc.IneligibleITC = decimal.Round(itc.IneligibleITC, 2);

        var calc = GstNetPayableCalculator.Compute(
            output.IGST, output.CGST, output.SGST,
            eligibleIGST, eligibleCGST, eligibleSGST);

        var net = new NetTaxPayableSection
        {
            IGST = decimal.Round(calc.NetIGST, 2),
            CGST = decimal.Round(calc.NetCGST, 2),
            SGST = decimal.Round(calc.NetSGST, 2),
        };
        net.Total = net.IGST + net.CGST + net.SGST;

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

        // When the user has picked a specific company (GST group) in the
        // sidebar, the dashboard should reflect THAT group's GSTIN, not the
        // tenant-level GSTIN from master. Tenants with multiple GST
        // registrations (e.g. KSCC Group 1 = main coir + Group 2 = mattress)
        // would otherwise always display the main GST regardless of selection.
        var displayGstin = tenant.GSTIN;
        if (_carol.ActiveCompanyId is byte activeCoId)
        {
            var groups = await _carol.CompanyGroupsAsync(cancellationToken);
            var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(activeCoId));
            if (!string.IsNullOrWhiteSpace(group?.Gstin)) displayGstin = group!.Gstin;
        }

        return new GstSummaryResponse
        {
            Period = period,
            TenantGSTIN = displayGstin,
            OutputGST = output,
            ItcFromGSTR2B = itc,
            ReconSummary = reconSummary,
            NetTaxPayable = net,
            CarryForward = carry,
            AIRemarks = BuildRemarks(reconSummary, itc, twoB.Count, output.InvoiceCount),
        };
    }

    private async Task<OutputGstSection> ComputeOutputGstAsync(int year, int month, CancellationToken cancellationToken)
    {
        // Prefer the outward SP when configured (as GSTR-1/3B/recon do) — the
        // Document-Mapping reader returns nothing for an SP-only tenant like KSCC.
        if (_spOutward.IsConfigured)
            return OutputFromInvoices(await _spOutward.ListAsync(year, month, cancellationToken));

        var bundles = await _reader.ReadOutwardAsync(year, month, cancellationToken);

        decimal taxable = 0m, igst = 0m, cgst = 0m, sgst = 0m;
        foreach (var b in bundles)
        {
            // Sales credit notes net DOWN output tax (stored positive → -1 sign).
            var sign = GstDocumentCatalog.ReducesOutputTax(b.Header.GstCategory) ? -1m : 1m;
            var lineTaxable = b.Lines.Sum(l => l.TaxableInr);
            // Fallback to header total when no line data is available.
            taxable += sign * (lineTaxable != 0m
                ? lineTaxable
                : b.Header.TotalAmt * (b.Header.ExchRate == 0 ? 1m : b.Header.ExchRate));
            igst += sign * b.Lines.Sum(l => l.IgstAmount);
            cgst += sign * b.Lines.Sum(l => l.CgstAmount);
            sgst += sign * b.Lines.Sum(l => l.SgstAmount);
        }

        var section = new OutputGstSection
        {
            TaxableAmount = decimal.Round(taxable, 2),
            IGST = decimal.Round(igst, 2),
            CGST = decimal.Round(cgst, 2),
            SGST = decimal.Round(sgst, 2),
            InvoiceCount = bundles.Count,
        };
        section.TotalGST = section.IGST + section.CGST + section.SGST;
        return section;
    }

    // Output GST from the outward SP invoice list. Credit notes (Section "CDN")
    // net output tax DOWN, matching the reader path's sign handling.
    private static OutputGstSection OutputFromInvoices(IReadOnlyList<InvoiceResponse> invoices)
    {
        decimal taxable = 0m, igst = 0m, cgst = 0m, sgst = 0m;
        foreach (var inv in invoices)
        {
            var sign = string.Equals(inv.Section, "CDN", StringComparison.OrdinalIgnoreCase) ? -1m : 1m;
            taxable += sign * inv.TaxableValue;
            igst += sign * inv.IGST;
            cgst += sign * inv.CGST;
            sgst += sign * inv.SGST;
        }
        var section = new OutputGstSection
        {
            TaxableAmount = decimal.Round(taxable, 2),
            IGST = decimal.Round(igst, 2),
            CGST = decimal.Round(cgst, 2),
            SGST = decimal.Round(sgst, 2),
            InvoiceCount = invoices.Count,
        };
        section.TotalGST = section.IGST + section.CGST + section.SGST;
        return section;
    }

    private static string BuildRemarks(ReconSummary summary, ItcFromGstr2BSection itc, int twoBCount, int invoiceCount)
    {
        if (invoiceCount == 0 && twoBCount == 0 && summary.Total == 0)
        {
            return "No invoices, GSTR-2B records, or recon data for this period. Capture sales/purchases and run /api/gstr2b/fetch first.";
        }

        var parts = new List<string>();

        if (twoBCount == 0)
        {
            parts.Add($"No GSTR-2B data for this period — run /api/gstr2b/fetch/{{period}} to pull it.");
        }

        if (summary.Mismatch > 0)
        {
            parts.Add($"{summary.Mismatch} mismatch(es) found. Eligible ITC reduced by Rs.{Format(itc.MismatchedITC)}.");
        }

        if (summary.Missing > 0)
        {
            parts.Add($"{summary.Missing} GSTR-2B invoice(s) missing from books — capture them to claim Rs.{Format(itc.MissingITC)} ITC.");
        }

        if (summary.NotIn2B > 0)
        {
            parts.Add($"{summary.NotIn2B} booked purchase(s) not yet in GSTR-2B — ITC may be deferred.");
        }

        if (itc.ImportIgst > 0m)
        {
            parts.Add($"Import IGST Rs.{Format(itc.ImportIgst)} from Bill(s) of Entry added to eligible ITC.");
        }

        if (summary.Mismatch == 0 && summary.Missing == 0 && summary.Matched > 0)
        {
            parts.Add($"All {summary.Matched} matched entries reconcile cleanly. Eligible ITC: Rs.{Format(itc.EligibleITC)}.");
        }

        parts.Add(summary.Mismatch > 0
            ? "File GSTR-3B after resolving mismatches."
            : "Safe to file GSTR-3B.");

        return string.Join(" ", parts);
    }

    private static decimal GstFromTwoB(GSTR2B g) => g.IGSTAmount + g.CGSTAmount + g.SGSTAmount;

    private static string Format(decimal amount) =>
        amount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));

    // Same key as ReconService so eligible-ITC lookups line up with recon rows.
    private static string MakeKey(string gstin, string invoiceNo) =>
        ReconService.MakeKey(gstin, invoiceNo);

    private static bool TryParsePeriod(string period, out int year, out int month)
    {
        year = 0;
        month = 0;
        if (string.IsNullOrWhiteSpace(period) || period.Length != 6) return false;
        if (!int.TryParse(period.AsSpan(0, 4), out year)) return false;
        if (!int.TryParse(period.AsSpan(4, 2), out month)) return false;
        return month >= 1 && month <= 12;
    }
}

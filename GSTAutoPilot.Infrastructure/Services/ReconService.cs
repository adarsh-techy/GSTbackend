using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.CarolERP.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class ReconService : IReconService
{
    private const decimal AmountTolerance = 1m;
    private readonly TenantDbContext _db;
    private readonly CarolERPDbContext _carol;
    private readonly CarolDocumentReader _reader;
    private readonly SpInwardService _spInward;
    private readonly IBillOfEntryService _billOfEntry;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ReconService(TenantDbContext db, CarolERPDbContext carol, CarolDocumentReader reader, SpInwardService spInward, IBillOfEntryService billOfEntry, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _carol = carol;
        _reader = reader;
        _spInward = spInward;
        _billOfEntry = billOfEntry;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<ReconRunResponse> RunAsync(string filingPeriod, CancellationToken cancellationToken = default)
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved.");

        if (!TryParsePeriod(filingPeriod, out var year, out var month))
        {
            throw new ArgumentException("filingPeriod must be in YYYYMM format (e.g. 202604).", nameof(filingPeriod));
        }

        var periodStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = periodStart.AddMonths(1);

        var allTwoB = await _db.GSTR2BRecords
            .AsNoTracking()
            .Where(g => g.FilingPeriod == filingPeriod)
            .ToListAsync(cancellationToken);
        // Split 2B into its sections: B2B (supplier invoices, incl. B2BA
        // amendments), CDNR (supplier credit/debit notes, incl. CDNRA) and IMPG
        // (import of goods). Each reconciles against its book-side counterpart.
        // ISD credit is left out (not reconciled invoice-for-invoice).
        var twoBRows = allTwoB.Where(g => g.RecordType is Gstr2bRecordType.B2B or Gstr2bRecordType.B2BA).ToList();
        var twoBCdnr = allTwoB.Where(g => g.RecordType is Gstr2bRecordType.CDNR or Gstr2bRecordType.CDNRA).ToList();
        var twoBImpg = allTwoB.Where(g => g.RecordType == Gstr2bRecordType.IMPG).ToList();

        var bookRows = await FetchBookRowsAsync(periodStart, periodEnd, cancellationToken);
        // Book-side credit notes (e.g. purchase credit notes, DocType 900) match
        // 2B CDNR; everything else matches 2B B2B.
        var bookB2B = bookRows.Where(b => !b.IsCreditNote);
        var bookCdn = bookRows.Where(b => b.IsCreditNote);

        var stale = await _db.ReconResults
            .Where(r => r.FilingPeriod == filingPeriod)
            .ToListAsync(cancellationToken);
        if (stale.Count > 0)
        {
            _db.ReconResults.RemoveRange(stale);
        }

        var results = new List<ReconResult>();
        var now = DateTime.UtcNow;

        results.AddRange(MatchInvoiceSection(tenant, ReconSectionType.B2B, filingPeriod, now,
            twoBRows.Select(TwoBSide), bookB2B.Select(BookSide)));
        results.AddRange(MatchInvoiceSection(tenant, ReconSectionType.CDNR, filingPeriod, now,
            twoBCdnr.Select(TwoBSide), bookCdn.Select(BookSide)));

        // IMPG (import of goods): GSTR-2B IMPG section vs the Bill-of-Entry
        // register, matched on BoE number. Default ReconResult.Section is B2B,
        // so only these IMPG rows are tagged IMPG.
        var boeRows = await _billOfEntry.ListAsync(filingPeriod, cancellationToken);
        var impgBooks = boeRows.GroupBy(b => Norm(b.BoENumber)).ToDictionary(g => g.Key, g => g.First());
        var impgTwoB = twoBImpg.GroupBy(t => Norm(t.InvoiceNo)).ToDictionary(g => g.Key, g => g.First());

        foreach (var t2b in twoBImpg)
        {
            var key = Norm(t2b.InvoiceNo);
            var twoBTotal = t2b.TaxableAmount + t2b.IGSTAmount;
            if (impgBooks.TryGetValue(key, out var boe))
            {
                var booksTotal = boe.AssessableValue + boe.IGSTAmount;
                var diff = decimal.Round(twoBTotal - booksTotal, 2);
                var isMatch = Math.Abs(diff) <= AmountTolerance;
                results.Add(new ReconResult
                {
                    TenantId = tenant.TenantId,
                    Section = ReconSectionType.IMPG,
                    SupplierGSTIN = "IMPORT",
                    SupplierName = t2b.SupplierName,
                    InvoiceNo = t2b.InvoiceNo,
                    GSTR2BAmount = twoBTotal,
                    BooksAmount = booksTotal,
                    Difference = diff,
                    Status = isMatch ? ReconStatus.Matched : ReconStatus.Mismatch,
                    AIRemarks = isMatch
                        ? "Import IGST matches the Bill of Entry; ITC can be claimed."
                        : $"Import value mismatch of {diff:N2}; verify Bill of Entry vs GSTR-2B IMPG.",
                    FilingPeriod = filingPeriod,
                    CreatedOn = now,
                });
            }
            else
            {
                results.Add(new ReconResult
                {
                    TenantId = tenant.TenantId,
                    Section = ReconSectionType.IMPG,
                    SupplierGSTIN = "IMPORT",
                    SupplierName = t2b.SupplierName,
                    InvoiceNo = t2b.InvoiceNo,
                    GSTR2BAmount = twoBTotal,
                    BooksAmount = 0m,
                    Difference = twoBTotal,
                    Status = ReconStatus.Missing,
                    AIRemarks = "Import in GSTR-2B IMPG but no Bill of Entry captured; add it to claim ITC.",
                    FilingPeriod = filingPeriod,
                    CreatedOn = now,
                });
            }
        }

        foreach (var boe in boeRows)
        {
            var key = Norm(boe.BoENumber);
            if (impgTwoB.ContainsKey(key)) continue;
            var booksTotal = boe.AssessableValue + boe.IGSTAmount;
            results.Add(new ReconResult
            {
                TenantId = tenant.TenantId,
                Section = ReconSectionType.IMPG,
                SupplierGSTIN = "IMPORT",
                SupplierName = string.IsNullOrWhiteSpace(boe.SupplierName) ? (boe.PortCode ?? "Import") : boe.SupplierName,
                InvoiceNo = boe.BoENumber,
                GSTR2BAmount = 0m,
                BooksAmount = booksTotal,
                Difference = -booksTotal,
                Status = ReconStatus.NotIn2B,
                AIRemarks = "Bill of Entry not yet in GSTR-2B IMPG; refresh GSTR-2B to pull it.",
                FilingPeriod = filingPeriod,
                CreatedOn = now,
            });
        }

        _db.ReconResults.AddRange(results);
        await _db.SaveChangesAsync(cancellationToken);

        var summary = BuildSummary(results);

        return new ReconRunResponse
        {
            FilingPeriod = filingPeriod,
            RowsProcessed = results.Count,
            Summary = summary,
            RanOn = now,
        };
    }

    public async Task<ReconReportResponse> GetResultsAsync(string filingPeriod, CancellationToken cancellationToken = default)
    {
        if (!TryParsePeriod(filingPeriod, out _, out _))
        {
            throw new ArgumentException("filingPeriod must be in YYYYMM format (e.g. 202604).", nameof(filingPeriod));
        }

        var rows = await _db.ReconResults
            .AsNoTracking()
            .Where(r => r.FilingPeriod == filingPeriod)
            .OrderBy(r => r.Status)
            .ThenBy(r => r.SupplierGSTIN)
            .ToListAsync(cancellationToken);

        return new ReconReportResponse
        {
            FilingPeriod = filingPeriod,
            Summary = BuildSummary(rows),
            Rows = rows.Select(MapToResponse).ToList(),
        };
    }

    internal async Task<IReadOnlyList<BookRow>> FetchBookRowsAsync(
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken)
    {
        // When an inward SP is configured it is the source of truth for
        // purchases (it owns all GST logic), so recon's book side must come from
        // the SAME place the Purchase tab does — otherwise the grid and the
        // reconciliation would disagree. Falls through to the Document-Mapping
        // reader only when no inward SP is set.
        // The inward SP tags each row via its Bill_Cat column
        // (-> PurchaseInvoiceResponse.BillCategory): "Purchase" reconciles
        // against 2B B2B; "CreditNote"/"DebitNote" reconcile against 2B CDNR.
        // A credit note REDUCES ITC so it's signed negative (mirroring how the
        // 2B CDNR parser negates credit notes); a debit note increases ITC and
        // stays positive. Both are flagged IsCreditNote so they land in CDNR.
        if (_spInward.IsConfigured)
        {
            var purchases = await _spInward.ListAsync(periodStart.Year, periodStart.Month, cancellationToken);
            return purchases.Select(p =>
            {
                var isNote = p.BillCategory is "CreditNote" or "DebitNote";
                var sign = p.BillCategory == "CreditNote" ? -1m : 1m;
                return new BookRow
                {
                    BillId = 0, // not used by matching; SP invoices key on GSTIN+InvoiceNo
                    SupplierGSTIN = string.IsNullOrWhiteSpace(p.SupplierGSTIN) ? "Unregistered" : p.SupplierGSTIN,
                    SupplierName = p.SupplierName,
                    InvoiceNo = p.InvoiceNo,
                    TaxableAmount = sign * p.TaxableAmount,
                    CGSTAmount = sign * p.CGSTAmount,
                    SGSTAmount = sign * p.SGSTAmount,
                    IGSTAmount = sign * p.IGSTAmount,
                    IsITCEligible = p.IsITCEligible,
                    IsCreditNote = isNote,
                };
            }).ToList();
        }

        // Inward documents across all active inward Document Mappings (each with
        // its own DocType filter + line table), normalized by the reader.
        var bundles = await _reader.ReadInwardAsync(periodStart.Year, periodStart.Month, cancellationToken);

        // Supplier GSTIN canonically lives on Bill_Mas.GstNo, but legacy rows
        // sometimes only carry it on the supplier Account master. Pull both and
        // COALESCE(Bill_Mas.GstNo, Account.GstNo, 'Unregistered'). Also grab the
        // supplier name so the grid can show it when there's no GSTIN.
        var accountIds = bundles.Select(b => b.Header.AccountId).Distinct().ToList();
        var accounts = await _carol.Accounts
            .AsNoTracking()
            .Where(a => accountIds.Contains(a.AccountId))
            .ToDictionaryAsync(a => a.AccountId, cancellationToken);

        return bundles.Select(b =>
        {
            var h = b.Header;
            accounts.TryGetValue(h.AccountId, out var acc);

            var supplier = NormalizeGstin(h.GstNo);
            if (string.IsNullOrEmpty(supplier)) supplier = NormalizeGstin(acc?.GstNo);
            if (string.IsNullOrEmpty(supplier)) supplier = "Unregistered";
            var supplierName = !string.IsNullOrWhiteSpace(acc?.AccountName)
                ? acc!.AccountName
                : (h.AcName ?? string.Empty);

            var rate = h.ExchRate == 0m ? 1m : h.ExchRate;
            var lineTaxable = decimal.Round(b.Lines.Sum(l => l.TaxableInr), 2);
            // Header-only entries (no line rows) fall back to header value.
            if (lineTaxable == 0m && h.TotalAmt > 0m)
            {
                lineTaxable = decimal.Round(h.TotalAmt * rate, 2);
            }

            // Credit notes are stored positive in CarolERP but REDUCE ITC, so
            // sign them negative — matching how the 2B parser negates CDNR — so
            // the CDNR recon and the dashboard's eligible-ITC net correctly.
            var isCreditNote = GstDocumentCatalog.ReducesItc(h.GstCategory);
            var sign = isCreditNote ? -1m : 1m;
            return new BookRow
            {
                BillId = h.BillId,
                SupplierGSTIN = supplier,
                SupplierName = supplierName,
                InvoiceNo = BuildPurchaseInvoiceNumber(h),
                TaxableAmount = sign * lineTaxable,
                CGSTAmount = sign * decimal.Round(b.Lines.Sum(l => l.CgstAmount), 2),
                SGSTAmount = sign * decimal.Round(b.Lines.Sum(l => l.SgstAmount), 2),
                IGSTAmount = sign * decimal.Round(b.Lines.Sum(l => l.IgstAmount), 2),
                IsITCEligible = h.GstReverse == 0,
                IsCreditNote = isCreditNote,
            };
        }).ToList();
    }

    private static string BuildPurchaseInvoiceNumber(CarolDocHeader h)
    {
        if (!string.IsNullOrWhiteSpace(h.InvNo)) return h.InvNo!;
        if (h.BillNumber.HasValue)
        {
            return h.BillNumber.Value.ToString() + (h.Suffix ?? string.Empty);
        }
        return $"BILL-{h.BillId}";
    }

    internal class BookRow
    {
        public int BillId { get; set; }
        public string SupplierGSTIN { get; set; } = string.Empty;
        public string SupplierName { get; set; } = string.Empty;
        public string InvoiceNo { get; set; } = string.Empty;
        public decimal TaxableAmount { get; set; }
        public decimal CGSTAmount { get; set; }
        public decimal SGSTAmount { get; set; }
        public decimal IGSTAmount { get; set; }
        public bool IsITCEligible { get; set; }
        public bool IsCreditNote { get; set; }
    }

    // One side of a recon comparison, normalized to a comparable shape.
    private readonly record struct SideRow(string Gstin, string Name, string Invoice, decimal Total);

    private static SideRow TwoBSide(GSTR2B g)
        => new(g.SupplierGSTIN, g.SupplierName ?? string.Empty, g.InvoiceNo,
               decimal.Round(g.TaxableAmount + g.IGSTAmount + g.CGSTAmount + g.SGSTAmount, 2));

    private static SideRow BookSide(BookRow b)
        => new(b.SupplierGSTIN, b.SupplierName, b.InvoiceNo,
               decimal.Round(b.TaxableAmount + b.IGSTAmount + b.CGSTAmount + b.SGSTAmount, 2));

    // Reconciles one section (B2B / CDNR) by aggregating BOTH sides on the
    // normalized (GSTIN, invoice-no) key — so duplicate keys are summed rather
    // than silently dropped — then comparing totals within tolerance.
    private static List<ReconResult> MatchInvoiceSection(
        Tenant tenant, string section, string filingPeriod, DateTime now,
        IEnumerable<SideRow> twoB, IEnumerable<SideRow> books)
    {
        var twoBAgg = Aggregate(twoB);
        var bookAgg = Aggregate(books);
        var keys = new HashSet<string>(twoBAgg.Keys);
        keys.UnionWith(bookAgg.Keys);

        var results = new List<ReconResult>();
        foreach (var key in keys)
        {
            var hasTwoB = twoBAgg.TryGetValue(key, out var t);
            var hasBook = bookAgg.TryGetValue(key, out var b);
            if (hasTwoB && hasBook)
            {
                var diff = decimal.Round(t.Total - b.Total, 2);
                var isMatch = Math.Abs(diff) <= AmountTolerance;
                var dup = (t.Count > 1 || b.Count > 1)
                    ? $" ({t.Count} 2B / {b.Count} book line(s) share this invoice no. and were aggregated.)"
                    : string.Empty;
                results.Add(new ReconResult
                {
                    TenantId = tenant.TenantId,
                    Section = section,
                    SupplierGSTIN = t.Gstin,
                    SupplierName = string.IsNullOrWhiteSpace(b.Name) ? t.Name : b.Name,
                    InvoiceNo = t.Invoice,
                    GSTR2BAmount = t.Total,
                    BooksAmount = b.Total,
                    Difference = diff,
                    Status = isMatch ? ReconStatus.Matched : ReconStatus.Mismatch,
                    AIRemarks = (isMatch
                        ? "Books and GSTR-2B agree within tolerance; ITC can be claimed."
                        : $"Value mismatch of {diff:N2}; verify supplier document vs books before claiming ITC.") + dup,
                    FilingPeriod = filingPeriod,
                    CreatedOn = now,
                });
            }
            else if (hasTwoB)
            {
                results.Add(new ReconResult
                {
                    TenantId = tenant.TenantId,
                    Section = section,
                    SupplierGSTIN = t.Gstin,
                    SupplierName = t.Name,
                    InvoiceNo = t.Invoice,
                    GSTR2BAmount = t.Total,
                    BooksAmount = 0m,
                    Difference = t.Total,
                    Status = ReconStatus.Missing,
                    AIRemarks = "Present in GSTR-2B but missing from books; capture it to claim ITC.",
                    FilingPeriod = filingPeriod,
                    CreatedOn = now,
                });
            }
            else
            {
                results.Add(new ReconResult
                {
                    TenantId = tenant.TenantId,
                    Section = section,
                    SupplierGSTIN = b.Gstin,
                    SupplierName = b.Name,
                    InvoiceNo = b.Invoice,
                    GSTR2BAmount = 0m,
                    BooksAmount = b.Total,
                    Difference = -b.Total,
                    Status = ReconStatus.NotIn2B,
                    AIRemarks = "Booked but not yet reflected in GSTR-2B; ITC may be deferred until the supplier files.",
                    FilingPeriod = filingPeriod,
                    CreatedOn = now,
                });
            }
        }
        return results;
    }

    private static Dictionary<string, (string Gstin, string Name, string Invoice, decimal Total, int Count)> Aggregate(
        IEnumerable<SideRow> rows)
    {
        var map = new Dictionary<string, (string Gstin, string Name, string Invoice, decimal Total, int Count)>();
        foreach (var r in rows)
        {
            var key = MakeKey(r.Gstin, r.Invoice);
            if (map.TryGetValue(key, out var cur))
            {
                map[key] = (cur.Gstin,
                    string.IsNullOrWhiteSpace(cur.Name) ? r.Name : cur.Name,
                    cur.Invoice,
                    decimal.Round(cur.Total + r.Total, 2),
                    cur.Count + 1);
            }
            else
            {
                map[key] = (r.Gstin, r.Name, r.Invoice, r.Total, 1);
            }
        }
        return map;
    }

    private static ReconSummary BuildSummary(IEnumerable<ReconResult> rows)
    {
        var summary = new ReconSummary();
        foreach (var row in rows)
        {
            switch (row.Status)
            {
                case ReconStatus.Matched: summary.Matched++; break;
                case ReconStatus.Mismatch: summary.Mismatch++; break;
                case ReconStatus.Missing: summary.Missing++; break;
                case ReconStatus.NotIn2B: summary.NotIn2B++; break;
            }
        }
        return summary;
    }

    private static ReconRowResponse MapToResponse(ReconResult r) => new()
    {
        ReconId = r.ReconId,
        SupplierGSTIN = r.SupplierGSTIN,
        SupplierName = r.SupplierName ?? string.Empty,
        InvoiceNo = r.InvoiceNo,
        GSTR2BAmount = r.GSTR2BAmount,
        BooksAmount = r.BooksAmount,
        Difference = r.Difference,
        Status = r.Status,
        Section = string.IsNullOrWhiteSpace(r.Section) ? ReconSectionType.B2B : r.Section,
        AIRemarks = r.AIRemarks,
        CreatedOn = r.CreatedOn,
    };

    private static string Norm(string? s) => (s ?? string.Empty).Trim().ToUpperInvariant();

    // Recon match key shared by ReconService and GstSummaryService so both keep
    // the same view of "this invoice". GSTIN is upper/trimmed; the invoice no is
    // normalized (see NormInvoice) to absorb trivial formatting differences.
    internal static string MakeKey(string gstin, string invoiceNo) =>
        $"{(gstin ?? string.Empty).Trim().ToUpperInvariant()}|{NormInvoice(invoiceNo)}";

    // Canonicalize an invoice number for matching: drop spaces/separators, upper-
    // case, and strip leading zeros ONLY when the whole token is numeric (so
    // "001"=="1" and "INV/001"=="INV 001", but "INV001" and "INV1" stay distinct
    // to avoid false matches).
    internal static string NormInvoice(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = new string(raw.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (s.Length > 0 && s.All(char.IsDigit))
        {
            s = s.TrimStart('0');
            if (s.Length == 0) s = "0";
        }
        return s;
    }

    private static string NormalizeGstin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var cleaned = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return cleaned.Length <= 15 ? cleaned : cleaned[..15];
    }

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

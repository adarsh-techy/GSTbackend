using System.Text.Json;
using Anthropic.Models.Messages;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GSTAutoPilot.Infrastructure.Services.Advisor;

// Read-only tools the advisor can call. Each wraps an EXISTING service so the
// numbers stay deterministic and auditable — the model reads via these tools
// and never computes tax itself. Every tool resolves the tenant/company from
// the request scope (the wrapped services read HttpContext), so the model can
// only ever choose a period, never a tenant.
internal static class AdvisorTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ToolUnion[] Definitions { get; } = BuildDefinitions();

    private static ToolUnion[] BuildDefinitions()
    {
        static ToolUnion PeriodTool(string name, string description) => new Tool
        {
            Name = name,
            Description = description,
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["period"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Filing period as YYYYMM, e.g. 202604." }),
                },
                Required = ["period"],
            },
        };

        return new ToolUnion[]
        {
            PeriodTool("get_gst_summary",
                "The headline GST position for a period: output GST (sales tax), ITC from GSTR-2B, the 2B-vs-books reconciliation summary, net tax payable, and ITC carry-forward. Use this first for 'how much do I owe / what's my position' questions."),
            PeriodTool("get_gstr3b",
                "The GSTR-3B figures for a period: Table 3.1 outward-supply breakdown (taxable, zero-rated/exports, nil/exempt, reverse-charge, non-GST), Table 4 ITC, and net tax payable."),
            PeriodTool("get_recon_results",
                "GSTR-2B-vs-books reconciliation for a period: counts of matched/mismatch/missing/not-in-2B plus the top supplier-level issues (invoice, amounts, difference). Use for 'which suppliers don't match' questions."),
            PeriodTool("get_gstr2b",
                "GSTR-2B inward data on record for a period: how many records were fetched, the source, and tax totals (taxable/IGST/CGST/SGST) broken down by record type. If nothing was fetched, records will be zero."),
            PeriodTool("get_filing_status",
                "The lock/file status of GSTR-1 and GSTR-3B for a period (Draft, Locked, Submitted, or Filed) with any ARN/acknowledgement."),
            PeriodTool("get_filing_readiness",
                "A readiness check for filing GSTR-3B for a period: whether GSTR-2B is fetched, recon mismatches/missing counts, net payable, current filing status, and a ready/not-ready verdict with reasons. Use for 'am I ready to file' questions."),
        };
    }

    public static async Task<string> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, JsonElement> input,
        IServiceProvider sp,
        CancellationToken ct)
    {
        try
        {
            var period = input.TryGetValue("period", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
            return name switch
            {
                "get_gst_summary" => await GetGstSummary(sp, period, ct),
                "get_gstr3b" => await GetGstr3b(sp, period, ct),
                "get_recon_results" => await GetRecon(sp, period, ct),
                "get_gstr2b" => await GetGstr2b(sp, period, ct),
                "get_filing_status" => await GetFilingStatus(sp, period, ct),
                "get_filing_readiness" => await GetFilingReadiness(sp, period, ct),
                _ => Err($"Unknown tool '{name}'."),
            };
        }
        catch (ArgumentException ex) { return Err(ex.Message); }
        catch (InvalidOperationException ex) { return Err(ex.Message); }
        catch (Exception ex) { return Err("Could not load this data: " + ex.Message); }
    }

    private static async Task<string> GetGstSummary(IServiceProvider sp, string period, CancellationToken ct)
    {
        var s = await sp.GetRequiredService<IGstSummaryService>().GetSummaryAsync(period, ct);
        return Ok(new
        {
            period = s.Period,
            gstin = s.TenantGSTIN,
            outputGst = new { taxable = s.OutputGST.TaxableAmount, igst = s.OutputGST.IGST, cgst = s.OutputGST.CGST, sgst = s.OutputGST.SGST, total = s.OutputGST.TotalGST, invoiceCount = s.OutputGST.InvoiceCount },
            itc = new { total = s.ItcFromGSTR2B.TotalITC, eligible = s.ItcFromGSTR2B.EligibleITC, matched = s.ItcFromGSTR2B.MatchedITC, mismatched = s.ItcFromGSTR2B.MismatchedITC, missing = s.ItcFromGSTR2B.MissingITC, importIgst = s.ItcFromGSTR2B.ImportIgst },
            recon = new { matched = s.ReconSummary.Matched, mismatch = s.ReconSummary.Mismatch, missing = s.ReconSummary.Missing, notIn2B = s.ReconSummary.NotIn2B },
            netPayable = new { igst = s.NetTaxPayable.IGST, cgst = s.NetTaxPayable.CGST, sgst = s.NetTaxPayable.SGST, total = s.NetTaxPayable.Total },
            carryForward = new { total = s.CarryForward.TotalCarryForward, remarks = s.CarryForward.Remarks },
            remarks = s.AIRemarks,
        });
    }

    private static async Task<string> GetGstr3b(IServiceProvider sp, string period, CancellationToken ct)
    {
        var (year, month) = ParsePeriod(period);
        var r = await sp.GetRequiredService<IGstr3bService>().ComputeAsync(year, month, ct);
        static object Line(Gstr3bLine l) => new { taxable = l.TaxableValue, igst = l.IGST, cgst = l.CGST, sgst = l.SGST };
        var o = r.Section3_1_OutwardSupplies;
        return Ok(new
        {
            period = r.Period,
            section3_1 = new
            {
                invoiceCount = o.InvoiceCount,
                taxableOutward = Line(o.TaxableOutward),
                zeroRated = Line(o.ZeroRated),
                nilRatedExempt = Line(o.NilRatedExempt),
                reverseChargeInward = Line(o.ReverseChargeInward),
                nonGstOutward = Line(o.NonGstOutward),
            },
            table4Itc = new { purchaseCount = r.Table4_Itc.PurchaseCount, taxable = r.Table4_Itc.TaxableValue, cgst = r.Table4_Itc.CGST, sgst = r.Table4_Itc.SGST, igst = r.Table4_Itc.IGST, importIgst = r.Table4_Itc.ImportIgst, totalItc = r.Table4_Itc.TotalItcAvailable },
            netTaxPayable = new { cgst = r.NetTaxPayable.CGST, sgst = r.NetTaxPayable.SGST, igst = r.NetTaxPayable.IGST, total = r.NetTaxPayable.Total },
            carryForward = new { total = r.CarryForward.TotalCarryForward, remarks = r.CarryForward.Remarks },
        });
    }

    private static async Task<string> GetRecon(IServiceProvider sp, string period, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<IReconService>().GetResultsAsync(period, ct);
        var problems = r.Rows
            .Where(x => !string.Equals(x.Status, "Matched", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => Math.Abs(x.Difference))
            .Take(15)
            .Select(x => new { supplier = x.SupplierName, gstin = x.SupplierGSTIN, invoiceNo = x.InvoiceNo, gstr2bAmount = x.GSTR2BAmount, booksAmount = x.BooksAmount, difference = x.Difference, status = x.Status, section = x.Section });
        return Ok(new
        {
            period = r.FilingPeriod,
            summary = new { matched = r.Summary.Matched, mismatch = r.Summary.Mismatch, missing = r.Summary.Missing, notIn2B = r.Summary.NotIn2B },
            issueCount = r.Rows.Count(x => !string.Equals(x.Status, "Matched", StringComparison.OrdinalIgnoreCase)),
            topIssues = problems,
        });
    }

    private static async Task<string> GetGstr2b(IServiceProvider sp, string period, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<IGstr2bService>().GetAsync(period, ct);
        var byType = r.Records
            .GroupBy(x => x.RecordType)
            .Select(g => new { type = g.Key, count = g.Count(), taxable = g.Sum(x => x.TaxableAmount), igst = g.Sum(x => x.IGSTAmount), cgst = g.Sum(x => x.CGSTAmount), sgst = g.Sum(x => x.SGSTAmount) });
        return Ok(new
        {
            period = r.FilingPeriod,
            recordsFetched = r.RecordsFetched,
            fetchedOn = r.FetchedOn,
            source = r.Source,
            totals = new { taxable = r.Records.Sum(x => x.TaxableAmount), igst = r.Records.Sum(x => x.IGSTAmount), cgst = r.Records.Sum(x => x.CGSTAmount), sgst = r.Records.Sum(x => x.SGSTAmount) },
            byType,
        });
    }

    private static async Task<string> GetFilingStatus(IServiceProvider sp, string period, CancellationToken ct)
    {
        var svc = sp.GetRequiredService<IFilingService>();
        var g1 = await svc.LatestAsync(period, FilingType.Gstr1, ct);
        var g3 = await svc.LatestAsync(period, FilingType.Gstr3b, ct);
        static object Status(FilingResponse? f) => f is null
            ? new { locked = false, status = "Draft", ackNo = (string?)null, filedOn = (DateTime?)null }
            : new { locked = true, status = f.Status.ToString(), ackNo = f.AckNo, filedOn = f.FiledOn };
        return Ok(new { period, gstr1 = Status(g1), gstr3b = Status(g3) });
    }

    private static async Task<string> GetFilingReadiness(IServiceProvider sp, string period, CancellationToken ct)
    {
        var filing = sp.GetRequiredService<IFilingService>();
        var s = await sp.GetRequiredService<IGstSummaryService>().GetSummaryAsync(period, ct);
        var twoB = await sp.GetRequiredService<IGstr2bService>().GetAsync(period, ct);
        var g1 = await filing.LatestAsync(period, FilingType.Gstr1, ct);
        var g3 = await filing.LatestAsync(period, FilingType.Gstr3b, ct);

        var reasons = new List<string>();
        var ready = true;
        if (g3 is not null && g3.Status == FilingStatus.Filed)
        {
            ready = false;
            reasons.Add($"GSTR-3B is already filed for this period (ARN {g3.AckNo}). Nothing to file.");
        }
        else
        {
            if (twoB.RecordsFetched == 0) { ready = false; reasons.Add("GSTR-2B has not been fetched for this period — fetch it on the GSTR-2B screen before relying on ITC."); }
            if (s.ReconSummary.Mismatch > 0) { ready = false; reasons.Add($"{s.ReconSummary.Mismatch} reconciliation mismatch(es) to resolve on the Reconciliation screen."); }
            if (s.ReconSummary.Missing > 0) { reasons.Add($"{s.ReconSummary.Missing} GSTR-2B invoice(s) missing from books — capture them to claim that ITC (does not block filing)."); }
            if (ready) reasons.Add("No blockers found — safe to lock and file GSTR-3B.");
        }

        return Ok(new
        {
            period,
            readyToFile = ready,
            reasons,
            gstr2bFetched = twoB.RecordsFetched > 0,
            reconMismatches = s.ReconSummary.Mismatch,
            reconMissing = s.ReconSummary.Missing,
            outputTax = s.OutputGST.TotalGST,
            eligibleItc = s.ItcFromGSTR2B.EligibleITC,
            netPayable = s.NetTaxPayable.Total,
            gstr1Status = g1 is null ? "Draft" : g1.Status.ToString(),
            gstr3bStatus = g3 is null ? "Draft" : g3.Status.ToString(),
        });
    }

    private static (int year, int month) ParsePeriod(string period)
    {
        if (!string.IsNullOrWhiteSpace(period) && period.Length == 6
            && int.TryParse(period.AsSpan(0, 4), out var y)
            && int.TryParse(period.AsSpan(4, 2), out var m)
            && m is >= 1 and <= 12)
        {
            return (y, m);
        }
        throw new ArgumentException("period must be YYYYMM, e.g. 202604.");
    }

    private static string Ok(object value) => JsonSerializer.Serialize(value, Json);
    private static string Err(string message) => JsonSerializer.Serialize(new { error = message }, Json);
}

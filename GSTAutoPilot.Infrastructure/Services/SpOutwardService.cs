using System.Data;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

// Alternative outward data source: a per-tenant stored procedure in the tenant's
// CarolERP DB that returns line-level GST rows already classified. Contract
// (Tenants.OutwardSP): EXEC <sp> @GstNo, @StartDate, @EndDate returns one row
// per invoice line with columns:
//   BillId, BillNumber, BillDate, AccountName, GstNumber, TotalAmt, TaxableAmt,
//   GstRate, HSNCode, CGSTAmt, SGSTAmt, IGSTAmt, GstType (B2B/B2CS/B2CL/Export/CDN)
// The SP owns ALL GST logic (classification, POS, interstate, exclusions). The
// app just groups rows by BillId into invoices and trusts GstType. Because the
// SP takes ONE GSTIN, we call it once per distinct GSTIN in the active company
// group (or all groups when no company is selected) and merge the results.
public class SpOutwardService
{
    private readonly CarolERPDbContext _carol;
    private readonly Persistence.TenantDbContext _db;
    private readonly IHttpContextAccessor _http;

    public SpOutwardService(CarolERPDbContext carol, Persistence.TenantDbContext db, IHttpContextAccessor http)
    {
        _carol = carol;
        _db = db;
        _http = http;
    }

    private Tenant? Tenant => _http.HttpContext?.Items["Tenant"] as Tenant;

    // True when this tenant is configured to use the outward SP.
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Tenant?.OutwardSP);

    public async Task<IReadOnlyList<InvoiceResponse>> ListAsync(int year, int month, CancellationToken ct = default)
    {
        var spName = Tenant?.OutwardSP;
        if (string.IsNullOrWhiteSpace(spName))
            throw new InvalidOperationException("Outward SP is not configured for this tenant.");
        var sp = ValidateSpName(spName);

        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var end = start.AddMonths(1).AddDays(-1); // last day of the month (inclusive)

        var gstins = await ResolveGstinsAsync(ct);
        var byBill = new Dictionary<int, InvoiceResponse>();

        var conn = (SqlConnection)_carol.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(ct);
        try
        {
            foreach (var gstin in gstins)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = sp;
                cmd.Parameters.Add(new SqlParameter("@GstNo", gstin));
                cmd.Parameters.Add(new SqlParameter("@StartDate", start));
                cmd.Parameters.Add(new SqlParameter("@EndDate", end));

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var map = BuildOrdinalMap(reader);
                while (await reader.ReadAsync(ct))
                {
                    var billId = GetInt(reader, map, "BillId");
                    if (!byBill.TryGetValue(billId, out var inv))
                    {
                        inv = new InvoiceResponse
                        {
                            Id = DeterministicGuid(billId),
                            BillId = billId,
                            InvoiceNumber = GetString(reader, map, "BillNumber"),
                            InvoiceDate = GetDate(reader, map, "BillDate"),
                            PartyName = GetString(reader, map, "AccountName"),
                            PartyGSTIN = GetString(reader, map, "GstNumber"),
                            Section = NormalizeSection(GetString(reader, map, "GstType")),
                            GstCategory = GetString(reader, map, "GstType"),
                            TotalAmount = GetDecimal(reader, map, "TotalAmt"),
                        };
                        byBill[billId] = inv;
                    }
                    var taxable = GetDecimal(reader, map, "TaxableAmt");
                    var cgst = GetDecimal(reader, map, "CGSTAmt");
                    var sgst = GetDecimal(reader, map, "SGSTAmt");
                    var igst = GetDecimal(reader, map, "IGSTAmt");
                    inv.TaxableValue += taxable;
                    inv.CGST += cgst;
                    inv.SGST += sgst;
                    inv.IGST += igst;
                    inv.Lines.Add(new InvoiceLineResponse
                    {
                        Id = DeterministicGuid(GetInt(reader, map, "BillId") * 1000 + inv.Lines.Count),
                        HSNCode = GetString(reader, map, "HSNCode"),
                        TaxableValue = taxable,
                        GstRate = GetDecimal(reader, map, "GstRate"),
                        Cess = GetDecimal(reader, map, "CessAmt"),
                        CGST = cgst,
                        SGST = sgst,
                        IGST = igst,
                        Total = decimal.Round(taxable + cgst + sgst + igst, 2),
                    });
                }
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }

        foreach (var inv in byBill.Values)
        {
            inv.TaxableValue = decimal.Round(inv.TaxableValue, 2);
            inv.CGST = decimal.Round(inv.CGST, 2);
            inv.SGST = decimal.Round(inv.SGST, 2);
            inv.IGST = decimal.Round(inv.IGST, 2);
        }

        await ApplyExportClassificationAsync(byBill, start, end, ct);

        var invoices = byBill.Values.OrderByDescending(i => i.InvoiceDate).ToList();
        await ApplyEInvoiceStatusAsync(invoices, ct);
        return invoices;
    }

    // Sales invoice counts per period (yyyyMM) for the ERP period selector.
    // The SP is per-GSTIN and per-date-range, so we run it once per GSTIN over a
    // recent window and let SQL aggregate (COUNT(DISTINCT BillId) grouped by
    // month) — only one small row per month comes back, not every line. Older
    // periods (before the window) simply carry no SP sales count.
    public async Task<Dictionary<string, int>> OutwardCountsByPeriodAsync(CancellationToken ct = default)
    {
        const int monthsBack = 24;
        var spName = Tenant?.OutwardSP;
        if (string.IsNullOrWhiteSpace(spName)) return new Dictionary<string, int>();
        var sp = ValidateSpName(spName);

        var firstOfThisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var start = firstOfThisMonth.AddMonths(-(monthsBack - 1));
        var end = firstOfThisMonth.AddMonths(1).AddDays(-1);

        var gstins = await ResolveGstinsAsync(ct);
        var counts = new Dictionary<string, int>();

        var conn = (SqlConnection)_carol.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(ct);
        try
        {
            foreach (var gstin in gstins)
            {
                await using var cmd = conn.CreateCommand();
                // Capture the SP's rows into a temp table, then aggregate server-
                // side so only ~one row per month crosses the wire. The SP name is
                // a validated identifier; the three params are bound.
                cmd.CommandText = $@"
CREATE TABLE #sp (TableRowId int NULL, BillId int NULL, BillNumber nvarchar(200) NULL, BillDate datetime NULL, AccountName nvarchar(300) NULL, GstNumber nvarchar(50) NULL, TotalAmt decimal(20,4) NULL, TaxableAmt decimal(20,4) NULL, GstRate decimal(12,4) NULL, HSNCode nvarchar(60) NULL, CGSTAmt decimal(20,4) NULL, SGSTAmt decimal(20,4) NULL, IGSTAmt decimal(20,4) NULL, GstType nvarchar(30) NULL);
INSERT INTO #sp EXEC {sp} @GstNo, @StartDate, @EndDate;
SELECT CONVERT(char(6), BillDate, 112) AS Period, COUNT(DISTINCT BillId) AS Cnt FROM #sp WHERE BillDate IS NOT NULL GROUP BY CONVERT(char(6), BillDate, 112);
DROP TABLE #sp;";
                cmd.Parameters.Add(new SqlParameter("@GstNo", gstin));
                cmd.Parameters.Add(new SqlParameter("@StartDate", start));
                cmd.Parameters.Add(new SqlParameter("@EndDate", end));

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var period = reader.GetString(0);
                    var cnt = Convert.ToInt32(reader.GetValue(1));
                    counts[period] = counts.TryGetValue(period, out var e) ? e + cnt : cnt;
                }
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
        return counts;
    }

    // The SP hardcodes GstType = 'B2B' on EVERY row it returns — it classifies
    // nothing (verified against KSCC's Usp_GSTR_For_Filing: 1,394/1,394 rows for
    // Oct-2025 came back 'B2B', exports and cash B2C alike). Left alone, exports
    // reach the JSON builder labelled B2B with no counter-party GSTIN and are
    // dropped from GSTR-1 entirely, so the section has to be derived here.
    //
    // The ERP does carry the signal, just not through the SP: KSCC raises export
    // invoices under a document type of DocType 35 / SubType 0 ("Invoice - Export")
    // in the export bill file, distinct from DocType 35 / SubType 2 ("Domestic
    // Invoice") in the same table. Those rows also carry the foreign currency and
    // the exchange rate the SP's TotalAmt is denominated in.
    //
    // KSCC-flavour schema only; other flavours keep whatever the SP said (their
    // SPs may classify properly — Flooratex has no outward SP at all).
    private async Task ApplyExportClassificationAsync(
        Dictionary<int, InvoiceResponse> byBill, DateTime start, DateTime end, CancellationToken ct)
    {
        if (byBill.Count == 0) return;
        if (!string.Equals(_carol.Flavor, "KSCC", StringComparison.OrdinalIgnoreCase)) return;

        var conn = (SqlConnection)_carol.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT m.BillId, m.CurId, m.ExchRate
FROM Bill_File_mas m
INNER JOIN Documents d ON m.DocId = d.DocId
WHERE m.BillDate BETWEEN @StartDate AND @EndDate
  AND m.Sanctioned = 1
  AND d.DocType = 35 AND d.SubType = 0";
            cmd.Parameters.Add(new SqlParameter("@StartDate", start));
            cmd.Parameters.Add(new SqlParameter("@EndDate", end));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var billId = Convert.ToInt32(reader.GetValue(0));
                if (!byBill.TryGetValue(billId, out var inv)) continue;

                inv.Section = "Export";
                inv.GstCategory = GstDocumentCatalog.ExportSales;

                // TotalAmt on an export bill is the FOB value in the INVOICE's
                // currency while TaxableAmt is already in rupees, so the raw
                // TotalAmt would put a dollar figure into inv_val (and into the
                // B2CL threshold and the >5L e-invoice flag). Convert it, then
                // add the tax so the value stays gross like every other section
                // (domestic TotalAmt is taxable + tax; the export FOB is net).
                var curId = reader.IsDBNull(1) ? 1 : Convert.ToInt32(reader.GetValue(1));
                var rate = reader.IsDBNull(2) ? 0m : Convert.ToDecimal(reader.GetValue(2));
                if (curId != InrCurrencyId && rate > 0m)
                    inv.TotalAmount = decimal.Round(inv.TotalAmount * rate + inv.IGST + inv.CGST + inv.SGST, 2);
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
    }

    // CurId of the base currency (INR) on the KSCC install; anything else is a
    // foreign-currency invoice whose TotalAmt needs the ExchRate applied.
    private const int InrCurrencyId = 1;

    // Distinct GSTIN(s) to call the SP for: the active company's GST group when a
    // company is selected, otherwise every group's GSTIN.
    private async Task<IReadOnlyList<string>> ResolveGstinsAsync(CancellationToken ct)
    {
        var groups = await _carol.CompanyGroupsAsync(ct);
        var all = groups.Select(g => g.Gstin).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList();
        var co = _carol.ActiveCompanyId;
        if (co is null) return all;
        var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(co.Value));
        return group is not null && !string.IsNullOrWhiteSpace(group.Gstin)
            ? new[] { group.Gstin }
            : Array.Empty<string>();
    }

    private async Task ApplyEInvoiceStatusAsync(IReadOnlyList<InvoiceResponse> invoices, CancellationToken ct)
    {
        if (invoices.Count == 0) return;
        var billIds = invoices.Select(i => i.BillId).ToList();
        var irnByBill = (await _db.IRNRecords.AsNoTracking()
            .Where(r => r.BillId != null && billIds.Contains(r.BillId!.Value) && r.Status == IRNStatus.Generated)
            .Select(r => new { BillId = r.BillId!.Value, r.IRNNumber })
            .ToListAsync(ct))
            .GroupBy(r => r.BillId).ToDictionary(g => g.Key, g => g.Last().IRNNumber);
        foreach (var inv in invoices)
        {
            var has = irnByBill.TryGetValue(inv.BillId, out var irn);
            inv.Irn = has ? irn ?? string.Empty : string.Empty;
            inv.EInvoiceStatus = has ? "Done" : inv.TotalAmount > 500_000m ? "Required" : "NA";
        }
    }

    // Map the app's five section values; passes through anything already matching.
    private static string NormalizeSection(string gstType)
    {
        var s = (gstType ?? string.Empty).Trim();
        return s switch
        {
            "B2B" or "B2CS" or "B2CL" or "Export" or "CDN" => s,
            "EXP" or "EXPORT" => "Export",
            "" => "B2CS",
            _ => s,
        };
    }

    private static string ValidateSpName(string name)
    {
        // SP name comes from tenant config, not user input, but keep it to a safe
        // identifier ([schema.]name) to be defensive.
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$"))
            throw new InvalidOperationException($"Invalid stored procedure name '{name}'.");
        return name;
    }

    private static Dictionary<string, int> BuildOrdinalMap(SqlDataReader reader)
    {
        var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) m[reader.GetName(i)] = i;
        return m;
    }

    private static int GetInt(SqlDataReader r, Dictionary<string, int> m, string col)
        => m.TryGetValue(col, out var o) && !r.IsDBNull(o) ? Convert.ToInt32(r.GetValue(o)) : 0;
    private static decimal GetDecimal(SqlDataReader r, Dictionary<string, int> m, string col)
        => m.TryGetValue(col, out var o) && !r.IsDBNull(o) ? Convert.ToDecimal(r.GetValue(o)) : 0m;
    private static string GetString(SqlDataReader r, Dictionary<string, int> m, string col)
        => m.TryGetValue(col, out var o) && !r.IsDBNull(o) ? r.GetValue(o).ToString() ?? string.Empty : string.Empty;
    private static DateTime GetDate(SqlDataReader r, Dictionary<string, int> m, string col)
        => m.TryGetValue(col, out var o) && !r.IsDBNull(o) ? Convert.ToDateTime(r.GetValue(o)) : default;

    private static Guid DeterministicGuid(int id)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[12..], id);
        return new Guid(bytes);
    }
}

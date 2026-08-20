using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

// Inward (purchase) counterpart of SpOutwardService: a per-tenant stored
// procedure in the tenant's CarolERP DB that returns purchase GST rows already
// classified. Contract (Tenants.InwardSP): EXEC <sp> @GstNo, @StartDate,
// @EndDate returns one row per purchase LINE.
//
// Column names are read case-insensitively and each logical field accepts a
// small set of aliases, because real-world inward SPs drift on names. The
// aliases below cover both the app's canonical names and the shape KSCC's
// Usp_GSTR2A_For_Filing actually emits:
//   invoice key : BillNumber (supplier invoice no)  [+ supplier GSTIN]
//   date        : BillDate   (real date OR dd-MM-yyyy text — both parsed)
//   supplier    : AccountName
//   gstin       : GstNumber / GSTNumber
//   taxable     : TaxableAmt / Amount
//   total       : TotalAmt   / TotalValue
//   tax         : CGSTAmt / SGSTAmt / IGSTAmt
//   rate        : GstRate
//   ITC (opt)   : ItcEligible (bit; absent => eligible)
//
// Invoices are grouped by (supplier GSTIN + supplier invoice no) — NOT by any
// single row id, since the SP's row id is per-LINE, so a multi-HSN bill spans
// several rows that must roll up into one purchase invoice.
//
// Some inward SPs ignore @StartDate and return everything up to @EndDate
// (cumulative 2A behaviour). To keep a month view honest we filter rows to the
// requested [start, end] window client-side after reading.
public class SpInwardService
{
    private readonly CarolERPDbContext _carol;
    private readonly IHttpContextAccessor _http;

    public SpInwardService(CarolERPDbContext carol, IHttpContextAccessor http)
    {
        _carol = carol;
        _http = http;
    }

    private Tenant? Tenant => _http.HttpContext?.Items["Tenant"] as Tenant;

    // True when this tenant is configured to use the inward SP.
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Tenant?.InwardSP);

    public async Task<IReadOnlyList<PurchaseInvoiceResponse>> ListAsync(int year, int month, CancellationToken ct = default)
    {
        var spName = Tenant?.InwardSP;
        if (string.IsNullOrWhiteSpace(spName))
            throw new InvalidOperationException("Inward SP is not configured for this tenant.");
        var sp = ValidateSpName(spName);

        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var end = start.AddMonths(1).AddDays(-1); // last day of the month (inclusive)

        var gstins = await ResolveGstinsAsync(ct);
        var byInvoice = new Dictionary<string, PurchaseInvoiceResponse>(StringComparer.OrdinalIgnoreCase);

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
                    var billDate = GetDateFlexible(reader, map, "BillDate", "InvoiceDate", "InvDate");
                    // Defensive: drop rows outside the requested month (SP may
                    // over-return earlier periods). Rows with an unparseable date
                    // are skipped rather than dumped into the wrong month.
                    if (billDate == default || billDate.Date < start.Date || billDate.Date > end.Date)
                        continue;

                    var invoiceNo = GetStringAny(reader, map, "BillNumber", "InvNo", "InvoiceNo").Trim();
                    var supplierGstin = NormalizeGstin(GetStringAny(reader, map, "GstNumber", "GSTNumber", "SupplierGSTIN"));
                    // Document category from the SP's Bill_Cat column (purchase /
                    // credit note / debit note). Part of the grouping key so a
                    // credit note never merges with a purchase that happens to
                    // share an invoice number.
                    var category = ClassifyBillCat(GetStringAny(reader, map, "Bill_Cat", "BillCat", "BillCategory", "DocCategory"));
                    var key = string.IsNullOrEmpty(supplierGstin) && string.IsNullOrEmpty(invoiceNo)
                        ? "row:" + GetIntAny(reader, map, "TableRowId", "BillId", "RowId")
                        : supplierGstin + "|" + invoiceNo + "|" + category;

                    if (!byInvoice.TryGetValue(key, out var inv))
                    {
                        inv = new PurchaseInvoiceResponse
                        {
                            PurchaseInvoiceId = DeterministicGuid(key),
                            InvoiceNo = string.IsNullOrEmpty(invoiceNo) ? key : invoiceNo,
                            InvoiceDate = billDate,
                            CreatedOn = billDate,
                            SupplierName = GetStringAny(reader, map, "AccountName", "SupplierName"),
                            SupplierGSTIN = supplierGstin,
                            BillCategory = category,
                            // Optional: absent column defaults to eligible.
                            IsITCEligible = GetBool(reader, map, "ItcEligible", @default: true),
                        };
                        byInvoice[key] = inv;
                    }
                    // Tax columns: accept both the app's canonical *Amt names and
                    // KSCC's Usp_GSTR2A_For_Filing *Amount names.
                    inv.TaxableAmount += GetDecimalAny(reader, map, "TaxableAmt", "TaxableAmount", "Amount", "TaxableValue");
                    inv.CGSTAmount += GetDecimalAny(reader, map, "CGSTAmt", "CGSTAmount", "CGST");
                    inv.SGSTAmount += GetDecimalAny(reader, map, "SGSTAmt", "SGSTAmount", "SGST");
                    inv.IGSTAmount += GetDecimalAny(reader, map, "IGSTAmt", "IGSTAmount", "IGST");
                    // Representative rate for the invoice = the largest line rate.
                    var rate = GetDecimalAny(reader, map, "GstRate", "GSTRate");
                    if (rate > inv.GSTRate) inv.GSTRate = rate;
                }
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }

        foreach (var inv in byInvoice.Values)
        {
            inv.TaxableAmount = decimal.Round(inv.TaxableAmount, 2);
            inv.CGSTAmount = decimal.Round(inv.CGSTAmount, 2);
            inv.SGSTAmount = decimal.Round(inv.SGSTAmount, 2);
            inv.IGSTAmount = decimal.Round(inv.IGSTAmount, 2);
            // Total is computed from components so it stays internally consistent
            // even when a multi-line bill's TotalValue is repeated per row.
            inv.TotalAmount = decimal.Round(
                inv.TaxableAmount + inv.CGSTAmount + inv.SGSTAmount + inv.IGSTAmount, 2);
        }

        return byInvoice.Values.OrderByDescending(i => i.InvoiceDate).ToList();
    }

    // Purchase invoice counts per period (yyyyMM) for the ERP period selector.
    // Reads the SP with a DataReader (no INSERT..EXEC into a fixed-shape temp
    // table, which would break the moment the SP's column list changes) and
    // counts DISTINCT invoices per period client-side.
    public async Task<Dictionary<string, int>> InwardCountsByPeriodAsync(CancellationToken ct = default)
    {
        const int monthsBack = 24;
        var spName = Tenant?.InwardSP;
        if (string.IsNullOrWhiteSpace(spName)) return new Dictionary<string, int>();
        var sp = ValidateSpName(spName);

        var firstOfThisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var start = firstOfThisMonth.AddMonths(-(monthsBack - 1));
        var end = firstOfThisMonth.AddMonths(1).AddDays(-1);

        var gstins = await ResolveGstinsAsync(ct);
        // period -> distinct invoice keys, so a multi-line bill counts once.
        var keysByPeriod = new Dictionary<string, HashSet<string>>();

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
                    var billDate = GetDateFlexible(reader, map, "BillDate", "InvoiceDate", "InvDate");
                    if (billDate == default || billDate.Date < start.Date || billDate.Date > end.Date)
                        continue;
                    var period = billDate.ToString("yyyyMM", CultureInfo.InvariantCulture);

                    var invoiceNo = GetStringAny(reader, map, "BillNumber", "InvNo", "InvoiceNo").Trim();
                    var supplierGstin = NormalizeGstin(GetStringAny(reader, map, "GstNumber", "GSTNumber", "SupplierGSTIN"));
                    var key = string.IsNullOrEmpty(supplierGstin) && string.IsNullOrEmpty(invoiceNo)
                        ? "row:" + GetIntAny(reader, map, "TableRowId", "BillId", "RowId")
                        : supplierGstin + "|" + invoiceNo;

                    if (!keysByPeriod.TryGetValue(period, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        keysByPeriod[period] = set;
                    }
                    set.Add(key);
                }
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }

        return keysByPeriod.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
    }

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

    private static string ValidateSpName(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$"))
            throw new InvalidOperationException($"Invalid stored procedure name '{name}'.");
        return name;
    }

    private static string NormalizeGstin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
    }

    private static Dictionary<string, int> BuildOrdinalMap(SqlDataReader reader)
    {
        var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) m[reader.GetName(i)] = i;
        return m;
    }

    // Accepts the app's canonical name plus any known aliases; first present,
    // non-null column wins.
    private static int GetIntAny(SqlDataReader r, Dictionary<string, int> m, params string[] cols)
    {
        foreach (var col in cols)
            if (m.TryGetValue(col, out var o) && !r.IsDBNull(o)) return Convert.ToInt32(r.GetValue(o));
        return 0;
    }
    private static decimal GetDecimalAny(SqlDataReader r, Dictionary<string, int> m, params string[] cols)
    {
        foreach (var col in cols)
            if (m.TryGetValue(col, out var o) && !r.IsDBNull(o)) return Convert.ToDecimal(r.GetValue(o));
        return 0m;
    }
    private static string GetStringAny(SqlDataReader r, Dictionary<string, int> m, params string[] cols)
    {
        foreach (var col in cols)
            if (m.TryGetValue(col, out var o) && !r.IsDBNull(o)) return r.GetValue(o).ToString() ?? string.Empty;
        return string.Empty;
    }
    private static bool GetBool(SqlDataReader r, Dictionary<string, int> m, string col, bool @default)
        => m.TryGetValue(col, out var o) && !r.IsDBNull(o) ? Convert.ToBoolean(Convert.ToInt32(r.GetValue(o))) : @default;

    // Normalize the SP's Bill_Cat text into "Purchase" / "CreditNote" /
    // "DebitNote". Tolerant: matches on the substrings "credit"/"debit" so
    // variants ("Credit Note", "CR NOTE", "creditnote") all resolve. Anything
    // else (incl. a blank/absent column) is treated as a purchase.
    private static string ClassifyBillCat(string raw)
    {
        var s = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (s.Contains("credit")) return "CreditNote";
        if (s.Contains("debit")) return "DebitNote";
        return "Purchase";
    }

    // Date columns may arrive as a real datetime OR as text (KSCC returns
    // dd-MM-yyyy). Try typed first, then the common Indian text formats.
    private static readonly string[] DateFormats =
    {
        "dd-MM-yyyy", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy",
        "yyyy-MM-dd", "dd-MM-yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss",
    };
    private static DateTime GetDateFlexible(SqlDataReader r, Dictionary<string, int> m, params string[] cols)
    {
        foreach (var col in cols)
        {
            if (!m.TryGetValue(col, out var o) || r.IsDBNull(o)) continue;
            var v = r.GetValue(o);
            if (v is DateTime dt) return dt;
            var s = v.ToString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            s = s.Trim();
            if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p2))
                return p2;
        }
        return default;
    }

    // Stable GUID from the invoice key, so the same purchase invoice keeps the
    // same id across requests (MD5 of the UTF-8 key -> 16 bytes).
    private static Guid DeterministicGuid(string key)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(bytes);
    }
}

using System.Data;
using GSTAutoPilot.Infrastructure.CarolERP;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

// One normalized document line, schema-agnostic. TaxableInr is already in INR
// (Amount * header ExchRate); IGST/CGST/SGST amounts are stored in INR on the
// CarolERP line in every schema.
public sealed record CarolSalesLine(
    int BillId,
    int LineSl,
    string Description,
    string Hsn,
    decimal Quantity,
    decimal Rate,
    decimal TaxableInr,
    decimal IgstRate,
    decimal IgstAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    // Gross line value in INR (pre-discount Amount × ExchRate). Discount =
    // GrossInr − TaxableInr. Defaults to TaxableInr when the query doesn't
    // supply a separate gross column (⇒ no discount). NOT Rate×Qty, which is
    // unreliable (Rate can be a list/MRP price, e.g. intercompany transfers).
    decimal GrossInr = 0m);

// Reads document lines for a set of bills from any of the known CarolERP line
// schemas, normalizing them into CarolSalesLine. The line table is chosen by
// the active Document Mapping; unknown tables yield no rows (caller falls back
// to the header amount).
//
// Known schemas:
//   Bill_File_trn (KSCC export): ItemId/SpecId/SizeId/DesignId; HSN via Item.
//   Bill_Exp_trn  (Flooratex export): line -> Bo_trn (ItemDescription) -> Product.
//   Bill_Ls_Trn   (local sales): ItemId/SpecId/SizeId + own HsnId + ProductId.
//   Bill_Lp_trn   (local purchase): ItemId/SpecId/SizeId + own HsnId.
//   Bill_Inp_trn  (purchase): amount + CGSTAmt/SGSTAmt/IGSTAmt only (no item).
// In every schema Amount is in the bill's currency (×ExchRate ⇒ INR) and the
// tax amounts are already INR.
public class SalesLineProvider
{
    private readonly CarolERPDbContext _carol;

    public SalesLineProvider(CarolERPDbContext carol)
    {
        _carol = carol;
    }

    public async Task<Dictionary<int, List<CarolSalesLine>>> GetLinesAsync(
        string lineTable,
        IReadOnlyCollection<int> billIds,
        IReadOnlyDictionary<int, decimal> exchByBill,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, List<CarolSalesLine>>();
        if (billIds.Count == 0 || string.IsNullOrWhiteSpace(lineTable)) return result;

        var table = CarolERPDbContext.ValidateTableName(lineTable);
        var sql = BuildSql(table, billIds.Count);
        if (sql is null) return result; // unknown schema -> header fallback

        var conn = (SqlConnection)_carol.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var idArray = billIds.ToArray();
            for (var i = 0; i < idArray.Length; i++)
                cmd.Parameters.Add(new SqlParameter($"@b{i}", idArray[i]));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var billId = reader.GetInt32(0);
                var rate = exchByBill.TryGetValue(billId, out var r) ? r : 1m;
                if (rate == 0m) rate = 1m;
                var amount = GetDecimal(reader, 4);
                // Optional column 11 = gross (pre-discount) line amount. Absent
                // on queries that don't expose it ⇒ gross = taxable ⇒ no discount.
                var grossRaw = reader.FieldCount > 11 ? GetDecimal(reader, 11) : amount;
                var line = new CarolSalesLine(
                    BillId: billId,
                    LineSl: reader.GetInt32(1),
                    Description: GetString(reader, 2),
                    Hsn: GetString(reader, 3),
                    Quantity: GetDecimal(reader, 5),
                    Rate: GetDecimal(reader, 6),
                    TaxableInr: decimal.Round(amount * rate, 2),
                    IgstRate: GetDecimal(reader, 7),
                    IgstAmount: decimal.Round(GetDecimal(reader, 8), 2),
                    CgstAmount: decimal.Round(GetDecimal(reader, 9), 2),
                    SgstAmount: decimal.Round(GetDecimal(reader, 10), 2),
                    GrossInr: decimal.Round(grossRaw * rate, 2));
                if (!result.TryGetValue(billId, out var list))
                {
                    list = new List<CarolSalesLine>();
                    result[billId] = list;
                }
                list.Add(line);
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
        return result;
    }

    // Column order (shared across all queries):
    // 0 BillId, 1 LineSl, 2 Description, 3 Hsn, 4 Amount, 5 Quantity, 6 Rate,
    // 7 IgstRate, 8 IgstAmount, 9 CgstAmount, 10 SgstAmount
    private string? BuildSql(string table, int count)
    {
        var isKscc = string.Equals(_carol.Flavor, "KSCC", StringComparison.OrdinalIgnoreCase);
        return table switch
        {
            // Bill_Ls_Trn / Bill_Lp_trn templates LEFT JOIN the Flooratex-only
            // `HSN` master. KSCC has no HSN table (HSN lives on GstCategory).
            // Until KSCC variants are written for those tables, return null on
            // KSCC and let the caller fall back to the header amount.
            // Bill_Exp_trn diverges between flavors: Flooratex's is a thin export
            // line (Amount/IGST only, HSN via Item.HsnId); KSCC's is a rich local
            // SALES line ("Sales Bill", DocType 525) carrying TaxableAmt +
            // CGST/SGST/IGST amounts and tracing HSN via BoSl→Product→Item→
            // GstCategory, like Bill_File_trn. Without the KSCC variant these
            // local sales fell back to the header total (wrong taxable, zero tax).
            _ when table.Equals("Bill_Exp_trn", StringComparison.OrdinalIgnoreCase) =>
                isKscc ? BillExpTrnKsccSql(count) : BillExpTrnSql(count),
            // Bill_File_trn schema diverges between flavors: Flooratex's has
            // ItemId/SpecId/SizeId/DesignId and joins to the Item→HSN master;
            // KSCC's has BoSl + FiledQty + *Perc/*Amt and joins via
            // Bo_trn→Product→Item→GstCategory for HSN.
            _ when table.Equals("Bill_File_trn", StringComparison.OrdinalIgnoreCase) =>
                isKscc ? BillFileTrnKsccSql(count) : BillFileTrnSql(table, count),
            // Bill_Ls_Trn (Local Sales lines) exists on BOTH flavors with the
            // same tax-column names (CgstAmount/SgstAmount/IgstAmount,
            // IgstRate), but the HSN trace differs: Flooratex joins
            // Bill_Ls_Trn.HsnId → HSN.HsnCode; KSCC has no HsnId column on
            // the line, so HSN comes via Item.GstCatId → GstCategory.HsnCode.
            _ when table.Equals("Bill_Ls_Trn", StringComparison.OrdinalIgnoreCase) =>
                isKscc ? BillLsTrnKsccSql(count) : BillLsTrnSql(table, count),
            // Bill_Lp_trn (Local Purchase): Flooratex has IgstRate + HsnId; KSCC
            // has IGSTPerc + TaxableAmt and traces HSN via Item.GstCatId. KSCC
            // local-purchase doctype 145 (Coir) lives here.
            _ when table.Equals("Bill_Lp_trn", StringComparison.OrdinalIgnoreCase) =>
                isKscc ? BillLpTrnKsccSql(count) : BillLpTrnSql(table, count),
            // Bill_Inp_trn (Purchase): Flooratex exposes IgstRate; KSCC exposes
            // IGSTPerc + TaxableAmt and no item ref. KSCC purchase doctypes
            // 520/80/85 live here — the source of input ITC.
            _ when table.Equals("Bill_Inp_trn", StringComparison.OrdinalIgnoreCase) =>
                isKscc ? BillInpTrnKsccSql(count) : BillInpTrnSql(table, count),
            // Bill_DrCr_Items (debit/credit note items): KSCC's carries its own
            // TaxableAmt + Cgst/Sgst/IgstAmount + IgstRate per line. Used by
            // purchase credit notes (DocType 900) — the service negates these
            // against ITC. Amounts are stored positive; the sign is applied by
            // the GstCategory, not here.
            _ when table.Equals("Bill_DrCr_Items", StringComparison.OrdinalIgnoreCase) =>
                isKscc ? BillDrCrItemsKsccSql(count) : null,
            // Bill_General (KSCC general-voucher / journal module, DocType 930):
            // a double-entry table (TrnType 1=Dr / 2=Cr) where GST-bearing rows
            // are EXPENSE debits carrying input CGST/SGST/IGST (freight, rent,
            // professional, AMC, commission…). Only those rows are ITC, so the
            // reader filters to lines with non-zero tax — the balancing journal
            // postings (party/bank, no GST) are excluded so they don't inflate
            // the ITC taxable base. Taxable = TaxableAmt, else the Dr amount.
            _ when table.Equals("Bill_General", StringComparison.OrdinalIgnoreCase) =>
                isKscc ? BillGeneralKsccSql(count) : null,
            _ => null,
        };
    }

    // KSCC Bill_Ls_Trn: same tax-column names as Flooratex
    // (Amount/Quantity/Rate/IgstRate + CgstAmount/SgstAmount/IgstAmount), but
    // no HsnId or ProductId column on the line. HSN trace:
    //   Item.GstCatId -> GstCategory.HsnCode
    // Description falls back to ItemName since Specification → ItemSpec on
    // KSCC and joining it doesn't pay back; keep the SQL minimal.
    private static string BillLsTrnKsccSql(int count) => $@"
SELECT t.BillId, t.BillLsSl,
       COALESCE(NULLIF(LTRIM(RTRIM(i.ItemName)),''),
                CONCAT('Line ', t.BillLsSl)) AS Descr,
       ISNULL(gc.HsnCode,'') AS Hsn,
       COALESCE(NULLIF(t.TaxableAmt,0), t.Amount - ISNULL(t.DiscAmt,0)), t.Quantity, t.Rate,
       t.IgstRate, ISNULL(t.IgstAmount,0), ISNULL(t.CgstAmount,0), ISNULL(t.SgstAmount,0),
       t.Amount
FROM Bill_Ls_Trn t
LEFT JOIN Item i ON t.ItemId = i.ItemId
LEFT JOIN GstCategory gc ON i.GstCatId = gc.GstCatId
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillLsSl";

    // KSCC Bill_File_trn: line has BoSl (no ItemId). HSN trace:
    //   Bill_File_trn.BoSl -> Bo_trn.ProductId -> Product.ItemId
    //     -> Item.GstCatId -> GstCategory.HsnCode
    // KSCC tracks CGST/SGST/IGST on sales lines (not export-only IGST).
    private static string BillFileTrnKsccSql(int count) => $@"
SELECT t.BillId, t.BillSl,
       COALESCE(NULLIF(LTRIM(RTRIM(p.ProductName)),''),
                NULLIF(LTRIM(RTRIM(i.ItemName)),''),
                NULLIF(LTRIM(RTRIM(bo.Remarks)),''),
                CONCAT('Line ', t.BillSl)) AS Descr,
       ISNULL(gc.HsnCode,'') AS Hsn,
       t.Amount, t.FiledQty, t.Rate,
       t.IGSTPerc, ISNULL(t.IGSTAmt,0), ISNULL(t.CGSTAmt,0), ISNULL(t.SGSTAmt,0)
FROM Bill_File_trn t
LEFT JOIN Bo_trn bo ON t.BoSl = bo.BoSl
LEFT JOIN Product p ON bo.ProductId = p.ProductId
LEFT JOIN Item i ON p.ItemId = i.ItemId
LEFT JOIN GstCategory gc ON i.GstCatId = gc.GstCatId
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillSl";

    // KSCC Bill_Exp_trn (local "Sales Bill", DocType 525): rich line carrying
    // TaxableAmt + CGST/SGST/IGST perc & amt. No ItemId/HsnId on the line; HSN
    // and description trace via BoSl, identical to the KSCC Bill_File_trn path:
    //   Bill_Exp_trn.BoSl -> Bo_trn.ProductId -> Product.ItemId
    //     -> Item.GstCatId -> GstCategory.HsnCode
    // Taxable uses TaxableAmt (the GST base, net of discounts) rather than the
    // gross Amount column.
    private static string BillExpTrnKsccSql(int count) => $@"
SELECT t.BillId, t.BillExpSl,
       COALESCE(NULLIF(LTRIM(RTRIM(p.ProductName)),''),
                NULLIF(LTRIM(RTRIM(i.ItemName)),''),
                NULLIF(LTRIM(RTRIM(bo.Remarks)),''),
                CONCAT('Line ', t.BillExpSl)) AS Descr,
       ISNULL(gc.HsnCode,'') AS Hsn,
       COALESCE(NULLIF(t.TaxableAmt,0), t.Amount - ISNULL(t.DiscAmount,0)), t.Quantity, t.Rate,
       t.IGSTPerc, ISNULL(t.IGSTAmt,0), ISNULL(t.CGSTAmt,0), ISNULL(t.SGSTAmt,0),
       t.Amount
FROM Bill_Exp_trn t
LEFT JOIN Bo_trn bo ON t.BoSl = bo.BoSl
LEFT JOIN Product p ON bo.ProductId = p.ProductId
LEFT JOIN Item i ON p.ItemId = i.ItemId
LEFT JOIN GstCategory gc ON i.GstCatId = gc.GstCatId
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillExpSl";

    private static string BillFileTrnSql(string table, int count) => $@"
SELECT t.BillId, t.BillFileSl,
       COALESCE(NULLIF(LTRIM(RTRIM(p.ProductName)),''),
                LTRIM(RTRIM(CONCAT(i.ItemName,
                    CASE WHEN sp.SpecName IS NOT NULL THEN ' - ' + sp.SpecName ELSE '' END,
                    CASE WHEN sz.SizeName IS NOT NULL THEN ' - ' + sz.SizeName ELSE '' END))),
                CONCAT('Line ', t.BillFileSl)) AS Descr,
       ISNULL(h.HsnCode,'') AS Hsn,
       t.Amount, t.Quantity, t.Rate,
       t.IgstRate, t.IgstAmount, 0 AS CgstAmount, 0 AS SgstAmount
FROM [{table}] t
LEFT JOIN Item i ON t.ItemId = i.ItemId
LEFT JOIN HSN h ON i.HsnId = h.HsnId
LEFT JOIN Specification sp ON t.SpecId = sp.SpecId
LEFT JOIN ItemSize sz ON t.SizeId = sz.SizeId
LEFT JOIN Product p ON p.ItemId = t.ItemId AND p.SpecId = t.SpecId
    AND p.SizeId = t.SizeId AND p.DesignId = t.DesignId
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillFileSl";

    private static string BillExpTrnSql(int count) => $@"
SELECT t.BillId, t.BillSl,
       COALESCE(NULLIF(LTRIM(RTRIM(bo.ItemDescription)),''),
                NULLIF(LTRIM(RTRIM(p.ProductName)),''),
                CONCAT('Line ', t.BillSl)) AS Descr,
       ISNULL(h.HsnCode,'') AS Hsn,
       t.Amount, t.Quantity, t.Rate,
       t.IgstRate, t.IgstAmount, ISNULL(t.CgstAmount,0), ISNULL(t.SgstAmount,0)
FROM Bill_Exp_trn t
LEFT JOIN Bo_trn bo ON t.BoSl = bo.BoSl
LEFT JOIN Product p ON bo.ProductId = p.ProductId
LEFT JOIN Item i ON p.ItemId = i.ItemId
LEFT JOIN HSN h ON i.HsnId = h.HsnId
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillSl";

    // Local sales: own HsnId + ProductId on the line.
    private static string BillLsTrnSql(string table, int count) => $@"
SELECT t.BillId, t.BillLsSl,
       COALESCE(NULLIF(LTRIM(RTRIM(p.ProductName)),''),
                LTRIM(RTRIM(CONCAT(i.ItemName,
                    CASE WHEN sp.SpecName IS NOT NULL THEN ' - ' + sp.SpecName ELSE '' END,
                    CASE WHEN sz.SizeName IS NOT NULL THEN ' - ' + sz.SizeName ELSE '' END))),
                CONCAT('Line ', t.BillLsSl)) AS Descr,
       ISNULL(h.HsnCode,'') AS Hsn,
       t.Amount, t.Quantity, t.Rate,
       t.IgstRate, ISNULL(t.IgstAmount,0), ISNULL(t.CgstAmount,0), ISNULL(t.SgstAmount,0)
FROM [{table}] t
LEFT JOIN Item i ON t.ItemId = i.ItemId
LEFT JOIN HSN h ON t.HsnId = h.HsnId
LEFT JOIN Specification sp ON t.SpecId = sp.SpecId
LEFT JOIN ItemSize sz ON t.SizeId = sz.SizeId
LEFT JOIN Product p ON p.ProductId = t.ProductId
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillLsSl";

    // Local purchase: own HsnId; item refs but no ProductId.
    private static string BillLpTrnSql(string table, int count) => $@"
SELECT t.BillId, t.BillLpSl,
       COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(i.ItemName,
                    CASE WHEN sp.SpecName IS NOT NULL THEN ' - ' + sp.SpecName ELSE '' END,
                    CASE WHEN sz.SizeName IS NOT NULL THEN ' - ' + sz.SizeName ELSE '' END))),''),
                CONCAT('Line ', t.BillLpSl)) AS Descr,
       ISNULL(h.HsnCode,'') AS Hsn,
       t.Amount, t.Quantity, t.Rate,
       t.IgstRate, ISNULL(t.IgstAmount,0), ISNULL(t.CgstAmount,0), ISNULL(t.SgstAmount,0)
FROM [{table}] t
LEFT JOIN Item i ON t.ItemId = i.ItemId
LEFT JOIN HSN h ON t.HsnId = h.HsnId
LEFT JOIN Specification sp ON t.SpecId = sp.SpecId
LEFT JOIN ItemSize sz ON t.SizeId = sz.SizeId
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillLpSl";

    // Purchase: amount + CGSTAmt/SGSTAmt/IGSTAmt only (no item/HSN refs).
    private static string BillInpTrnSql(string table, int count) => $@"
SELECT t.BillId, t.BillInpSl,
       CONCAT('Line ', t.BillInpSl) AS Descr,
       '' AS Hsn,
       t.Amount, t.Quantity, t.Rate,
       t.IgstRate, ISNULL(t.IGSTAmt,0), ISNULL(t.CGSTAmt,0), ISNULL(t.SGSTAmt,0)
FROM [{table}] t
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillInpSl";

    // KSCC Bill_Inp_trn (input purchase, doctypes 520/80/85 = bulk of ITC): no
    // item/HSN ref, rate column is IGSTPerc (not IgstRate). Taxable base prefers
    // TaxableAmt but falls back to Amount - DiscAmount when the ERP left
    // TaxableAmt unpopulated (0).
    private static string BillInpTrnKsccSql(int count) => $@"
SELECT t.BillId, t.BillInpSl,
       CONCAT('Line ', t.BillInpSl) AS Descr,
       '' AS Hsn,
       COALESCE(NULLIF(t.TaxableAmt,0), t.Amount - ISNULL(t.DiscAmount,0)), t.Quantity, t.Rate,
       t.IGSTPerc, ISNULL(t.IGSTAmt,0), ISNULL(t.CGSTAmt,0), ISNULL(t.SGSTAmt,0)
FROM Bill_Inp_trn t
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillInpSl";

    // KSCC Bill_DrCr_Items (debit/credit note items, e.g. purchase credit note
    // DocType 900): own TaxableAmt + Cgst/Sgst/IgstAmount + IgstRate, no HSN
    // ref. Amounts positive; the service applies the ITC-reduction sign by
    // GstCategory. Taxable prefers TaxableAmt, else Amount - Discount.
    private static string BillDrCrItemsKsccSql(int count) => $@"
SELECT t.BillId, t.BillDrCrSl,
       COALESCE(NULLIF(LTRIM(RTRIM(t.Description)),''),
                CONCAT('Line ', t.BillDrCrSl)) AS Descr,
       '' AS Hsn,
       COALESCE(NULLIF(t.TaxableAmt,0), t.Amount - ISNULL(t.Discount,0)), t.Quantity, t.Rate,
       t.IgstRate, ISNULL(t.IgstAmount,0), ISNULL(t.CgstAmount,0), ISNULL(t.SgstAmount,0)
FROM Bill_DrCr_Items t
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillDrCrSl";

    // KSCC Bill_Lp_trn (local purchase, doctype 145 = Coir): ItemId on line,
    // rate column is IGSTPerc, HSN via Item.GstCatId -> GstCategory.HsnCode.
    // Same TaxableAmt-or-(Amount-Disc) taxable rule as the other KSCC tables.
    private static string BillLpTrnKsccSql(int count) => $@"
SELECT t.BillId, t.BillLpSl,
       COALESCE(NULLIF(LTRIM(RTRIM(i.ItemName)),''),
                CONCAT('Line ', t.BillLpSl)) AS Descr,
       ISNULL(gc.HsnCode,'') AS Hsn,
       COALESCE(NULLIF(t.TaxableAmt,0), t.Amount - ISNULL(t.DiscAmount,0)), t.Quantity, t.Rate,
       t.IGSTPerc, ISNULL(t.IGSTAmt,0), ISNULL(t.CGSTAmt,0), ISNULL(t.SGSTAmt,0),
       t.Amount
FROM Bill_Lp_trn t
LEFT JOIN Item i ON t.ItemId = i.ItemId
LEFT JOIN GstCategory gc ON i.GstCatId = gc.GstCatId
WHERE t.BillId IN ({BillIdParams(count)})
ORDER BY t.BillId, t.BillLpSl";

    // KSCC Bill_General (general voucher / journal, DocType 930 = expense ITC):
    // double-entry table with no item/HSN ref. GST lives only on the expense
    // Dr lines; filter to non-zero-tax rows so balancing journal postings don't
    // inflate the taxable base. Description from the expense Account name (else
    // Remarks). Taxable prefers TaxableAmt, falls back to the Dr amount. No
    // Quantity/Rate columns on this table → emit 0.
    private static string BillGeneralKsccSql(int count) => $@"
SELECT t.BillId, t.BillGeneralSl,
       COALESCE(NULLIF(LTRIM(RTRIM(ac.AccountName)),''),
                NULLIF(LTRIM(RTRIM(t.Remarks)),''),
                CONCAT('Line ', t.BillGeneralSl)) AS Descr,
       '' AS Hsn,
       COALESCE(NULLIF(t.TaxableAmt,0), t.DrAmount) AS Taxable, 0 AS Quantity, 0 AS Rate,
       t.IGSTRate, ISNULL(t.IGSTAmt,0), ISNULL(t.CGSTAmt,0), ISNULL(t.SGSTAmt,0)
FROM Bill_General t
LEFT JOIN Account ac ON t.AccountId = ac.AccountId
WHERE t.BillId IN ({BillIdParams(count)})
  AND (ISNULL(t.CGSTAmt,0) + ISNULL(t.SGSTAmt,0) + ISNULL(t.IGSTAmt,0)) <> 0
ORDER BY t.BillId, t.BillGeneralSl";

    private static string BillIdParams(int count)
        => string.Join(",", Enumerable.Range(0, count).Select(i => $"@b{i}"));

    private static decimal GetDecimal(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return 0m;
        return Convert.ToDecimal(reader.GetValue(ordinal));
    }

    private static string GetString(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? string.Empty : reader.GetValue(ordinal).ToString() ?? string.Empty;
}

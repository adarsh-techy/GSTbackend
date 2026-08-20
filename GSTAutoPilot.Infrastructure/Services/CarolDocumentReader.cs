using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.CarolERP.Entities;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

// One normalized document header, direction-agnostic. Outward headers come from
// the keyless CarolSalesMas (any header table); inward headers from the typed
// CarolPurchaseMas (Bill_Mas) so supplier GstNo / GstReverse / AcName are
// available for reconciliation.
public sealed record CarolDocHeader(
    int BillId,
    short? DocId,
    DateTime BillDate,
    string? InvNo,
    int? BillNumber,
    string? Suffix,
    short AccountId,
    decimal TotalAmt,
    decimal ExchRate,
    string? SupplyType,
    string? GstNo,
    byte GstReverse,
    string? AcName,
    byte? StateId,
    string GstCategory,
    string TaxMode,
    bool IsOutward,
    // Customer name pulled from the bill HEADER (OtherRef/Title/TosName) — used
    // for cash / walk-in sales where the Account is a generic "Cash" ledger and
    // the real buyer name lives on the bill. Null for inward / when unavailable.
    string? CustomerRef = null);

public sealed record CarolDocBundle(CarolDocHeader Header, List<CarolSalesLine> Lines);

// Central reader for the universal Document Mapping engine. Unions every active
// outward (sales) / inward (purchase) mapping for the resolved tenant, reading
// each mapping's own header table, resolved DocId set (DocType/SubType -> DocId
// via the Documents master) and line table. Falls back to the legacy
// Tenant.Sales* profile when a tenant has no active mappings yet.
public class CarolDocumentReader
{
    private readonly CarolERPDbContext _carol;
    private readonly SalesLineProvider _lineProvider;

    public CarolDocumentReader(CarolERPDbContext carol, SalesLineProvider lineProvider)
    {
        _carol = carol;
        _lineProvider = lineProvider;
    }

    private sealed record ResolvedMap(
        string HeaderTable,
        string LineTable,
        IReadOnlyList<short>? DocIds, // null => no DocId filter (all)
        string GstCategory,
        string TaxMode);

    private async Task<List<ResolvedMap>> ResolveAsync(bool outward, CancellationToken ct)
    {
        var active = outward ? _carol.ActiveOutwardMappings : _carol.ActiveInwardMappings;
        if (active.Count == 0)
        {
            // The tenant configured mappings for this direction but disabled them
            // all -> honour that as "no data" (do NOT fall back to reading the
            // whole Bill_Mas table). This is what makes "disable everything +
            // rely on the SP" work: when the SP for a direction isn't set and the
            // mappings are off, the app shows nothing rather than the whole table.
            var hasRows = outward ? _carol.HasAnyOutwardMappings : _carol.HasAnyInwardMappings;
            if (hasRows) return new List<ResolvedMap>();

            // Legacy fallback so genuinely unseeded tenants (no mapping rows at
            // all) behave as before.
            if (outward)
            {
                var d = _carol.LegacySalesDocId;
                return new List<ResolvedMap>
                {
                    new(_carol.LegacySalesHeaderTable, _carol.LegacySalesLineTable,
                        d.HasValue ? new[] { (short)d.Value } : null, "Sales", "AUTO"),
                };
            }
            return new List<ResolvedMap>
            {
                new("Bill_Mas", _carol.DefaultInwardLineTable, null, "Purchase", "AUTO"),
            };
        }

        var list = new List<ResolvedMap>();
        foreach (var m in active)
        {
            var ids = await _carol.ResolveDocIdsAsync(m.DocTypes, m.SubTypes, ct);
            list.Add(new ResolvedMap(m.HeaderTable, m.LineTable, ids, m.GstCategory, m.TaxMode));
        }
        return list;
    }

    public async Task<IReadOnlyList<CarolDocBundle>> ReadOutwardAsync(int year, int month, CancellationToken ct = default)
    {
        var (start, end) = Period(year, month);
        var maps = await ResolveAsync(outward: true, ct);
        var sanctionIds = await _carol.SanctionRequiredDocIdsAsync(ct);
        var companyIds = await ResolveCompanyDocIdsAsync(ct);
        var bundles = new List<CarolDocBundle>();

        foreach (var m in maps)
        {
            var q = _carol.HeadersFromTable(m.HeaderTable)
                .Where(h => h.BillDate >= start && h.BillDate < end);
            if (m.DocIds is not null)
            {
                var ids = m.DocIds.ToArray();
                q = q.Where(h => h.DocId != null && ids.Contains(h.DocId.Value));
            }
            q = ApplySanctionFilter(q, sanctionIds);
            q = ApplyCompanyFilter(q, companyIds);
            var headers = await q.ToListAsync(ct);
            if (headers.Count == 0) continue;

            var billIds = headers.Select(h => h.BillId).ToList();
            var exch = headers.ToDictionary(h => h.BillId, h => h.ExchRate == 0 ? 1m : h.ExchRate);
            var linesByBill = await _lineProvider.GetLinesAsync(m.LineTable, billIds, exch, ct);
            var extras = await ReadHeaderExtrasAsync(m.HeaderTable, billIds, ct);

            foreach (var h in headers)
            {
                var lines = linesByBill.TryGetValue(h.BillId, out var ls) ? ls : new List<CarolSalesLine>();
                var ex = extras.TryGetValue(h.BillId, out var e) ? e : null;
                bundles.Add(new CarolDocBundle(OutwardHeader(h, m, ex), lines));
            }
        }
        return await ExcludeIntraStateIntercompanyAsync(bundles, ct);
    }

    // KSCC only: DocType 30 / SubType 6 = "Invoice - Intercompany". A transfer
    // between branches in the SAME state (i.e. the buyer account's state equals
    // the selling company's state — one GST registration) is NOT a taxable
    // supply, so it must not appear in the invoice list / GSTR-1 / GSTR-3B.
    // Inter-state transfers (account state != company state → a different GSTIN)
    // ARE deemed supplies and are kept. Account.StateId is smallint on KSCC (the
    // entity ignores it), so it's read via raw SQL here.
    private async Task<List<CarolDocBundle>> ExcludeIntraStateIntercompanyAsync(List<CarolDocBundle> bundles, CancellationToken ct)
    {
        if (!string.Equals(_carol.Flavor, "KSCC", StringComparison.OrdinalIgnoreCase)) return bundles;
        var interIds = await _carol.ResolveDocIdsAsync("30", "6", ct);
        if (interIds is null || interIds.Count == 0) return bundles;
        var interSet = interIds.ToHashSet();

        var interBundles = bundles.Where(b => b.Header.DocId is short d && interSet.Contains(d)).ToList();
        if (interBundles.Count == 0) return bundles;

        // Selling company's state per bill: DocId -> Documents.CoId -> company.StateId.
        var docToCo = await _carol.DocIdToCompanyMapAsync(ct);
        var coState = (await _carol.ListCompaniesAsync(ct)).ToDictionary(c => c.CoId, c => c.StateId);
        // Buyer account state.
        var accountIds = interBundles.Select(b => b.Header.AccountId).Distinct().ToArray();
        var acctState = await ReadAccountStatesAsync(accountIds, ct);

        var drop = new HashSet<int>();
        foreach (var b in interBundles)
        {
            var docId = b.Header.DocId!.Value;
            int? sellerState = docToCo.TryGetValue(docId, out var co) && coState.TryGetValue(co, out var ss) ? ss : null;
            int? buyerState = acctState.TryGetValue(b.Header.AccountId, out var bs) ? bs : null;
            // Only drop when BOTH states are known and equal (intra-state).
            if (sellerState.HasValue && buyerState.HasValue && sellerState.Value == buyerState.Value)
                drop.Add(b.Header.BillId);
        }
        return drop.Count == 0 ? bundles : bundles.Where(b => !drop.Contains(b.Header.BillId)).ToList();
    }

    // AccountId -> StateId via raw SQL (KSCC's Account.StateId is smallint and
    // the entity ignores it). Returns empty when the column is absent.
    private async Task<Dictionary<short, int?>> ReadAccountStatesAsync(short[] accountIds, CancellationToken ct)
    {
        var result = new Dictionary<short, int?>();
        if (accountIds.Length == 0 || !await _carol.ColumnExistsAsync("Account", "StateId", ct)) return result;
        var paramNames = string.Join(",", Enumerable.Range(0, accountIds.Length).Select(i => $"@a{i}"));
        var sql = $"SELECT AccountId, CAST(StateId AS int) AS StateId FROM Account WHERE AccountId IN ({paramNames})";
        var conn = (Microsoft.Data.SqlClient.SqlConnection)_carol.Database.GetDbConnection();
        var shouldClose = conn.State != System.Data.ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < accountIds.Length; i++)
                cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter($"@a{i}", accountIds[i]));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var acctId = Convert.ToInt16(reader.GetValue(0));
                int? state = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1));
                result[acctId] = state;
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
        return result;
    }

    public async Task<IReadOnlyList<CarolDocBundle>> ReadInwardAsync(int year, int month, CancellationToken ct = default)
    {
        var (start, end) = Period(year, month);
        var maps = await ResolveAsync(outward: false, ct);
        var sanctionIds = await _carol.SanctionRequiredDocIdsAsync(ct);
        var companyIds = await ResolveCompanyDocIdsAsync(ct);
        var bundles = new List<CarolDocBundle>();

        foreach (var m in maps)
        {
            var q = _carol.PurchaseHeaders.AsNoTracking()
                .Where(h => h.BillDate >= start && h.BillDate < end);
            if (m.DocIds is not null)
            {
                var ids = m.DocIds.ToArray();
                q = q.Where(h => h.DocId != null && ids.Contains(h.DocId.Value));
            }
            q = ApplySanctionFilter(q, sanctionIds);
            q = ApplyCompanyFilter(q, companyIds);
            var headers = await q.ToListAsync(ct);
            if (headers.Count == 0) continue;

            var billIds = headers.Select(h => h.BillId).ToList();
            var exch = headers.ToDictionary(h => h.BillId, h => h.ExchRate == 0 ? 1m : h.ExchRate);
            var linesByBill = await _lineProvider.GetLinesAsync(m.LineTable, billIds, exch, ct);

            foreach (var h in headers)
            {
                var lines = linesByBill.TryGetValue(h.BillId, out var ls) ? ls : new List<CarolSalesLine>();
                bundles.Add(new CarolDocBundle(InwardHeader(h, m), lines));
            }
        }
        return bundles;
    }

    // Single outward document (for PDF / detail view): find which active outward
    // mapping covers this bill, then read its lines from that mapping's table.
    public async Task<CarolDocBundle?> ReadOutwardByBillIdAsync(int billId, CancellationToken ct = default)
    {
        var maps = await ResolveAsync(outward: true, ct);
        var sanctionIds = await _carol.SanctionRequiredDocIdsAsync(ct);
        var companyIds = await ResolveCompanyDocIdsAsync(ct);
        foreach (var m in maps)
        {
            var q = _carol.HeadersFromTable(m.HeaderTable).Where(h => h.BillId == billId);
            if (m.DocIds is not null)
            {
                var ids = m.DocIds.ToArray();
                q = q.Where(h => h.DocId != null && ids.Contains(h.DocId.Value));
            }
            q = ApplySanctionFilter(q, sanctionIds);
            q = ApplyCompanyFilter(q, companyIds);
            var header = await q.FirstOrDefaultAsync(ct);
            if (header is null) continue;

            var exch = new Dictionary<int, decimal> { [billId] = header.ExchRate == 0 ? 1m : header.ExchRate };
            var linesByBill = await _lineProvider.GetLinesAsync(m.LineTable, new[] { billId }, exch, ct);
            var lines = linesByBill.TryGetValue(billId, out var ls) ? ls : new List<CarolSalesLine>();
            var extras = await ReadHeaderExtrasAsync(m.HeaderTable, new[] { billId }, ct);
            var ex = extras.TryGetValue(billId, out var e) ? e : null;
            return new CarolDocBundle(OutwardHeader(header, m, ex), lines);
        }
        return null;
    }

    // Single outward document with its RAW CarolSalesMas header (for the PDF,
    // which needs IRN / AckNo / EwbNo / SignedQRCode) plus normalized lines
    // from the matching mapping's line table.
    public async Task<(CarolSalesMas Header, IReadOnlyList<CarolSalesLine> Lines, HeaderExtras? Extras, string? Prefix)?> ReadOutwardRawByBillIdAsync(
        int billId, CancellationToken ct = default)
    {
        var maps = await ResolveAsync(outward: true, ct);
        var sanctionIds = await _carol.SanctionRequiredDocIdsAsync(ct);
        var companyIds = await ResolveCompanyDocIdsAsync(ct);
        foreach (var m in maps)
        {
            var q = _carol.HeadersFromTable(m.HeaderTable).Where(h => h.BillId == billId);
            if (m.DocIds is not null)
            {
                var ids = m.DocIds.ToArray();
                q = q.Where(h => h.DocId != null && ids.Contains(h.DocId.Value));
            }
            q = ApplySanctionFilter(q, sanctionIds);
            q = ApplyCompanyFilter(q, companyIds);
            var header = await q.FirstOrDefaultAsync(ct);
            if (header is null) continue;

            var exch = new Dictionary<int, decimal> { [billId] = header.ExchRate == 0 ? 1m : header.ExchRate };
            var linesByBill = await _lineProvider.GetLinesAsync(m.LineTable, new[] { billId }, exch, ct);
            var lines = linesByBill.TryGetValue(billId, out var ls) ? ls : new List<CarolSalesLine>();
            var extrasMap = await ReadHeaderExtrasAsync(m.HeaderTable, new[] { billId }, ct);
            var extras = extrasMap.TryGetValue(billId, out var e) ? e : null;
            var prefixMap = await _carol.DocIdToPrefixMapAsync(ct);
            var prefix = header.DocId is short d && prefixMap.TryGetValue(d, out var p) ? p : null;
            return (header, lines, extras, prefix);
        }
        return null;
    }

    public async Task<Dictionary<string, int>> OutwardCountsByPeriodAsync(CancellationToken ct = default)
    {
        var maps = await ResolveAsync(outward: true, ct);
        var sanctionIds = await _carol.SanctionRequiredDocIdsAsync(ct);
        var companyIds = await ResolveCompanyDocIdsAsync(ct);
        var counts = new Dictionary<string, int>();
        foreach (var m in maps)
        {
            var q = _carol.HeadersFromTable(m.HeaderTable);
            if (m.DocIds is not null)
            {
                var ids = m.DocIds.ToArray();
                q = q.Where(h => h.DocId != null && ids.Contains(h.DocId.Value));
            }
            q = ApplySanctionFilter(q, sanctionIds);
            q = ApplyCompanyFilter(q, companyIds);
            var grouped = await q.GroupBy(h => new { h.BillDate.Year, h.BillDate.Month })
                .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
                .ToListAsync(ct);
            Accumulate(counts, grouped.Select(g => ($"{g.Year:D4}{g.Month:D2}", g.Count)));
        }
        return counts;
    }

    public async Task<Dictionary<string, int>> InwardCountsByPeriodAsync(CancellationToken ct = default)
    {
        var maps = await ResolveAsync(outward: false, ct);
        var sanctionIds = await _carol.SanctionRequiredDocIdsAsync(ct);
        var companyIds = await ResolveCompanyDocIdsAsync(ct);
        var counts = new Dictionary<string, int>();
        foreach (var m in maps)
        {
            var q = _carol.PurchaseHeaders.AsNoTracking().AsQueryable();
            if (m.DocIds is not null)
            {
                var ids = m.DocIds.ToArray();
                q = q.Where(h => h.DocId != null && ids.Contains(h.DocId.Value));
            }
            q = ApplySanctionFilter(q, sanctionIds);
            q = ApplyCompanyFilter(q, companyIds);
            var grouped = await q.GroupBy(h => new { h.BillDate.Year, h.BillDate.Month })
                .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
                .ToListAsync(ct);
            Accumulate(counts, grouped.Select(g => ($"{g.Year:D4}{g.Month:D2}", g.Count)));
        }
        return counts;
    }

    // SANCTION/APPROVAL filter — applied uniformly to every Bill_Mas /
    // Bill_File_mas read so unapproved (Sanctioned=0) bills don't bleed into
    // invoice lists, GSTR-1/3B figures, or reconciliation. Rule (per DocId,
    // NOT DocType — a single DocType can contain multiple Documents rows
    // with mixed Sanction values, so the join must be header.DocId →
    // Documents.DocId, never header.DocId → DocType):
    //   IF the row's Documents.DocId has Sanction=1 (this specific document
    //   type requires approval) THEN include only when Header.Sanctioned=1
    //   ELSE include regardless of Sanctioned.
    // Both Bill_Mas and Bill_File_mas carry the Sanctioned tinyint column;
    // CarolPurchaseMas + CarolSalesMas map it as `byte`. The `ids` set is
    // resolved upstream via CarolERPDbContext.SanctionRequiredDocIdsAsync.
    private static IQueryable<CarolSalesMas> ApplySanctionFilter(IQueryable<CarolSalesMas> q, HashSet<short> sanctionRequiredDocIds)
    {
        if (sanctionRequiredDocIds.Count == 0) return q;
        var ids = sanctionRequiredDocIds.ToArray();
        return q.Where(h => h.DocId == null || !ids.Contains(h.DocId.Value) || h.Sanctioned == 1);
    }

    private static IQueryable<CarolPurchaseMas> ApplySanctionFilter(IQueryable<CarolPurchaseMas> q, HashSet<short> sanctionRequiredDocIds)
    {
        if (sanctionRequiredDocIds.Count == 0) return q;
        var ids = sanctionRequiredDocIds.ToArray();
        return q.Where(h => h.DocId == null || !ids.Contains(h.DocId.Value) || h.Sanctioned == 1);
    }

    // COMPANY filter (multi-company tenants) — resolves which DocIds belong
    // to the active GST GROUP (from X-Company-Id header) and restricts reads
    // to those. The X-Company-Id value is the REPRESENTATIVE CoId of a GST
    // group (sister branches sharing one GSTIN collapse into one group in
    // /api/companies). Here we expand it back: find the group containing
    // that CoId, then union DocIds for every member CoId. null = "All
    // companies" (no filter). Bill_Mas / Bill_File_mas don't carry CoId;
    // the relationship is header.DocId → Documents.CoId, same as the
    // sanction lookup.
    private async Task<HashSet<short>?> ResolveCompanyDocIdsAsync(CancellationToken ct)
    {
        var co = _carol.ActiveCompanyId;
        if (co is null) return null;
        var groups = await _carol.CompanyGroupsAsync(ct);
        // Find the group the active CoId belongs to (it may be the
        // representative OR any member — sister branches all map to the
        // same group via shared/inherited GSTIN).
        var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(co.Value));
        if (group is null) return new HashSet<short>(); // unknown CoId → no rows
        var union = new HashSet<short>();
        foreach (var memberCoId in group.MemberCoIds)
        {
            foreach (var docId in await _carol.DocIdsForCompanyAsync(memberCoId, ct))
                union.Add(docId);
        }
        return union;
    }

    private static IQueryable<CarolSalesMas> ApplyCompanyFilter(IQueryable<CarolSalesMas> q, HashSet<short>? companyDocIds)
    {
        if (companyDocIds is null) return q; // null = "All companies"
        if (companyDocIds.Count == 0)
        {
            // Active company has no DocIds at all → return nothing rather than
            // accidentally widening the result (e.g. if config is half-done).
            return q.Where(_ => false);
        }
        var ids = companyDocIds.ToArray();
        return q.Where(h => h.DocId != null && ids.Contains(h.DocId.Value));
    }

    private static IQueryable<CarolPurchaseMas> ApplyCompanyFilter(IQueryable<CarolPurchaseMas> q, HashSet<short>? companyDocIds)
    {
        if (companyDocIds is null) return q;
        if (companyDocIds.Count == 0) return q.Where(_ => false);
        var ids = companyDocIds.ToArray();
        return q.Where(h => h.DocId != null && ids.Contains(h.DocId.Value));
    }

    private static void Accumulate(Dictionary<string, int> counts, IEnumerable<(string Key, int Count)> items)
    {
        foreach (var (key, count) in items)
            counts[key] = counts.GetValueOrDefault(key) + count;
    }

    private static CarolDocHeader OutwardHeader(CarolSalesMas h, ResolvedMap m, HeaderExtras? extras = null) => new(
        h.BillId, h.DocId, h.BillDate, h.InvNo, h.BillNumber, h.Suffix,
        h.AccountId, h.TotalAmt, h.ExchRate, h.SupplyType,
        GstNo: extras?.GstNo, GstReverse: 0, AcName: null, StateId: null,
        m.GstCategory, m.TaxMode, IsOutward: true, CustomerRef: extras?.CustomerRef);

    // Per-bill customer/GST fields lifted from the outward HEADER table. The
    // header carries the buyer GSTIN (used to split B2B vs B2C) and, for cash
    // sales, the real buyer name (OtherRef/Title/TosName) that the generic
    // "Cash" Account ledger hides.
    public sealed record HeaderExtras(string? GstNo, string? CustomerRef);

    // Net invoice-level adjustment (round-off / misc charges / discount) from
    // CarolERP Bill_Tax, already signed per Tax.ValEffect (1 = add, 2 = subtract).
    // Label is the Tax.TaxName(s), e.g. "Round Off(-)".
    public sealed record RoundOffInfo(decimal Amount, string Label);

    // Reads the signed invoice-level adjustment for a set of bills from
    // Bill_Tax (amount) joined to Tax (name + ValEffect). Tolerant of installs
    // that don't have these tables/columns: probes first and returns an empty
    // map, so totals stay as taxable + tax. ValEffect 1 => +, 2 => -, else 0.
    public async Task<Dictionary<int, RoundOffInfo>> ReadRoundOffAsync(
        IReadOnlyCollection<int> billIds, CancellationToken ct)
    {
        var result = new Dictionary<int, RoundOffInfo>();
        if (billIds.Count == 0) return result;
        if (!await _carol.ColumnExistsAsync("Bill_Tax", "TaxAmount", ct)
            || !await _carol.ColumnExistsAsync("Tax", "ValEffect", ct))
            return result;

        var idArray = billIds.ToArray();
        var paramNames = string.Join(",", Enumerable.Range(0, idArray.Length).Select(i => $"@b{i}"));
        var sql = $@"SELECT bt.BillId, ISNULL(bt.TaxAmount,0) AS Amount,
                            ISNULL(t.TaxName,'') AS TaxName, ISNULL(t.ValEffect,0) AS ValEffect
                     FROM Bill_Tax bt LEFT JOIN Tax t ON bt.TaxId = t.TaxId
                     WHERE bt.BillId IN ({paramNames})";

        // Accumulate signed amount + distinct labels per bill.
        var acc = new Dictionary<int, (decimal Amount, List<string> Labels)>();
        var conn = (Microsoft.Data.SqlClient.SqlConnection)_carol.Database.GetDbConnection();
        var shouldClose = conn.State != System.Data.ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < idArray.Length; i++)
                cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter($"@b{i}", idArray[i]));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var billId = reader.GetInt32(0);
                var amount = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1));
                var name = reader.IsDBNull(2) ? string.Empty : reader.GetValue(2).ToString() ?? string.Empty;
                var valEffect = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
                var signed = valEffect == 1 ? amount : valEffect == 2 ? -amount : 0m;
                if (signed == 0m && string.IsNullOrWhiteSpace(name)) continue;
                if (!acc.TryGetValue(billId, out var cur))
                    cur = (0m, new List<string>());
                cur.Amount += signed;
                if (!string.IsNullOrWhiteSpace(name) && !cur.Labels.Contains(name))
                    cur.Labels.Add(name.Trim());
                acc[billId] = cur;
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }

        foreach (var (billId, v) in acc)
            result[billId] = new RoundOffInfo(
                decimal.Round(v.Amount, 2),
                v.Labels.Count > 0 ? string.Join(", ", v.Labels) : "Round Off");
        return result;
    }

    // Reads header GstNo + customer-ref for a set of bills from any outward
    // header table, tolerant of schema drift: each column is probed and only
    // SELECTed when it exists (KSCC Bill_Mas has GstNo/OtherRef/Title/TosName;
    // Bill_File_mas has no GstNo; other flavors may have none). Returns an empty
    // map when no relevant column exists, so callers behave as before.
    private async Task<Dictionary<int, HeaderExtras>> ReadHeaderExtrasAsync(
        string headerTable, IReadOnlyCollection<int> billIds, CancellationToken ct)
    {
        var result = new Dictionary<int, HeaderExtras>();
        if (billIds.Count == 0) return result;
        var table = CarolERPDbContext.ValidateTableName(headerTable);

        var hasGst = await _carol.ColumnExistsAsync(table, "GstNo", ct);
        var refCols = new List<string>();
        foreach (var c in new[] { "OtherRef", "Title", "TosName" })
            if (await _carol.ColumnExistsAsync(table, c, ct)) refCols.Add(c);
        if (!hasGst && refCols.Count == 0) return result; // nothing to add

        var gstExpr = hasGst ? "GstNo" : "CAST(NULL AS varchar(50))";
        var refExpr = refCols.Count > 0
            ? "COALESCE(" + string.Join(", ", refCols.Select(c => $"NULLIF(LTRIM(RTRIM([{c}])),'')")) + ")"
            : "CAST(NULL AS varchar(200))";

        var idArray = billIds.ToArray();
        var paramNames = string.Join(",", Enumerable.Range(0, idArray.Length).Select(i => $"@b{i}"));
        var sql = $"SELECT BillId, {gstExpr} AS GstNo, {refExpr} AS CustomerRef FROM [{table}] WHERE BillId IN ({paramNames})";

        var conn = (Microsoft.Data.SqlClient.SqlConnection)_carol.Database.GetDbConnection();
        var shouldClose = conn.State != System.Data.ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < idArray.Length; i++)
                cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter($"@b{i}", idArray[i]));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var billId = reader.GetInt32(0);
                var gst = reader.IsDBNull(1) ? null : reader.GetValue(1)?.ToString();
                var cref = reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString();
                result[billId] = new HeaderExtras(gst, cref);
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
        return result;
    }

    private static CarolDocHeader InwardHeader(CarolPurchaseMas h, ResolvedMap m) => new(
        h.BillId, h.DocId, h.BillDate, h.InvNo, h.BillNumber, h.Suffix,
        h.AccountId, h.TotalAmt, h.ExchRate, SupplyType: null,
        h.GstNo, h.GstReverse, h.AcName, h.StateId,
        m.GstCategory, m.TaxMode, IsOutward: false);

    private static (DateTime Start, DateTime End) Period(int year, int month)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (start, start.AddMonths(1));
    }
}

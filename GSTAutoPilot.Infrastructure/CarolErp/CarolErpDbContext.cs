using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GSTAutoPilot.Infrastructure.CarolERP;

// Per-flavor compiled model cache key. EF normally caches the compiled model
// based on the DbContext type alone, but our OnModelCreating branches on the
// resolved tenant's flavor (which controls which columns CarolSalesMas maps
// to) and the per-tenant sales table name. We must include both in the
// cache key, otherwise the FIRST request's flavor wins and locks in the
// wrong mapping for every other tenant.
internal sealed class CarolErpModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        var c = (CarolERPDbContext)context;
        return (typeof(CarolERPDbContext), c.Flavor, c.LegacySalesHeaderTable, designTime);
    }
}

// READ ONLY - Never write to CarolERP.
// CarolERP is the customer's authoritative ERP database; this app pulls
// invoice/purchase data live and must not mutate it. Do not call SaveChanges
// on this context. Tracking is disabled at the context level so accidental
// updates throw at query-time.
//
// The sales HEADER table name is per-tenant (KSCC=Bill_File_mas,
// Flooratex=Bill_Mas) — the columns are identical across installs, so we just
// vary ToTable() from the resolved tenant's profile. A custom
// IModelCacheKeyFactory keeps a separate compiled model per distinct table
// name. The sales LINE table is intentionally NOT remapped yet (those schemas
// differ structurally); flooratex line reads come back empty and totals fall
// back to the header amount.
public class CarolERPDbContext : DbContext
{
    private const string DefaultSalesHeaderTable = "Bill_File_mas";
    private const string DefaultSalesLineTable = "Bill_File_trn";
    private const string DefaultPurchaseLineTable = "Bill_Inp_trn";
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public CarolERPDbContext(DbContextOptions<CarolERPDbContext> options, IHttpContextAccessor? httpContextAccessor = null)
        : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private Tenant? ResolvedTenant => _httpContextAccessor?.HttpContext?.Items["Tenant"] as Tenant;

    // Active company filter for this request. null = "All companies" (no
    // company filter). Set by TenantMiddleware from the X-Company-Id header.
    // CarolERP's Bill_Mas / Bill_File_mas don't carry CoId; the bill's
    // company is resolved through Documents.CoId via the header's DocId, so
    // the filter is "header.DocId IN (Documents.DocId where CoId = active)".
    public byte? ActiveCompanyId =>
        _httpContextAccessor?.HttpContext?.Items["CompanyId"] is byte b ? b : null;

    // Schema flavor for the resolved tenant — drives which physical column
    // name to target for the few drift'd CarolERP columns (company.GstNo vs
    // GSTNumber, Documents.Sanction vs SanctionRequired). "Default" matches
    // Flooratex; "KSCC" matches the Coir Corp install style.
    public string Flavor => ResolvedTenant?.CarolErpFlavor ?? "Default";

    // Physical column name for the "this DocType requires approval" flag on
    // the Documents master.
    public string DocumentSanctionColumn =>
        string.Equals(Flavor, "KSCC", StringComparison.OrdinalIgnoreCase)
            ? "SanctionRequired"
            : "Sanction";

    // Physical column name for the company GSTIN on the `company` master.
    public string CompanyGstColumn =>
        string.Equals(Flavor, "KSCC", StringComparison.OrdinalIgnoreCase)
            ? "GSTNumber"
            : "GstNo";

    // The universal Document Mapping rows for the resolved tenant, stashed by
    // TenantMiddleware. Single source of truth for the read path; the legacy
    // Tenant.Sales* columns are only a fallback for tenants with no mappings.
    private IReadOnlyList<DocumentMapping> ResolvedMappings =>
        _httpContextAccessor?.HttpContext?.Items["DocumentMappings"] as IReadOnlyList<DocumentMapping>
        ?? Array.Empty<DocumentMapping>();

    // Active outward (sales) / inward (purchase) mappings, in display order.
    public IReadOnlyList<DocumentMapping> ActiveOutwardMappings =>
        ResolvedMappings.Where(m => m.IsActive && m.IsOutward).OrderBy(m => m.SortOrder).ToList();

    public IReadOnlyList<DocumentMapping> ActiveInwardMappings =>
        ResolvedMappings.Where(m => m.IsActive && !m.IsOutward).OrderBy(m => m.SortOrder).ToList();

    // Whether the tenant has ANY mapping row for a direction (active or not).
    // Lets the reader distinguish "configured but all turned off" (=> the tenant
    // means no data) from "never seeded" (=> legacy whole-table fallback).
    public bool HasAnyOutwardMappings => ResolvedMappings.Any(m => m.IsOutward);
    public bool HasAnyInwardMappings => ResolvedMappings.Any(m => !m.IsOutward);

    // Legacy fallback (tenants not yet seeded with mappings): the original
    // three-column sales profile + an all-purchases inward default.
    public string LegacySalesHeaderTable =>
        string.IsNullOrWhiteSpace(ResolvedTenant?.SalesHeaderTable) ? DefaultSalesHeaderTable : ResolvedTenant!.SalesHeaderTable;
    public int? LegacySalesDocId => ResolvedTenant?.SalesDocId;
    public string LegacySalesLineTable =>
        string.IsNullOrWhiteSpace(ResolvedTenant?.SalesLineTable) ? DefaultSalesLineTable : ResolvedTenant!.SalesLineTable;
    public string DefaultInwardLineTable => DefaultPurchaseLineTable;

    // Resolve a mapping's portable DocType/SubType codes to this install's
    // header DocId set via the Documents master. Returns null when no DocType
    // filter is configured (=> caller applies no DocId filter); an (possibly
    // empty) list otherwise.
    public async Task<IReadOnlyList<short>?> ResolveDocIdsAsync(string? docTypes, string? subTypes, CancellationToken ct = default)
    {
        var types = ParseShorts(docTypes);
        if (types.Count == 0) return null;
        var subs = ParseShorts(subTypes).Select(s => (byte)s).ToList();
        var q = Documents.AsNoTracking().Where(d => types.Contains(d.DocType));
        if (subs.Count > 0) q = q.Where(d => subs.Contains(d.SubType));
        return await q.Select(d => d.DocId).Distinct().ToListAsync(ct);
    }

    // Per-DocId set where the "this doctype requires per-bill approval" flag
    // is set on the Documents master. Column drifts (`Sanction` vs
    // `SanctionRequired`) so we issue raw SQL with the flavor-specific name.
    // Documents is small (~30-400 rows), so loading the whole set per request
    // is fine.
    public async Task<HashSet<short>> SanctionRequiredDocIdsAsync(CancellationToken ct = default)
    {
        var col = DocumentSanctionColumn;
        // Column name comes from a 2-value allow-list, not user input, so the
        // interpolation is safe despite the FromSqlRaw warning.
#pragma warning disable EF1002
        var ids = await Database.SqlQueryRaw<short>(
            $"SELECT CAST(DocId AS smallint) AS Value FROM Documents WHERE {col} = 1")
            .ToListAsync(ct);
#pragma warning restore EF1002
        return ids.ToHashSet();
    }

    // CoId → GstNo using whatever column name this tenant's `company` table
    // exposes (GstNo on Flooratex, GSTNumber on KSCC). Raw-SQL projection so
    // EF doesn't try to read both columns; column name is from a 2-value
    // allow-list (DocumentSanctionColumn / CompanyGstColumn).
    public async Task<IReadOnlyDictionary<byte, string?>> CompanyGstinsAsync(CancellationToken ct = default)
    {
        var col = CompanyGstColumn;
#pragma warning disable EF1002
        var rows = await Database.SqlQueryRaw<CoGstRow>(
            $"SELECT CAST(CoId AS tinyint) AS CoId, {col} AS GstNo FROM company")
            .ToListAsync(ct);
#pragma warning restore EF1002
        return rows.ToDictionary(r => r.CoId, r => r.GstNo);
    }

    private sealed class CoGstRow { public byte CoId { get; set; } public string? GstNo { get; set; } }

    // Cross-flavor company list. The `company` table differs in TYPE for
    // StateId (Flooratex tinyint, KSCC smallint) and in column NAME for
    // GstNo vs GSTNumber, so the cleanest portable read is a CAST'd raw SQL
    // projection. Returns one row per `company` table row (NOT deduped).
    // Buyer email per GSTIN, from the ERP account master, for addressing bulk
    // e-Invoice emails. Keyed by GSTIN because the outward SP hands us the
    // counter-party's GSTIN but no AccountId, and e-invoicing is B2B-only so a
    // GSTIN is always present on the rows that matter.
    //
    // The email column is optional across CarolERP installs, so its presence is
    // tested in SQL rather than assumed — a flavour without it yields no emails
    // instead of a broken query.
    public async Task<IReadOnlyDictionary<string, string>> AccountEmailsByGstinAsync(CancellationToken ct = default)
    {
        const string col = "AcEmail";
        var gstCol = string.Equals(Flavor, "KSCC", StringComparison.OrdinalIgnoreCase) ? "GSTNumber" : "GstNo";
#pragma warning disable EF1002
        var rows = await Database.SqlQueryRaw<AccountEmailRow>($@"
IF COL_LENGTH('Account','{col}') IS NOT NULL AND COL_LENGTH('Account','{gstCol}') IS NOT NULL
    SELECT LTRIM(RTRIM({gstCol})) AS Gstin, LTRIM(RTRIM({col})) AS Email
    FROM Account
    WHERE {col} IS NOT NULL AND LTRIM(RTRIM({col})) <> ''
      AND {gstCol} IS NOT NULL AND LEN(LTRIM(RTRIM({gstCol}))) = 15
ELSE
    SELECT CAST(NULL AS nvarchar(20)) AS Gstin, CAST(NULL AS nvarchar(200)) AS Email WHERE 1 = 0")
            .ToListAsync(ct);
#pragma warning restore EF1002

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Gstin) || string.IsNullOrWhiteSpace(r.Email)) continue;
            // Several account rows can share a GSTIN (branches); first wins.
            map.TryAdd(r.Gstin!.ToUpperInvariant(), r.Email!);
        }
        return map;
    }

    public sealed class AccountEmailRow
    {
        public string? Gstin { get; set; }
        public string? Email { get; set; }
    }

    public async Task<IReadOnlyList<CompanyListRow>> ListCompaniesAsync(CancellationToken ct = default)
    {
        var col = CompanyGstColumn;
        var isKscc = string.Equals(Flavor, "KSCC", StringComparison.OrdinalIgnoreCase);
        // KSCC has a single Email column and lacks the Flooratex-only bank /
        // pin / IECode / BankAccName columns — project NULL placeholders so
        // both flavors deserialise into the same CompanyListRow shape.
        var emailExpr = isKscc
            ? "Email"
            : "COALESCE(EmailSales, EmailPurchase)";
        var floortexOnly = isKscc
            ? "CAST(NULL AS nvarchar(100)) AS BankName, CAST(NULL AS nvarchar(50)) AS AccountNo, CAST(NULL AS nvarchar(50)) AS IFSCCode, CAST(NULL AS nvarchar(100)) AS BranchName, CAST(NULL AS nvarchar(20)) AS PinCode, CAST(NULL AS nvarchar(50)) AS IECode, CAST(NULL AS nvarchar(100)) AS BankAccName"
            : "BankName, AccountNo, IFSCode AS IFSCCode, BranchName, PinCode, IECode, BankAccName";
#pragma warning disable EF1002
        var rows = await Database.SqlQueryRaw<CompanyListRow>(
            $"SELECT CAST(CoId AS tinyint) AS CoId, CoName, {col} AS GstNo, CAST(StateId AS int) AS StateId, CoAddr1 AS Address1, CoAddr2 AS Address2, CoAddr3 AS Address3, TelNo AS Phone, PanNo AS Pan, {emailExpr} AS Email, {floortexOnly} FROM company ORDER BY CoId")
            .ToListAsync(ct);
#pragma warning restore EF1002
        return rows;
    }

    public sealed class CompanyListRow
    {
        public byte CoId { get; set; }
        public string? CoName { get; set; }
        public string? GstNo { get; set; }
        public int? StateId { get; set; }
        public string? Address1 { get; set; }
        public string? Address2 { get; set; }
        public string? Address3 { get; set; }
        public string? Phone { get; set; }
        public string? Pan { get; set; }
        public string? Email { get; set; }
        public string? BankName { get; set; }
        public string? AccountNo { get; set; }
        public string? IFSCCode { get; set; }
        public string? BranchName { get; set; }
        public string? PinCode { get; set; }
        public string? IECode { get; set; }
        public string? BankAccName { get; set; }
    }

    // Companies grouped by EFFECTIVE GST. CarolERP tenants often have many
    // physical company rows for branches/showrooms but only 1-2 distinct
    // GST registrations (sister branches share one GSTIN; rows with empty
    // GST inherit the main/first company's GST since they're operating
    // under that umbrella). The UI dropdown / multi-company filter wants
    // one entry per GST group, not per branch row.
    //
    // Returns one CompanyGroup per unique effective GST. RepCoId is the
    // lowest CoId in the group (the "main" branch for that GST); CoName is
    // its name; MemberCoIds carries every CoId that maps to this group so
    // ApplyCompanyFilter can expand back to the full DocId set when the
    // user picks the group.
    public async Task<IReadOnlyList<CompanyGroup>> CompanyGroupsAsync(CancellationToken ct = default)
    {
        var rows = await ListCompaniesAsync(ct);
        if (rows.Count == 0) return Array.Empty<CompanyGroup>();

        // The "main" GST is the first row's (typically CoId=1) GST. Empty/
        // whitespace rows inherit it.
        var main = rows[0];
        var mainGst = Normalize(main.GstNo) ?? string.Empty;

        var groups = rows
            .Select(r => new { Row = r, Eff = Normalize(r.GstNo) ?? mainGst })
            .GroupBy(x => x.Eff)
            .Select(g =>
            {
                var members = g.OrderBy(x => x.Row.CoId).Select(x => x.Row).ToList();
                var rep = members[0];
                return new CompanyGroup
                {
                    RepCoId = rep.CoId,
                    CoName = rep.CoName ?? string.Empty,
                    Gstin = g.Key,
                    MemberCoIds = members.Select(m => m.CoId).ToArray(),
                };
            })
            // Show the main-GST group first, then any others.
            .OrderBy(g => g.Gstin == mainGst ? 0 : 1)
            .ThenBy(g => g.RepCoId)
            .ToList();
        return groups;

        static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public sealed class CompanyGroup
    {
        public byte RepCoId { get; set; }
        public string CoName { get; set; } = string.Empty;
        public string Gstin { get; set; } = string.Empty;
        public byte[] MemberCoIds { get; set; } = Array.Empty<byte>();
    }

    // DocIds for the GST GROUP that the given CoId belongs to. The sidebar
    // company selector returns one entry per effective-GST group (sister
    // companies sharing one GST registration roll up to a single row via
    // CompanyGroupsAsync), so a user who picks "Group 1" expects to see HQ +
    // every branch under that GST. Expand the filter to all MemberCoIds rather
    // than narrowing to the single rep, otherwise branch invoices stay hidden.
    // Branches with NULL/empty company.GstNo inherit the main entity's GST in
    // CompanyGroupsAsync, so they're included transparently.
    public async Task<HashSet<short>> DocIdsForCompanyAsync(byte coId, CancellationToken ct = default)
    {
        var groups = await CompanyGroupsAsync(ct);
        var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(coId));
        // Filter by the group's EFFECTIVE GSTIN, resolved per DocId as
        // Documents.GSTNo FIRST, then the company's GSTIN. So a document billed
        // under a second GST registration (e.g. a mattress showroom whose
        // Documents.GSTNo differs from its company master) lands in that GST's
        // group, not the company's — even though the two share a CoId.
        if (group is not null && !string.IsNullOrWhiteSpace(group.Gstin))
        {
            var eff = await DocIdEffectiveGstinAsync(ct);
            return eff.Where(kv => string.Equals(kv.Value, group.Gstin, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToHashSet();
        }
        // Fallback (no group / no GSTIN): original CoId-membership behaviour.
        var members = group?.MemberCoIds ?? new[] { coId };
        var ids = await Documents.AsNoTracking()
            .Where(d => members.Contains(d.CoId))
            .Select(d => d.DocId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    // DocId -> effective GSTIN: the document's own Documents.GSTNo when present,
    // otherwise its company's GSTIN (Documents.CoId -> company.GstNo, blank
    // companies inheriting the main entity's GSTIN). Attributes each bill to the
    // GST registration it was actually issued under. Documents.GSTNo is probed —
    // installs without the column fall back to the company GSTIN entirely.
    public async Task<IReadOnlyDictionary<short, string>> DocIdEffectiveGstinAsync(CancellationToken ct = default)
    {
        var companies = await ListCompaniesAsync(ct);
        var companyGst = new Dictionary<byte, string?>();
        foreach (var c in companies) companyGst[c.CoId] = Norm(c.GstNo);
        var mainGstin = companies.Count > 0 ? Norm(companies[0].GstNo) : null;

        var result = new Dictionary<short, string>();
        if (await ColumnExistsAsync("Documents", "GSTNo", ct))
        {
            var rows = await Database.SqlQueryRaw<DocGstRow>(
                "SELECT CAST(DocId AS smallint) AS DocId, CAST(CoId AS tinyint) AS CoId, GSTNo AS DocGst FROM Documents")
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                var eff = Norm(r.DocGst)
                    ?? (companyGst.TryGetValue(r.CoId, out var cg) ? cg : null)
                    ?? mainGstin;
                if (eff is not null) result[r.DocId] = eff;
            }
        }
        else
        {
            var pairs = await Documents.AsNoTracking().Select(d => new { d.DocId, d.CoId }).ToListAsync(ct);
            foreach (var p in pairs)
            {
                var eff = (companyGst.TryGetValue(p.CoId, out var cg) ? cg : null) ?? mainGstin;
                if (eff is not null) result[p.DocId] = eff;
            }
        }
        return result;

        static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private sealed class DocGstRow { public short DocId { get; set; } public byte CoId { get; set; } public string? DocGst { get; set; } }

    // DocId -> CoId map for the whole tenant. Used to attach company info to
    // each invoice/bill response without per-row joins. Documents is small
    // (~30-400 rows depending on install) so a single dictionary is fine.
    // Uses .Select() so EF emits a projection (`SELECT DocId, CoId`) rather
    // than loading the full row — otherwise EF includes columns like
    // GstReverse / Sanction that may not exist on every install flavor.
    public async Task<IReadOnlyDictionary<short, byte>> DocIdToCompanyMapAsync(CancellationToken ct = default)
    {
        var pairs = await Documents.AsNoTracking()
            .Select(d => new { d.DocId, d.CoId })
            .ToListAsync(ct);
        return pairs.ToDictionary(x => x.DocId, x => x.CoId);
    }

    // DocId -> series Prefix (e.g. "CC") for building printed invoice numbers.
    // The Prefix column exists on KSCC-style installs but not necessarily on
    // others, so we probe sys.columns first and return an empty map when it's
    // absent (callers then fall back to the bare number). Raw SQL projection so
    // EF doesn't try to read a column that may not exist.
    public async Task<IReadOnlyDictionary<short, string>> DocIdToPrefixMapAsync(CancellationToken ct = default)
    {
        if (!await ColumnExistsAsync("Documents", "Prefix", ct))
            return new Dictionary<short, string>();
        var rows = await Database.SqlQueryRaw<DocPrefixRow>(
            "SELECT CAST(DocId AS smallint) AS DocId, Prefix FROM Documents WHERE Prefix IS NOT NULL")
            .ToListAsync(ct);
        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Prefix))
            .GroupBy(r => r.DocId)
            .ToDictionary(g => g.Key, g => g.First().Prefix!.Trim());
    }

    private sealed class DocPrefixRow { public short DocId { get; set; } public string? Prefix { get; set; } }

    // True when the given column exists on the given table in the active CarolERP
    // database. Used to stay tolerant of schema drift between installs (some
    // columns — Documents.Prefix, Bill_Mas.GstNo/OtherRef/Title — are present on
    // KSCC but not every flavor). Table/column names here come from code, not
    // user input.
    public async Task<bool> ColumnExistsAsync(string table, string column, CancellationToken ct = default)
    {
        var rows = await Database.SqlQueryRaw<int>(
            "SELECT 1 AS Value FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {0} AND COLUMN_NAME = {1}",
            table, column)
            .ToListAsync(ct);
        return rows.Count > 0;
    }

    private static List<short> ParseShorts(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new List<short>();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => short.TryParse(s, out _))
            .Select(short.Parse)
            .Distinct()
            .ToList();
    }

    // Read header rows from an arbitrary (validated) CarolERP header table.
    // Some installs keep sales AND purchases in one physical table (Bill_Mas,
    // separated by DocId). EF won't allow two entity types on one table, so
    // CarolSalesMas is keyless/unmapped and read via raw SQL with a
    // regex-whitelisted table name (safe despite EF1002).
    public IQueryable<CarolSalesMas> HeadersFromTable(string table)
    {
        var validated = ValidateTableName(table);
#pragma warning disable EF1002
        return Set<CarolSalesMas>().FromSqlRaw($"SELECT * FROM [{validated}]");
#pragma warning restore EF1002
    }

    public static string ValidateTableName(string t)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(t ?? string.Empty, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new InvalidOperationException($"Invalid table name '{t}'.");
        return t!;
    }

    public DbSet<CarolSalesTrn> SalesLines => Set<CarolSalesTrn>();
    public DbSet<CarolPurchaseMas> PurchaseHeaders => Set<CarolPurchaseMas>();
    public DbSet<CarolPurchaseTrn> PurchaseLines => Set<CarolPurchaseTrn>();
    public DbSet<CarolDocument> Documents => Set<CarolDocument>();
    public DbSet<CarolAccount> Accounts => Set<CarolAccount>();
    public DbSet<CarolEmployee> Employees => Set<CarolEmployee>();
    public DbSet<CarolMasters> Masters => Set<CarolMasters>();
    public DbSet<CarolCompany> Companies => Set<CarolCompany>();
    public DbSet<CarolItem> Items => Set<CarolItem>();
    public DbSet<CarolHsn> HsnCodes => Set<CarolHsn>();
    public DbSet<CarolSpecification> Specifications => Set<CarolSpecification>();
    public DbSet<CarolItemSize> ItemSizes => Set<CarolItemSize>();
    public DbSet<CarolProduct> Products => Set<CarolProduct>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, CarolErpModelCacheKeyFactory>();
    }

    public override int SaveChanges()
        => throw new InvalidOperationException("CarolERPDbContext is read-only. Do not call SaveChanges.");

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("CarolERPDbContext is read-only. Do not call SaveChangesAsync.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("CarolERPDbContext is read-only. Do not call SaveChangesAsync.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Keyless + unmapped: read exclusively through the SalesHeaders
        // FromSqlRaw query so it can target a per-tenant table without a fixed
        // ToTable() (which would collide with CarolPurchaseMas on Bill_Mas).
        // The e-Invoice columns (IRN, AckNo, EwbNo, Status, SignedQRCode) and
        // a couple of identification columns (InvNo, SuppType) only exist on
        // Flooratex-flavor installs — KSCC's Bill_File_mas doesn't have them.
        // Ignore them for KSCC so EF doesn't add them to the SELECT projection
        // around FromSqlRaw and fail with "Invalid column name".
        var isKscc = string.Equals(Flavor, "KSCC", StringComparison.OrdinalIgnoreCase);
        modelBuilder.Entity<CarolSalesMas>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            if (isKscc)
            {
                e.Ignore(x => x.IRN);
                e.Ignore(x => x.AckNo);
                e.Ignore(x => x.EwbNo);
                e.Ignore(x => x.Status);
                e.Ignore(x => x.SignedQRCode);
                e.Ignore(x => x.InvNo);
                e.Ignore(x => x.SupplyType);
                e.Ignore(x => x.Suffix);
            }
            else
            {
                e.Property(x => x.SupplyType).HasColumnName("SuppType");
            }
        });

        modelBuilder.Entity<CarolSalesTrn>(e =>
        {
            e.ToTable("Bill_File_trn");
            e.HasKey(x => x.BillFileSl);
            if (isKscc)
            {
                // KSCC's Bill_File_trn is structurally different from
                // Flooratex's: PK is `BillSl`; no Item/Spec/Size/Design
                // references (KSCC line items aren't tied to a master Item
                // table the way Flooratex is); Quantity is `FiledQty`;
                // IGST rate/amount are `IGSTPerc`/`IGSTAmt`; discount is
                // `DiscAmount`; there's no separate net-amount column.
                e.Property(x => x.BillFileSl).HasColumnName("BillSl");
                e.Ignore(x => x.ItemId);
                e.Ignore(x => x.SpecId);
                e.Ignore(x => x.SizeId);
                e.Ignore(x => x.DesignId);
                e.Ignore(x => x.NetAmount);
                e.Property(x => x.Quantity).HasColumnName("FiledQty");
                e.Property(x => x.IgstRate).HasColumnName("IGSTPerc");
                e.Property(x => x.IgstAmount).HasColumnName("IGSTAmt");
                e.Property(x => x.DiscAmt).HasColumnName("DiscAmount");
            }
        });

        modelBuilder.Entity<CarolPurchaseMas>(e =>
        {
            e.ToTable("Bill_Mas");
            e.HasKey(x => x.BillId);
            if (isKscc)
            {
                // KSCC's Bill_Mas: no AcName (party name comes from Account
                // join), no Suffix, InvNo lives as `InvoiceNo`, and the
                // reverse-charge flag is `ReverseCalc` instead of `GstReverse`.
                // StateId is `smallint` on KSCC (byte? would throw Int16->Byte on
                // materialization, e.g. the purchase-invoice list) — ignore it,
                // same as CarolAccount does.
                e.Ignore(x => x.AcName);
                e.Ignore(x => x.Suffix);
                e.Ignore(x => x.StateId);
                e.Property(x => x.InvNo).HasColumnName("InvoiceNo");
                e.Property(x => x.GstReverse).HasColumnName("ReverseCalc");
            }
        });

        modelBuilder.Entity<CarolPurchaseTrn>(e =>
        {
            e.ToTable("Bill_Inp_trn");
            e.HasKey(x => x.BillInpSl);
            if (isKscc)
            {
                // KSCC's Bill_Inp_trn uses *Perc columns for tax rates where
                // Flooratex uses *Rate. Quantity/Amount/Amt names match.
                e.Property(x => x.CgstRate).HasColumnName("CGSTPerc");
                e.Property(x => x.SgstRate).HasColumnName("SGSTPerc");
                e.Property(x => x.IgstRate).HasColumnName("IGSTPerc");
            }
        });

        modelBuilder.Entity<CarolDocument>(e =>
        {
            e.ToTable("Documents");
            e.HasKey(x => x.DocId);
        });

        modelBuilder.Entity<CarolAccount>(e =>
        {
            e.ToTable("Account");
            e.HasKey(x => x.AccountId);
            if (isKscc)
            {
                // KSCC's Account: GST column is `GSTNumber` (not `GstNo`),
                // and StateId is smallint (not tinyint) — the entity's byte?
                // typing would throw on read, so Ignore here and resolve state
                // via the company/Documents path when needed.
                e.Property(x => x.GstNo).HasColumnName("GSTNumber");
                e.Ignore(x => x.StateId);
            }
        });

        modelBuilder.Entity<CarolEmployee>(e =>
        {
            e.ToTable("Employee");
            e.HasKey(x => x.EmplId);
        });

        modelBuilder.Entity<CarolMasters>(e =>
        {
            e.ToTable("Masters");
            e.HasKey(x => x.MasId);
        });

        modelBuilder.Entity<CarolCompany>(e =>
        {
            e.ToTable("company");
            e.HasKey(x => x.CoId);
        });

        modelBuilder.Entity<CarolItem>(e =>
        {
            e.ToTable("Item");
            e.HasKey(x => x.ItemId);
        });

        modelBuilder.Entity<CarolHsn>(e =>
        {
            e.ToTable("HSN");
            e.HasKey(x => x.HsnId);
        });

        modelBuilder.Entity<CarolSpecification>(e =>
        {
            e.ToTable("Specification");
            e.HasKey(x => x.SpecId);
        });

        modelBuilder.Entity<CarolItemSize>(e =>
        {
            e.ToTable("ItemSize");
            e.HasKey(x => x.SizeId);
        });

        modelBuilder.Entity<CarolProduct>(e =>
        {
            e.ToTable("Product");
            e.HasKey(x => x.ProductId);
        });
    }
}

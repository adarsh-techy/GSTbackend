using System.Data;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class DocumentMappingService : IDocumentMappingService
{
    private readonly MasterDbContext _master;
    private readonly CarolERPDbContext _carol;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public DocumentMappingService(
        MasterDbContext master,
        CarolERPDbContext carol,
        IHttpContextAccessor httpContextAccessor)
    {
        _master = master;
        _carol = carol;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<IReadOnlyList<DocumentMappingDto>> GetMappingsAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var rows = await _master.DocumentMappings
            .Where(d => d.TenantId == tenant.TenantId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            rows = await SeedDefaultsAsync(tenant, cancellationToken);
        }

        return rows.OrderBy(r => r.SortOrder).ThenBy(r => r.GstCategory).Select(Map).ToList();
    }

    public async Task<IReadOnlyList<DocumentMappingDto>> UpdateMappingsAsync(
        UpdateDocumentMappingsCommand command,
        CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var existing = await _master.DocumentMappings
            .Where(d => d.TenantId == tenant.TenantId)
            .ToListAsync(cancellationToken);
        var byCategory = existing.ToDictionary(d => d.GstCategory, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var dto in command.Mappings)
        {
            if (!GstDocumentCatalog.IsKnownCategory(dto.GstCategory))
                throw new ArgumentException($"Unknown GST category '{dto.GstCategory}'.");

            var header = ValidateTableName(dto.HeaderTable, nameof(dto.HeaderTable));
            var line = ValidateTableName(dto.LineTable, nameof(dto.LineTable));
            var docTypes = NormalizeCsvInts(dto.DocTypes);
            var subTypes = NormalizeCsvInts(dto.SubTypes);
            var taxMode = NormalizeTaxMode(dto.TaxMode);

            if (byCategory.TryGetValue(dto.GstCategory, out var row))
            {
                row.DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? row.DisplayName : dto.DisplayName.Trim();
                row.HeaderTable = header;
                row.LineTable = line;
                row.DocTypes = docTypes;
                row.SubTypes = subTypes;
                row.IsOutward = dto.IsOutward;
                row.SortOrder = dto.SortOrder;
                row.TaxMode = taxMode;
                row.IsActive = dto.IsActive;
                row.UpdatedOn = now;
            }
            else
            {
                _master.DocumentMappings.Add(new DocumentMapping
                {
                    TenantId = tenant.TenantId,
                    GstCategory = dto.GstCategory,
                    DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.GstCategory : dto.DisplayName.Trim(),
                    HeaderTable = header,
                    LineTable = line,
                    DocTypes = docTypes,
                    SubTypes = subTypes,
                    IsOutward = dto.IsOutward,
                    SortOrder = dto.SortOrder,
                    TaxMode = taxMode,
                    IsActive = dto.IsActive,
                    CreatedOn = now,
                    UpdatedOn = now,
                });
            }
        }

        await _master.SaveChangesAsync(cancellationToken);
        return await GetMappingsAsync(cancellationToken);
    }

    // Known CarolERP bill tables this app knows how to read. The kind tells
    // the UI which dropdown each goes in; the description is just human aid.
    private static readonly (string Name, string Kind, string Description)[] KnownTables = new[]
    {
        ("Bill_Mas",        "header", "Primary bill header (exports/purchases on Flooratex; everything on KSCC)"),
        ("Bill_File_mas",   "header", "File-style sales header (consolidated invoices)"),
        ("Bill_File_trn",   "line",   "Sales line (matches Bill_File_mas)"),
        ("Bill_Exp_trn",    "line",   "Export-sales line (matches Bill_Mas export-flavored)"),
        ("Bill_Ls_Trn",     "line",   "Local-sales line"),
        ("Bill_Lp_trn",     "line",   "Local-purchase line"),
        ("Bill_Inp_trn",    "line",   "Purchase line (matches Bill_Mas)"),
        ("Bill_DrCr_Items", "line",   "Debit/Credit note items"),
        ("Bill_Serv_Trn",   "line",   "Service bill line"),
        ("Bill_Gen_Trn",    "line",   "General/misc bill line"),
        ("Bill_General",    "line",   "General voucher / journal (expense ITC; Dr/Cr postings)"),
    };

    public async Task<KnownTablesResponse> GetKnownTablesAsync(CancellationToken cancellationToken = default)
    {
        _ = RequireTenant();
        var existing = await ListExistingTablesAsync(cancellationToken);
        var response = new KnownTablesResponse();
        foreach (var (name, kind, descr) in KnownTables)
        {
            var info = new KnownTableInfo
            {
                Name = name,
                Kind = kind,
                Description = descr,
                Exists = existing.Contains(name),
            };
            (kind == "header" ? response.HeaderTables : response.LineTables).Add(info);
        }
        return response;
    }

    public async Task<DocTypeDiscoveryResponse> DiscoverDocTypesAsync(
        string? headerTable,
        CancellationToken cancellationToken = default)
    {
        _ = RequireTenant();

        // Empty / null → scan ALL known header tables that exist on this
        // tenant. Specific name → scan just that one (validated allow-list).
        List<string> tables;
        if (string.IsNullOrWhiteSpace(headerTable))
        {
            var existing = await ListExistingTablesAsync(cancellationToken);
            tables = KnownTables
                .Where(t => t.Kind == "header" && existing.Contains(t.Name))
                .Select(t => t.Name)
                .ToList();
        }
        else
        {
            var requested = ValidateTableName(headerTable, nameof(headerTable));
            if (!KnownTables.Any(t => t.Kind == "header" && string.Equals(t.Name, requested, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"'{requested}' is not a known header table.", nameof(headerTable));
            tables = new List<string> { requested };
        }

        var response = new DocTypeDiscoveryResponse { HeaderTable = string.Join(",", tables) };
        if (tables.Count == 0) return response;

        // When X-Company-Id is set, narrow the discover scan to doctypes owned
        // by that GST group (CompanyGroupsAsync expands sister CoIds sharing
        // the same effective GST). Otherwise the panel shows doctypes from
        // every company on the tenant — noisy when an admin is configuring
        // mappings for a specific GST (e.g. KSCC Mattress = CoId 51 + 52).
        string coIdClause = string.Empty;
        if (_carol.ActiveCompanyId is byte activeCoId)
        {
            var groups = await _carol.CompanyGroupsAsync(cancellationToken);
            var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(activeCoId));
            var members = group?.MemberCoIds ?? new[] { activeCoId };
            coIdClause = $"AND d.CoId IN ({string.Join(",", members.Select(c => (int)c))}) ";
        }

        // Aggregate across all scanned tables, keyed by (DocType, SubType).
        var agg = new Dictionary<(int DocType, int SubType), DiscoveredDocType>();
        var conn = (SqlConnection)_carol.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(cancellationToken);
        try
        {
            foreach (var table in tables)
            {
                var sql = $@"
SELECT d.DocType, CAST(d.SubType AS int) AS SubType, MAX(d.DocName) AS DocName,
       COUNT(*) AS Cnt, MIN(m.BillDate) AS FirstDate, MAX(m.BillDate) AS LastDate
FROM [{table}] m
JOIN Documents d ON m.DocId = d.DocId
WHERE 1=1 {coIdClause}
GROUP BY d.DocType, d.SubType
ORDER BY Cnt DESC";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var dt = Convert.ToInt32(reader.GetValue(0));
                    var st = Convert.ToInt32(reader.GetValue(1));
                    var name = reader.IsDBNull(2) ? string.Empty : reader.GetValue(2).ToString() ?? string.Empty;
                    var cnt = Convert.ToInt32(reader.GetValue(3));
                    var first = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
                    var last = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);

                    if (!agg.TryGetValue((dt, st), out var row))
                    {
                        row = new DiscoveredDocType
                        {
                            DocType = dt,
                            SubType = st,
                            DocName = name,
                            HeaderTable = table,
                            CountsByTable = tables.Count > 1 ? new Dictionary<string, int>() : null,
                        };
                        agg[(dt, st)] = row;
                    }
                    if (string.IsNullOrWhiteSpace(row.DocName) && !string.IsNullOrWhiteSpace(name)) row.DocName = name;
                    row.DocumentCount += cnt;
                    if (row.CountsByTable is not null) row.CountsByTable[table] = cnt;
                    if (first is not null && (row.FirstDate is null || first < row.FirstDate)) row.FirstDate = first;
                    if (last is not null && (row.LastDate is null || last > row.LastDate)) row.LastDate = last;
                    // Dominant table = whichever holds the highest count for this pair.
                    if (row.CountsByTable is not null)
                    {
                        var dominant = row.CountsByTable.OrderByDescending(kv => kv.Value).First().Key;
                        row.HeaderTable = dominant;
                    }
                }
            }
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }

        response.DocTypes = agg.Values.OrderByDescending(r => r.DocumentCount).ToList();
        return response;
    }

    // sys.tables probe for the tenant's CarolERP — used to mark missing
    // tables as unavailable in the UI dropdown.
    private async Task<HashSet<string>> ListExistingTablesAsync(CancellationToken ct)
    {
        var names = KnownTables.Select(t => t.Name).ToArray();
        var inClause = string.Join(",", names.Select((_, i) => $"@t{i}"));
        var sql = $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ({inClause})";
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var conn = (SqlConnection)_carol.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < names.Length; i++)
                cmd.Parameters.Add(new SqlParameter($"@t{i}", names[i]));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                found.Add(reader.GetString(0));
        }
        finally
        {
            if (shouldClose) await conn.CloseAsync();
        }
        return found;
    }

    // Seeds one row per catalogue entry, using the portable DocType/SubType
    // codes. Categories the admin still needs to point at a table are seeded
    // inactive so they never alter a report until switched on.
    private async Task<List<DocumentMapping>> SeedDefaultsAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var seeded = GstDocumentCatalog.Defaults.Select(def => new DocumentMapping
        {
            TenantId = tenant.TenantId,
            GstCategory = def.GstCategory,
            DisplayName = def.DisplayName,
            HeaderTable = def.HeaderTable,
            LineTable = def.LineTable,
            DocTypes = def.DocTypes,
            SubTypes = def.SubTypes,
            IsOutward = def.IsOutward,
            SortOrder = def.SortOrder,
            TaxMode = def.TaxMode,
            IsActive = def.SeedActive,
            CreatedOn = now,
            UpdatedOn = now,
        }).ToList();

        _master.DocumentMappings.AddRange(seeded);
        await _master.SaveChangesAsync(cancellationToken);
        return seeded;
    }

    private static DocumentMappingDto Map(DocumentMapping d) => new()
    {
        MappingId = d.MappingId,
        GstCategory = d.GstCategory,
        DisplayName = d.DisplayName,
        HeaderTable = d.HeaderTable,
        LineTable = d.LineTable,
        DocTypes = d.DocTypes,
        SubTypes = d.SubTypes,
        IsOutward = d.IsOutward,
        SortOrder = d.SortOrder,
        TaxMode = d.TaxMode,
        IsActive = d.IsActive,
    };

    private static string ValidateTableName(string? name, string field)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException($"{field} must be a valid table name (letters, digits, underscore).", field);
        return trimmed;
    }

    // Keep only comma-separated integer codes; drop blanks/garbage. Returns null
    // when nothing valid remains (= no filter).
    private static string? NormalizeCsvInts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .Distinct()
            .ToList();
        return ids.Count == 0 ? null : string.Join(",", ids);
    }

    private static string NormalizeTaxMode(string? raw)
    {
        if (string.Equals(raw, GstDocumentCatalog.TaxModeIgst, StringComparison.OrdinalIgnoreCase)) return GstDocumentCatalog.TaxModeIgst;
        if (string.Equals(raw, GstDocumentCatalog.TaxModeCgstSgst, StringComparison.OrdinalIgnoreCase)) return GstDocumentCatalog.TaxModeCgstSgst;
        return GstDocumentCatalog.TaxModeAuto;
    }

    private Tenant RequireTenant()
        => _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved; X-Tenant-Id header is required.");
}

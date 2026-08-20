namespace GSTAutoPilot.Application.DTOs;

// One row of the universal Document Mapping table, as shown/edited in
// Settings -> Document Mapping.
public class DocumentMappingDto
{
    public int MappingId { get; set; }
    public string GstCategory { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string HeaderTable { get; set; } = string.Empty;
    public string LineTable { get; set; } = string.Empty;
    // Comma-separated Documents.DocType codes (portable). Null/blank = all.
    public string? DocTypes { get; set; }
    // Comma-separated Documents.SubType codes. Null/blank = all subtypes.
    public string? SubTypes { get; set; }
    public bool IsOutward { get; set; } = true;
    public int SortOrder { get; set; }
    public string TaxMode { get; set; } = "AUTO";
    public bool IsActive { get; set; } = true;
}

// PUT payload: the admin saves the whole grid; the service upserts each row by
// GstCategory for the current tenant.
public class UpdateDocumentMappingsCommand
{
    public List<DocumentMappingDto> Mappings { get; set; } = new();
}

// One distinct DocType (+SubType) found in a CarolERP header table via the
// Documents master, with its human name, how many documents carry it, and the
// date span — surfaced by the "Discover" button so the admin can pick the right
// DocType/SubType for each category. When multiple header tables are scanned,
// CountsByTable holds the per-table breakdown so the UI can suggest where the
// dominant data lives.
public class DiscoveredDocType
{
    public int DocType { get; set; }
    public int SubType { get; set; }
    public string DocName { get; set; } = string.Empty;
    public int DocumentCount { get; set; }
    public DateTime? FirstDate { get; set; }
    public DateTime? LastDate { get; set; }
    // Header table where these documents live. When the request scans all
    // tables, this is the table with the highest count (= "suggested header").
    public string HeaderTable { get; set; } = string.Empty;
    // Per-table row counts when multiple tables were scanned. Null when only
    // one table was requested.
    public Dictionary<string, int>? CountsByTable { get; set; }
}

public class DocTypeDiscoveryResponse
{
    // Comma-separated list of header tables that were scanned, in order. Single
    // table when the caller passed a specific headerTable; all known header
    // tables when the caller passed none.
    public string HeaderTable { get; set; } = string.Empty;
    public List<DiscoveredDocType> DocTypes { get; set; } = new();
}

// One physical table on the tenant's CarolERP that the app knows how to read.
// `Kind` is "header" for bill-header tables (Bill_Mas / Bill_File_mas) or
// "line" for the per-line companions. `Exists` is true if the table is present
// on this tenant's CarolERP — UI uses it to grey out unavailable choices.
public class KnownTableInfo
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Exists { get; set; }
}

public class KnownTablesResponse
{
    public List<KnownTableInfo> HeaderTables { get; set; } = new();
    public List<KnownTableInfo> LineTables { get; set; } = new();
}

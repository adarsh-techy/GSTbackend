using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IDocumentMappingService
{
    // Returns all 9 mappings for the current tenant, seeding the defaults on
    // first access so the grid is never empty.
    Task<IReadOnlyList<DocumentMappingDto>> GetMappingsAsync(CancellationToken cancellationToken = default);

    // Upserts the supplied rows (by GstCategory) for the current tenant.
    Task<IReadOnlyList<DocumentMappingDto>> UpdateMappingsAsync(
        UpdateDocumentMappingsCommand command,
        CancellationToken cancellationToken = default);

    // Lists distinct DocIds (with counts + date span) in a CarolERP header
    // table so the admin can choose the right DocType filter per category.
    // When headerTable is null/empty, scans ALL known header tables and returns
    // one row per (DocType, SubType) with per-table count breakdowns.
    Task<DocTypeDiscoveryResponse> DiscoverDocTypesAsync(
        string? headerTable,
        CancellationToken cancellationToken = default);

    // Lists all CarolERP bill tables this app knows how to read, marking which
    // ones exist on the current tenant's CarolERP. Drives the dropdowns in
    // Settings → Document Mapping so admins pick from a validated allow-list.
    Task<KnownTablesResponse> GetKnownTablesAsync(CancellationToken cancellationToken = default);
}

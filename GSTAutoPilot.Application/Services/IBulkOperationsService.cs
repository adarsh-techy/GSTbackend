using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

// Read-only work lists for the bulk toolbars. Nothing here performs an
// operation: the bulk runs reuse the existing single-item endpoints so each
// action keeps its own validation, audit trail and rate limit.
public interface IBulkOperationsService
{
    Task<BulkCandidatesResponse> PendingIrnAsync(string period, CancellationToken cancellationToken = default);
    Task<BulkCandidatesResponse> PendingEmailAsync(string period, CancellationToken cancellationToken = default);
    Task<PendingReturnsResponse> PendingReturnsAsync(string period, CancellationToken cancellationToken = default);
}

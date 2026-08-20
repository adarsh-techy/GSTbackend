using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IGstSummaryService
{
    Task<GstSummaryResponse> GetSummaryAsync(string period, CancellationToken cancellationToken = default);
}

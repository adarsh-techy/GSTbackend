using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IGstAdvisorService
{
    // True only when an API key is configured and the feature flag is on.
    bool IsEnabled { get; }

    Task<AdvisorChatResponse> ChatAsync(AdvisorChatRequest request, CancellationToken cancellationToken = default);
}

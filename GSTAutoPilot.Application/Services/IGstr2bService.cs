using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IGstr2bService
{
    Task<Gstr2bFetchResponse> FetchAsync(string filingPeriod, CancellationToken cancellationToken = default);

    // Returns the GSTR-2B records already stored for the period (no re-fetch).
    Task<Gstr2bFetchResponse> GetAsync(string filingPeriod, CancellationToken cancellationToken = default);
}

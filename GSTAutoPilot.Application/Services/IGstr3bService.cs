using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IGstr3bService
{
    Task<Gstr3bResponse> ComputeAsync(int year, int month, CancellationToken cancellationToken = default);

    // Net-payable trend for the `months` periods ending at anchorPeriod (YYYYMM).
    Task<Gstr3bTrendResponse> ComputeTrendAsync(string anchorPeriod, int months, CancellationToken cancellationToken = default);
}

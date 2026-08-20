using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IReconService
{
    Task<ReconRunResponse> RunAsync(string filingPeriod, CancellationToken cancellationToken = default);
    Task<ReconReportResponse> GetResultsAsync(string filingPeriod, CancellationToken cancellationToken = default);
}

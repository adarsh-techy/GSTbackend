using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IGstinValidationService
{
    Task<GstinValidationResponse> ValidateAsync(string gstin, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GstinValidationResponse>> GetHistoryAsync(string gstin, CancellationToken cancellationToken = default);
    Task<BulkValidateResponse> BulkValidateAsync(IEnumerable<string> gstins, CancellationToken cancellationToken = default);
}

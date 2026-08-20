using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

// Pre-file data validation for outward returns — run before locking/filing so
// the preview can surface fixable problems (missing HSN, invalid party GSTIN,
// missing place of supply, tax that doesn't match the rate).
public interface IReturnValidationService
{
    Task<ReturnValidationResult> ValidateGstr1Async(int year, int month, CancellationToken cancellationToken = default);
}

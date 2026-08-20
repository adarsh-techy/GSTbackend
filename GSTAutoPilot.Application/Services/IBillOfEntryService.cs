using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IBillOfEntryService
{
    Task<IReadOnlyList<BillOfEntryDto>> ListAsync(string period, CancellationToken cancellationToken = default);
    Task<BillOfEntryDto> CreateAsync(SaveBillOfEntryCommand command, CancellationToken cancellationToken = default);
    Task<BillOfEntryDto?> UpdateAsync(int boeId, SaveBillOfEntryCommand command, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int boeId, CancellationToken cancellationToken = default);

    // Period rollup of import IGST/Cess for GSTR-3B Table 4(A)(1).
    Task<BillOfEntryPeriodTotals> GetPeriodTotalsAsync(string period, CancellationToken cancellationToken = default);
}

using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IInvoiceService
{
    Task<IReadOnlyList<InvoiceResponse>> ListAsync(int year, int month, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Gstr1SummaryRow>> GetGstr1SummaryAsync(int year, int month, CancellationToken cancellationToken = default);
    Task<Gstr1TablesResponse> GetGstr1TablesAsync(int year, int month, CancellationToken cancellationToken = default);
    Task<InvoiceResponse?> GetByBillIdAsync(int billId, CancellationToken cancellationToken = default);
}

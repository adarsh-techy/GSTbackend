using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IPurchaseInvoiceService
{
    Task<IReadOnlyList<PurchaseInvoiceResponse>> ListAsync(int year, int month, CancellationToken cancellationToken = default);
}

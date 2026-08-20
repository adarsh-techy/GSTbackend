using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IEWayBillService
{
    Task<EWayBillResponse> GenerateAsync(Guid invoiceId, GenerateEWayBillRequest? request, CancellationToken cancellationToken = default);

    // Generate for a CarolERP-sourced bill (no row in the tenant Invoices
    // table). Used by KSCC + Flooratex Live where invoices are read live from
    // CarolERP. Mirrors IEInvoiceService.GenerateForBillAsync.
    Task<EWayBillResponse> GenerateForBillAsync(int billId, GenerateEWayBillRequest? request, CancellationToken cancellationToken = default);

    Task<EWayBillResponse> CancelAsync(Guid ewbId, string reason, CancellationToken cancellationToken = default);
    Task<EWayBillResponse> UpdateVehicleAsync(Guid ewbId, string newVehicleNumber, CancellationToken cancellationToken = default);
    Task<EWayBillResponse?> GetByInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EWayBillResponse>> ListAsync(CancellationToken cancellationToken = default);
}

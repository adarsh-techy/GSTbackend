using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IEInvoiceService
{
    Task<IRNResponse> GenerateAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    Task<IRNResponse> GenerateForBillAsync(int billId, CancellationToken cancellationToken = default);
    // Builds and returns the NIC v1.1 JSON that GenerateForBillAsync would POST
    // to WhiteBooks/NIC, WITHOUT making the call. Lets the user paste the same
    // payload into Postman or share with provider support for schema review.
    Task<string> PreviewPayloadAsync(int billId, CancellationToken cancellationToken = default);
    Task<IRNResponse> CancelAsync(Guid irnId, string reason, string? remarks = null, CancellationToken cancellationToken = default);
    Task<IRNResponse?> GetByInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    Task<IRNResponse?> GetByBillAsync(int billId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IRNResponse>> ListAsync(CancellationToken cancellationToken = default);
    // Download the stored signed-invoice JSON / QR PNG for a CarolERP bill.
    Task<EInvoiceFile?> GetSignedJsonAsync(int billId, CancellationToken cancellationToken = default);
    Task<EInvoiceFile?> GetQrPngAsync(int billId, CancellationToken cancellationToken = default);
    // Email the signed JSON + PDF to the buyer (for invoices past the 24h window).
    Task<IRNResponse> EmailJsonAsync(int billId, EmailJsonRequest request, CancellationToken cancellationToken = default);
}

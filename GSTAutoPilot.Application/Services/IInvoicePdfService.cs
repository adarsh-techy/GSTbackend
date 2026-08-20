namespace GSTAutoPilot.Application.Services;

public interface IInvoicePdfService
{
    Task<InvoicePdfResult?> RenderAsync(int billId, CancellationToken cancellationToken = default);
}

public record InvoicePdfResult(byte[] Bytes, string FileName);

namespace GSTAutoPilot.Application.Services;

public interface IExportService
{
    // section: null/"all" = full workbook; otherwise "summary" | "b2b" |
    // "export" | "b2c" | "cdn" for a single per-tab sheet.
    Task<ExportResult> ExportGstr1Async(string period, string? section = null, CancellationToken cancellationToken = default);
    Task<ExportResult> ExportGstr3bAsync(string period, CancellationToken cancellationToken = default);
    Task<ExportResult> ExportInvoicesAsync(string period, CancellationToken cancellationToken = default);
    Task<ExportResult> ExportReconAsync(string period, CancellationToken cancellationToken = default);
}

public record ExportResult(byte[] Bytes, string FileName);

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using GSTAutoPilot.Infrastructure.Services.WhiteBooks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GSTAutoPilot.Infrastructure.Services;

public class EInvoiceService : IEInvoiceService
{
    private static readonly TimeSpan CancelWindow = TimeSpan.FromHours(24);

    // PascalCase + indented — matches what WhiteBooksClient sends on the wire,
    // and indented so users can read/edit it in Postman or a text editor.
    private static readonly JsonSerializerOptions PreviewJson = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    private readonly TenantDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IInvoiceService _invoiceService;
    private readonly ICompanyService _companyService;
    private readonly IWhiteBooksClient _whiteBooks;
    private readonly IEmailService _email;
    private readonly ITenantSettingsService _settings;
    private readonly IInvoicePdfService _pdf;
    private readonly IFilingService _filingService;
    private readonly IReturnValidationService _validationService;
    private readonly WhiteBooksGst.IWhiteBooksGstClient _gstClient;
    private readonly ILogger<EInvoiceService> _logger;

    public EInvoiceService(
        TenantDbContext db,
        IHttpContextAccessor httpContextAccessor,
        IInvoiceService invoiceService,
        ICompanyService companyService,
        IWhiteBooksClient whiteBooks,
        IEmailService email,
        ITenantSettingsService settings,
        IInvoicePdfService pdf,
        IFilingService filingService,
        IReturnValidationService validationService,
        WhiteBooksGst.IWhiteBooksGstClient gstClient,
        ILogger<EInvoiceService> logger)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _invoiceService = invoiceService;
        _companyService = companyService;
        _whiteBooks = whiteBooks;
        _email = email;
        _settings = settings;
        _pdf = pdf;
        _filingService = filingService;
        _validationService = validationService;
        _gstClient = gstClient;
        _logger = logger;
    }

    public async Task<IRNResponse> GenerateForBillAsync(int billId, CancellationToken cancellationToken = default)
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved.");

        var existing = await _db.IRNRecords
            .Where(r => r.BillId == billId && r.Status == IRNStatus.Generated)
            .OrderByDescending(r => r.AcknowledgementDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return MapToResponse(existing);
        }

        var invoice = await _invoiceService.GetByBillIdAsync(billId, cancellationToken)
            ?? throw new ArgumentException($"Invoice with BillId {billId} not found in CarolERP.", nameof(billId));

        IRNRecord record;
        if (_whiteBooks.IsConfigured)
        {
            var company = await _companyService.GetAsync(cancellationToken)
                ?? new CompanyDto { CompanyName = tenant.Name, GSTIN = tenant.GSTIN };
            // In sandbox mode the auth account is registered for a specific
            // test GSTIN (29AAGCB1286Q000) — sending the tenant's REAL GSTIN
            // as seller gets NIC 1015 "Invalid GSTIN for this user". Use the
            // GSTIN the client will auth with as the seller. In production
            // these are the same value (tenant's real GSTIN).
            var sellerGstin = _whiteBooks.ActiveGstin;
            var payload = WhiteBooksPayloadBuilder.Build(invoice, company, sellerGstin, _whiteBooks.IsSandbox);
            var result = await _whiteBooks.GenerateIrnAsync(payload, cancellationToken);
            record = new IRNRecord
            {
                TenantId = tenant.TenantId,
                InvoiceId = DeterministicGuid(billId),
                BillId = billId,
                InvoiceNo = invoice.InvoiceNumber,
                IRNNumber = result.Irn,
                QRCode = result.SignedQrCode,
                AcknowledgementNo = result.AckNo,
                AcknowledgementDate = result.AckDate,
                SignedInvoice = result.SignedInvoice,
                Status = IRNStatus.Generated,
                Source = _whiteBooks.SourceLabel,
            };
            _logger.LogInformation("WhiteBooks IRN {Irn} generated for BillId {BillId}", result.Irn, billId);
        }
        else
        {
            record = BuildStubRecord(tenant, invoice, billId);
            _logger.LogInformation("WhiteBooks not configured — produced STUB IRN for BillId {BillId}", billId);
        }

        _db.IRNRecords.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
        var resp = MapToResponse(record);
        resp.AutoFiling = await TryAutoFileGstr1Async(invoice.InvoiceDate, cancellationToken);
        return resp;
    }

    public async Task<string> PreviewPayloadAsync(int billId, CancellationToken cancellationToken = default)
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved.");
        var invoice = await _invoiceService.GetByBillIdAsync(billId, cancellationToken)
            ?? throw new ArgumentException($"Invoice with BillId {billId} not found in CarolERP.", nameof(billId));
        var company = await _companyService.GetAsync(cancellationToken)
            ?? new CompanyDto { CompanyName = tenant.Name, GSTIN = tenant.GSTIN };
        // Same builder + same sellerGstin source as the real send →
        // byte-for-byte equivalent (including the sandbox-GSTIN override).
        var sellerGstin = _whiteBooks.IsConfigured ? _whiteBooks.ActiveGstin : tenant.GSTIN;
        var payload = WhiteBooksPayloadBuilder.Build(invoice, company, sellerGstin, _whiteBooks.IsConfigured && _whiteBooks.IsSandbox);
        return JsonSerializer.Serialize(payload, PreviewJson);
    }

    // Deterministic offline IRN used until WhiteBooks credentials are present.
    private static IRNRecord BuildStubRecord(Tenant tenant, InvoiceResponse invoice, int billId)
    {
        var ack = DateTime.UtcNow;
        var canonical = string.Join('|',
            tenant.GSTIN,
            invoice.InvoiceNumber,
            invoice.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            invoice.TaxableValue.ToString("F2", CultureInfo.InvariantCulture),
            invoice.TotalAmount.ToString("F2", CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var irn = Convert.ToHexString(hash).ToLowerInvariant();
        var ackNoSeed = (long)(BitConverter.ToUInt64(hash, 0) & 0x7FFFFFFFFFFFFFFFL);
        var ackNo = (ackNoSeed % 10_000_000_000_000_000L).ToString("D16", CultureInfo.InvariantCulture);

        var qrPayload = new
        {
            irn,
            ackNo,
            ackDt = ack.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            sellerGstin = tenant.GSTIN,
            buyerGstin = invoice.PartyGSTIN,
            docNo = invoice.InvoiceNumber,
            docDt = invoice.InvoiceDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            totInvVal = invoice.TotalAmount,
            mainHsnCode = invoice.Lines.FirstOrDefault()?.HSNCode ?? string.Empty,
        };
        var qrJson = JsonSerializer.Serialize(qrPayload);
        var qr = Convert.ToBase64String(Encoding.UTF8.GetBytes(qrJson));
        var signed = $"eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.{Convert.ToBase64String(Encoding.UTF8.GetBytes(qrJson)).TrimEnd('=')}.SIM_SIG_{irn[..16]}";

        return new IRNRecord
        {
            TenantId = tenant.TenantId,
            InvoiceId = DeterministicGuid(billId),
            BillId = billId,
            InvoiceNo = invoice.InvoiceNumber,
            IRNNumber = irn,
            QRCode = qr,
            AcknowledgementNo = ackNo,
            AcknowledgementDate = ack,
            SignedInvoice = signed,
            Status = IRNStatus.Generated,
            Source = "STUB",
        };
    }

    public async Task<IRNResponse?> GetByBillAsync(int billId, CancellationToken cancellationToken = default)
    {
        var record = await _db.IRNRecords.AsNoTracking()
            .Where(r => r.BillId == billId)
            .OrderByDescending(r => r.CreatedOn)
            .FirstOrDefaultAsync(cancellationToken);
        return record is null ? null : MapToResponse(record);
    }

    private static Guid DeterministicGuid(int id)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[12..], id);
        return new Guid(bytes);
    }

    public async Task<IRNResponse> GenerateAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved.");

        var invoice = await _db.Invoices
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new ArgumentException($"Invoice {invoiceId} not found.", nameof(invoiceId));

        var existing = await _db.IRNRecords
            .Where(r => r.InvoiceId == invoiceId && r.Status == IRNStatus.Generated)
            .OrderByDescending(r => r.AcknowledgementDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return MapToResponse(existing);
        }

        var ack = DateTime.UtcNow;
        var canonical = string.Join('|',
            tenant.GSTIN,
            invoice.InvoiceNumber,
            invoice.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            invoice.TaxableValue.ToString("F2", CultureInfo.InvariantCulture),
            invoice.TotalAmount.ToString("F2", CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var irn = Convert.ToHexString(hash).ToLowerInvariant();
        var ackNoSeed = (long)(BitConverter.ToUInt64(hash, 0) & 0x7FFFFFFFFFFFFFFFL);
        var ackNo = (ackNoSeed % 10_000_000_000_000_000L).ToString("D16", CultureInfo.InvariantCulture);

        var qrPayload = new
        {
            irn,
            ackNo,
            ackDt = ack.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            sellerGstin = tenant.GSTIN,
            buyerGstin = invoice.PartyGSTIN,
            docNo = invoice.InvoiceNumber,
            docDt = invoice.InvoiceDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            totInvVal = invoice.TotalAmount,
            mainHsnCode = invoice.Lines.FirstOrDefault()?.HSNCode ?? string.Empty,
        };
        var qrJson = JsonSerializer.Serialize(qrPayload);
        var qr = Convert.ToBase64String(Encoding.UTF8.GetBytes(qrJson));
        var signed = $"eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.{Convert.ToBase64String(Encoding.UTF8.GetBytes(qrJson)).TrimEnd('=')}.SIM_SIG_{irn[..16]}";

        var record = new IRNRecord
        {
            TenantId = tenant.TenantId,
            InvoiceId = invoiceId,
            InvoiceNo = invoice.InvoiceNumber,
            IRNNumber = irn,
            QRCode = qr,
            AcknowledgementNo = ackNo,
            AcknowledgementDate = ack,
            SignedInvoice = signed,
            Status = IRNStatus.Generated,
        };
        _db.IRNRecords.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
        var resp = MapToResponse(record);
        resp.AutoFiling = await TryAutoFileGstr1Async(invoice.InvoiceDate, cancellationToken);
        return resp;
    }

    public async Task<IRNResponse> CancelAsync(Guid irnId, string reason, string? remarks = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Cancellation reason is required.", nameof(reason));
        }

        var record = await _db.IRNRecords.FirstOrDefaultAsync(r => r.IRNId == irnId, cancellationToken)
            ?? throw new ArgumentException($"IRN {irnId} not found.", nameof(irnId));

        if (record.Status == IRNStatus.Cancelled)
        {
            throw new InvalidOperationException("IRN is already cancelled.");
        }

        var elapsed = DateTime.UtcNow - record.AcknowledgementDate;
        if (elapsed > CancelWindow)
        {
            throw new InvalidOperationException(
                "Cancellation window closed. Use Credit Note or Email JSON option.");
        }

        // Real WhiteBooks IRNs must be cancelled on the NIC portal first; stub
        // IRNs (offline) are cancelled locally only.
        var isStub = IsStub(record.Source);
        if (!isStub && _whiteBooks.IsConfigured)
        {
            await _whiteBooks.CancelIrnAsync(record.IRNNumber, reason.Trim(), (remarks ?? string.Empty).Trim(), cancellationToken);
        }

        record.Status = IRNStatus.Cancelled;
        record.CancelledOn = DateTime.UtcNow;
        record.CancelReason = reason.Trim();
        record.CancelRemarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("IRN {IrnId} cancelled (reason {Reason}, stub={Stub})", irnId, reason, isStub);

        return MapToResponse(record);
    }

    public async Task<EInvoiceFile?> GetSignedJsonAsync(int billId, CancellationToken cancellationToken = default)
    {
        var record = await _db.IRNRecords
            .Where(r => r.BillId == billId)
            .OrderByDescending(r => r.CreatedOn)
            .FirstOrDefaultAsync(cancellationToken);
        if (record is null || string.IsNullOrWhiteSpace(record.SignedInvoice)) return null;

        var bytes = Encoding.UTF8.GetBytes(record.SignedInvoice);
        record.JsonDownloadedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return new EInvoiceFile(bytes, $"{SafeName(record.InvoiceNo)}_einvoice.json", "application/json");
    }

    public async Task<EInvoiceFile?> GetQrPngAsync(int billId, CancellationToken cancellationToken = default)
    {
        var record = await _db.IRNRecords.AsNoTracking()
            .Where(r => r.BillId == billId)
            .OrderByDescending(r => r.CreatedOn)
            .FirstOrDefaultAsync(cancellationToken);
        if (record is null) return null;
        var png = QrPngRenderer.ToPng(record.QRCode);
        return png is null ? null : new EInvoiceFile(png, $"{SafeName(record.InvoiceNo)}_qr.png", "image/png");
    }

    public async Task<IRNResponse> EmailJsonAsync(int billId, EmailJsonRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ToEmail))
            throw new ArgumentException("Recipient email is required.", nameof(request));

        var record = await _db.IRNRecords
            .Where(r => r.BillId == billId)
            .OrderByDescending(r => r.CreatedOn)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ArgumentException($"No e-Invoice (IRN) found for BillId {billId}.", nameof(billId));

        var invoice = await _invoiceService.GetByBillIdAsync(billId, cancellationToken)
            ?? throw new ArgumentException($"Invoice with BillId {billId} not found in CarolERP.", nameof(billId));
        var company = await _companyService.GetAsync(cancellationToken) ?? new CompanyDto { CompanyName = "Company" };

        var attachments = new List<EmailAttachment>();
        if (!string.IsNullOrWhiteSpace(record.SignedInvoice))
            attachments.Add(new EmailAttachment($"{SafeName(record.InvoiceNo)}_einvoice.json",
                Encoding.UTF8.GetBytes(record.SignedInvoice), "application/json"));
        var pdf = await _pdf.RenderAsync(billId, cancellationToken);
        if (pdf is not null)
            attachments.Add(new EmailAttachment($"{SafeName(record.InvoiceNo)}.pdf", pdf.Bytes, "application/pdf"));

        var subject = $"e-Invoice - {record.InvoiceNo} dt {invoice.InvoiceDate:dd/MM/yyyy} from {company.CompanyName}";
        var body = BuildEmailBody(record, invoice, company, request.Remarks);

        // Send via the tenant's configured SMTP (throws if not configured).
        var smtp = await _settings.GetSmtpConfigAsync(cancellationToken);
        await _email.SendAsync(smtp, new EmailMessage(request.ToEmail.Trim(), request.CcEmail, subject, body, attachments), cancellationToken);

        record.EmailSentOn = DateTime.UtcNow;
        record.EmailSentTo = request.ToEmail.Trim();
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("e-Invoice JSON for BillId {BillId} emailed to {To}", billId, request.ToEmail);

        return MapToResponse(record);
    }

    private static string BuildEmailBody(IRNRecord record, InvoiceResponse invoice, CompanyDto company, string? remarks)
    {
        var extra = string.IsNullOrWhiteSpace(remarks) ? string.Empty : $"\n{remarks.Trim()}\n";
        return
$@"Dear {invoice.PartyName},

Please find attached the signed e-Invoice JSON and PDF for:

Invoice No: {record.InvoiceNo}
Date: {invoice.InvoiceDate:dd/MM/yyyy}
IRN: {record.IRNNumber}
Ack No: {record.AcknowledgementNo}
Ack Date: {record.AcknowledgementDate:dd/MM/yyyy HH:mm}
Amount: ₹{invoice.TotalAmount:N2}

The signed JSON can be used for your ITC verification.

Note: This invoice cannot be cancelled via the e-Invoice portal as the 24-hour window has passed.
{extra}
Regards,
{company.CompanyName}
GSTIN: {company.GSTIN}";
    }

    private static bool IsStub(string? source)
        => string.IsNullOrWhiteSpace(source) || string.Equals(source, "STUB", StringComparison.OrdinalIgnoreCase);

    private static string SafeName(string? s)
    {
        var cleaned = new string((s ?? "invoice").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "invoice" : cleaned;
    }

    public async Task<IRNResponse?> GetByInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var record = await _db.IRNRecords.AsNoTracking()
            .Where(r => r.InvoiceId == invoiceId)
            .OrderByDescending(r => r.CreatedOn)
            .FirstOrDefaultAsync(cancellationToken);
        return record is null ? null : MapToResponse(record);
    }

    public async Task<IReadOnlyList<IRNResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.IRNRecords.AsNoTracking()
            .OrderByDescending(r => r.AcknowledgementDate)
            .ToListAsync(cancellationToken);
        return rows.Select(MapToResponse).ToList();
    }

    private static IRNResponse MapToResponse(IRNRecord r)
    {
        var stub = IsStub(r.Source);
        return new IRNResponse
        {
            IRNId = r.IRNId,
            InvoiceId = r.InvoiceId,
            BillId = r.BillId,
            InvoiceNo = r.InvoiceNo,
            IRNNumber = r.IRNNumber,
            QRCode = r.QRCode,
            AcknowledgementNo = r.AcknowledgementNo,
            AcknowledgementDate = r.AcknowledgementDate,
            SignedInvoice = r.SignedInvoice,
            Status = r.Status,
            CancelledOn = r.CancelledOn,
            CancelReason = r.CancelReason,
            CancelRemarks = r.CancelRemarks,
            EmailSentOn = r.EmailSentOn,
            EmailSentTo = r.EmailSentTo,
            JsonDownloadedOn = r.JsonDownloadedOn,
            CreatedOn = r.CreatedOn,
            Source = stub ? "STUB" : r.Source,
            LifecycleStatus = IRNAgeService.GetLifecycleStatus(r.Status, r.AcknowledgementDate),
            IsCancellable = r.Status != IRNStatus.Cancelled && IRNAgeService.IsCancellable(r.AcknowledgementDate),
            IsStub = stub,
            AgeHours = Math.Round(IRNAgeService.AgeHours(r.AcknowledgementDate), 1),
            TimeRemaining = IRNAgeService.GetTimeRemaining(r.AcknowledgementDate),
        };
    }

    private async Task<AutoFilingResult?> TryAutoFileGstr1Async(DateTime invoiceDate, CancellationToken cancellationToken)
    {
        try
        {
            var year = invoiceDate.Year;
            var month = invoiceDate.Month;
            var period = $"{year:D4}{month:D2}";

            // 1. If GSTR-1 for this period is already filed or submitted, do nothing.
            var latest = await _filingService.LatestAsync(period, FilingType.Gstr1, cancellationToken);
            if (latest != null && (latest.Status == FilingStatus.Filed || latest.Status == FilingStatus.Submitted))
            {
                return null;
            }

            // 2. Fetch outward invoices for this month to check e-invoice status.
            var invoices = await _invoiceService.ListAsync(year, month, cancellationToken);
            if (invoices == null || invoices.Count == 0)
            {
                return null;
            }

            // 3. Check if any invoice still requires an e-invoice.
            var requiredPending = invoices.Count(i => string.Equals(i.EInvoiceStatus, "Required", StringComparison.OrdinalIgnoreCase));
            if (requiredPending > 0)
            {
                return new AutoFilingResult
                {
                    Triggered = false,
                    Period = period,
                    PendingEInvoicesCount = requiredPending,
                    Message = $"{requiredPending} e-invoice(s) still pending before GSTR-1 can auto-file."
                };
            }

            // 4. Ensure at least one e-invoice has been done in this period.
            var doneCount = invoices.Count(i => string.Equals(i.EInvoiceStatus, "Done", StringComparison.OrdinalIgnoreCase));
            if (doneCount == 0)
            {
                return null;
            }

            _logger.LogInformation("All {DoneCount} required e-invoices completed for period {Period}. Initiating GSTR-1 auto-file.", doneCount, period);

            // 5. Run GSTR-1 pre-file validation.
            var validation = await _validationService.ValidateGstr1Async(year, month, cancellationToken);
            if (validation.ErrorCount > 0)
            {
                _logger.LogWarning("Auto-filing GSTR-1 for {Period} stopped due to {ErrorCount} validation errors.", period, validation.ErrorCount);
                return new AutoFilingResult
                {
                    Triggered = true,
                    Period = period,
                    Status = "ValidationFailed",
                    Message = $"All {doneCount} e-invoices are done, but GSTR-1 validation found {validation.ErrorCount} error(s). Please review GSTR-1.",
                    PendingEInvoicesCount = 0
                };
            }

            // 6. Lock GSTR-1 return.
            FilingResponse filing;
            if (latest == null || latest.Status != FilingStatus.Locked)
            {
                filing = await _filingService.LockGstr1Async(period, confirmNil: false, cancellationToken);
                _logger.LogInformation("GSTR-1 auto-locked for period {Period}, FilingId: {FilingId}", period, filing.FilingId);
            }
            else
            {
                filing = latest;
            }

            // 7. Auto-submit to GSTN if GSP client is configured.
            if (_gstClient.IsConfigured)
            {
                try
                {
                    var submitRes = await _filingService.SubmitToGstnAsync(filing.FilingId, cancellationToken);
                    if (submitRes.ReadyToFile)
                    {
                        _logger.LogInformation("GSTR-1 auto-submitted to GSTN for period {Period}. EVC OTP sent.", period);
                        return new AutoFilingResult
                        {
                            Triggered = true,
                            Period = period,
                            Status = "Submitted",
                            FilingId = filing.FilingId,
                            Message = $"All {doneCount} e-invoices completed! GSTR-1 for {period} has been locked and submitted to GSTN. EVC OTP sent to registered mobile.",
                            PendingEInvoicesCount = 0
                        };
                    }
                    else
                    {
                        _logger.LogWarning("GSTR-1 auto-submit to GSTN for period {Period} returned errors: {Msg}", period, submitRes.Message);
                        return new AutoFilingResult
                        {
                            Triggered = true,
                            Period = period,
                            Status = "SaveFailed",
                            FilingId = filing.FilingId,
                            Message = $"All e-invoices completed & GSTR-1 locked, but GSTN save reported errors: {submitRes.Message}",
                            PendingEInvoicesCount = 0
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-submit to GSTN failed for period {Period}", period);
                    return new AutoFilingResult
                    {
                        Triggered = true,
                        Period = period,
                        Status = "Locked",
                        FilingId = filing.FilingId,
                        Message = $"All {doneCount} e-invoices completed! GSTR-1 for {period} has been locked and is ready to submit on the GSTR-1 page.",
                        PendingEInvoicesCount = 0
                    };
                }
            }

            return new AutoFilingResult
            {
                Triggered = true,
                Period = period,
                Status = "Locked",
                FilingId = filing.FilingId,
                Message = $"All {doneCount} e-invoices completed! GSTR-1 for {period} has been automatically locked and is ready to file.",
                PendingEInvoicesCount = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during automatic GSTR-1 filing check for invoice date {Date}", invoiceDate);
            return null;
        }
    }
}

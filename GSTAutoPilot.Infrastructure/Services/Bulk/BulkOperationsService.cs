using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services.Bulk;

// Work lists for the bulk toolbars. This service only ever READS: it says what
// a bulk run would act on and what is standing in the way. The acting itself
// stays on the existing single-item endpoints, driven one at a time from the
// browser, so a run stops when the user stops it and nothing keeps firing at
// the e-Invoice portal or the mail server after a tab is closed.
public class BulkOperationsService : IBulkOperationsService
{
    private readonly IInvoiceService _invoices;
    private readonly TenantDbContext _db;
    private readonly CarolERPDbContext _carol;
    private readonly IFilingService _filings;
    private readonly WhiteBooksGst.IWhiteBooksGstClient _gst;
    private readonly OperationRateLimiter _limiter;
    private readonly IHttpContextAccessor _http;

    public BulkOperationsService(
        IInvoiceService invoices,
        TenantDbContext db,
        CarolERPDbContext carol,
        IFilingService filings,
        WhiteBooksGst.IWhiteBooksGstClient gst,
        OperationRateLimiter limiter,
        IHttpContextAccessor http)
    {
        _invoices = invoices;
        _db = db;
        _carol = carol;
        _filings = filings;
        _gst = gst;
        _limiter = limiter;
        _http = http;
    }

    private Guid TenantId => (_http.HttpContext?.Items["Tenant"] as Tenant)?.TenantId
        ?? throw new InvalidOperationException("Tenant not resolved; X-Tenant-Id header is required.");

    // Invoices that need an IRN and don't have one. "Required" is the same
    // per-invoice threshold the invoice grid shows, so the toolbar count and the
    // grid never disagree.
    public async Task<BulkCandidatesResponse> PendingIrnAsync(string period, CancellationToken ct = default)
    {
        var (year, month) = ParsePeriod(period);
        var invoices = await _invoices.ListAsync(year, month, ct);

        var result = NewResponse("generate-irn", period, OperationRateLimiter.EInvoiceGenerate);
        foreach (var inv in invoices.Where(i => string.Equals(i.EInvoiceStatus, "Required", StringComparison.OrdinalIgnoreCase)))
        {
            var row = ToCandidate(inv);
            // "Required" is a value threshold only, so it also catches B2C
            // supplies that e-invoicing simply doesn't apply to. Exports DO
            // need one — WhiteBooksPayloadBuilder already files them as
            // EXPWP/EXPWOP with the buyer as URP — so a missing GSTIN is not by
            // itself a reason to skip.
            row.BlockedReason = inv.Section switch
            {
                "B2B" when !IsGstin(inv.PartyGSTIN) => "Buyer is marked B2B but has no valid GSTIN.",
                "B2CS" or "B2CL" => "Supply to an unregistered buyer — e-Invoicing does not apply.",
                _ => null,
            };
            (row.BlockedReason is null ? result.Ready : result.Blocked).Add(row);
        }
        return result;
    }

    // Generated IRNs for the period that have not been emailed to the buyer yet.
    public async Task<BulkCandidatesResponse> PendingEmailAsync(string period, CancellationToken ct = default)
    {
        var (year, month) = ParsePeriod(period);
        var invoices = await _invoices.ListAsync(year, month, ct);
        var byBill = invoices.ToDictionary(i => i.BillId, i => i);

        var billIds = byBill.Keys.ToList();
        var notEmailed = await _db.IRNRecords.AsNoTracking()
            .Where(r => r.BillId != null
                && billIds.Contains(r.BillId!.Value)
                && r.Status == IRNStatus.Generated
                && r.EmailSentOn == null)
            .Select(r => r.BillId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var emails = await _carol.AccountEmailsByGstinAsync(ct);
        var result = NewResponse("email-einvoice", period, OperationRateLimiter.EInvoiceEmail);

        foreach (var billId in notEmailed)
        {
            if (!byBill.TryGetValue(billId, out var inv)) continue;
            var row = ToCandidate(inv);
            if (IsGstin(inv.PartyGSTIN) && emails.TryGetValue(inv.PartyGSTIN.Trim().ToUpperInvariant(), out var to))
                row.PartyEmail = to;
            else
                row.BlockedReason = "No email address on file for this buyer in the ERP account master.";
            (row.BlockedReason is null ? result.Ready : result.Blocked).Add(row);
        }

        result.Ready.Sort((a, b) => a.InvoiceDate.CompareTo(b.InvoiceDate));
        result.Blocked.Sort((a, b) => a.InvoiceDate.CompareTo(b.InvoiceDate));
        return result;
    }

    // What still has to happen for the period's returns, in the order it has to
    // happen: GSTR-1 before GSTR-3B.
    public async Task<PendingReturnsResponse> PendingReturnsAsync(string period, CancellationToken ct = default)
    {
        ParsePeriod(period);
        var response = new PendingReturnsResponse
        {
            Period = period,
            GstnConfigured = _gst.IsConfigured,
            HasGstnSession = _gst.IsConfigured && _gst.HasSession,
        };

        foreach (var type in new[] { FilingType.Gstr1, FilingType.Gstr3b })
        {
            var latest = await _filings.LatestAsync(period, type, ct);
            var status = latest?.Status.ToString();
            response.Returns.Add(new PendingReturn
            {
                Type = type,
                Period = period,
                Status = status,
                FilingId = latest?.FilingId,
                AckNo = latest?.AckNo,
                NeedsAction = latest is null || latest.Status != FilingStatus.Filed,
                NextStep = latest?.Status switch
                {
                    null => "lock",
                    FilingStatus.Locked or FilingStatus.SaveFailed => "submit",
                    FilingStatus.Submitted => "file",
                    _ => "done",
                },
            });
        }
        return response;
    }

    private BulkCandidatesResponse NewResponse(string operation, string period, OperationLimit limit) => new()
    {
        Operation = operation,
        Period = period,
        RateLimitMax = limit.Max,
        RateLimitPeriodSeconds = (int)limit.Period.TotalSeconds,
        RateLimitRemaining = _limiter.Remaining(TenantId, limit),
    };

    private static BulkCandidate ToCandidate(InvoiceResponse inv) => new()
    {
        BillId = inv.BillId,
        InvoiceNumber = inv.InvoiceNumber,
        InvoiceDate = inv.InvoiceDate,
        PartyName = inv.PartyName,
        PartyGSTIN = inv.PartyGSTIN,
        InvoiceValue = inv.TotalAmount,
    };

    private static bool IsGstin(string? raw)
    {
        var g = (raw ?? string.Empty).Trim();
        return g.Length == 15 && g.All(char.IsLetterOrDigit);
    }

    private static (int Year, int Month) ParsePeriod(string period)
    {
        if (string.IsNullOrWhiteSpace(period) || period.Length != 6
            || !int.TryParse(period.AsSpan(0, 4), out var y)
            || !int.TryParse(period.AsSpan(4, 2), out var m)
            || m < 1 || m > 12)
        {
            throw new ArgumentException("period must be YYYYMM (e.g. 202604).", nameof(period));
        }
        return (y, m);
    }
}

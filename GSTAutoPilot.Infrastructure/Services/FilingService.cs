using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Exceptions;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class FilingService : IFilingService
{
    private readonly TenantDbContext _db;
    private readonly IGstnReturnService _gstnService;
    private readonly IInvoiceService _invoiceService;
    private readonly WhiteBooksGst.IWhiteBooksGstClient _gst;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public FilingService(
        TenantDbContext db,
        IGstnReturnService gstnService,
        IInvoiceService invoiceService,
        WhiteBooksGst.IWhiteBooksGstClient gst,
        IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _gstnService = gstnService;
        _invoiceService = invoiceService;
        _gst = gst;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<FilingResponse> LockGstr1Async(string period, bool confirmNil = false, CancellationToken cancellationToken = default)
    {
        var (year, month) = ParsePeriod(period);
        var tenantId = RequireTenantId();

        // The locked payload IS the portal-uploadable GSTN GSTR-1 JSON.
        var gstn = await _gstnService.BuildGstr1Async(year, month, cancellationToken);
        var json = _gstnService.Serialize(gstn);
        RequireNilAgreement("gstr1", period, json, confirmNil);

        var entity = new Gstr1Filing
        {
            FilingId = Guid.NewGuid(),
            TenantId = tenantId,
            Period = period,
            Status = "Locked",
            PayloadJson = json,
            CreatedOn = DateTime.UtcNow,
        };
        _db.Gstr1Filings.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return MapGstr1(entity);
    }

    public async Task<FilingResponse> LockGstr3bAsync(string period, bool confirmNil = false, CancellationToken cancellationToken = default)
    {
        var (year, month) = ParsePeriod(period);
        var tenantId = RequireTenantId();

        // The locked payload IS the portal-uploadable GSTN GSTR-3B JSON.
        var gstn = await _gstnService.BuildGstr3bAsync(year, month, cancellationToken);
        var json = _gstnService.Serialize(gstn);
        RequireNilAgreement("gstr3b", period, json, confirmNil);

        var entity = new Gstr3bFiling
        {
            FilingId = Guid.NewGuid(),
            TenantId = tenantId,
            Period = period,
            Status = "Locked",
            PayloadJson = json,
            CreatedOn = DateTime.UtcNow,
        };
        _db.Gstr3bFilings.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return MapGstr3b(entity);
    }

    // A NIL return and an ordinary one must never be reached by accident in
    // either direction: locking an empty period needs the caller to say so, and
    // asking for NIL over a period that has supplies is refused outright.
    private static void RequireNilAgreement(string returnType, string period, string payloadJson, bool confirmNil)
    {
        var isNil = NilReturnDetector.IsNil(returnType, payloadJson);
        if (isNil && !confirmNil) throw NilReturnConfirmationException.NeedsConfirmation(returnType, period);
        if (!isNil && confirmNil) throw NilReturnConfirmationException.HasData(returnType, period);
    }

    public async Task<NilCheckResponse> CheckNilAsync(string period, FilingType type, CancellationToken cancellationToken = default)
    {
        var (year, month) = ParsePeriod(period);
        RequireTenantId();

        var json = type == FilingType.Gstr1
            ? _gstnService.Serialize(await _gstnService.BuildGstr1Async(year, month, cancellationToken))
            : _gstnService.Serialize(await _gstnService.BuildGstr3bAsync(year, month, cancellationToken));
        var returnType = type == FilingType.Gstr1 ? "gstr1" : "gstr3b";
        var isNil = NilReturnDetector.IsNil(returnType, json);

        // Book figures come from the invoice list rather than the payload, so a
        // "not nil" verdict can point at what is actually in the period.
        var invoices = await _invoiceService.ListAsync(year, month, cancellationToken);
        var taxable = decimal.Round(invoices.Sum(i => i.TaxableValue), 2);
        var tax = decimal.Round(invoices.Sum(i => i.IGST + i.CGST + i.SGST), 2);

        return new NilCheckResponse
        {
            Period = period,
            Type = type,
            IsNil = isNil,
            InvoiceCount = invoices.Count,
            TaxableValue = taxable,
            Tax = tax,
            Reason = isNil
                ? $"No transactions were found for {period}. Filing it would declare a NIL return."
                : $"{invoices.Count} document(s) totalling {taxable:N2} taxable / {tax:N2} tax were found for {period}, so this is not a NIL return.",
        };
    }

    public async Task<FilingResponse> MarkFiledAsync(Guid filingId, MarkFiledCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.AckNo))
            throw new ArgumentException("AckNo is required.", nameof(command));

        var tenantId = RequireTenantId();
        var filedOn = command.FiledOn ?? DateTime.UtcNow;

        var g1 = await _db.Gstr1Filings.FirstOrDefaultAsync(f => f.FilingId == filingId && f.TenantId == tenantId, cancellationToken);
        if (g1 is not null)
        {
            if (g1.Status == "Filed")
                throw new InvalidOperationException("Filing already marked as filed.");
            g1.Status = "Filed";
            g1.AckNo = command.AckNo.Trim();
            g1.FiledOn = filedOn;
            g1.FiledBy = CurrentUserName();
            await _db.SaveChangesAsync(cancellationToken);
            return MapGstr1(g1);
        }

        var g3 = await _db.Gstr3bFilings.FirstOrDefaultAsync(f => f.FilingId == filingId && f.TenantId == tenantId, cancellationToken);
        if (g3 is null)
            throw new InvalidOperationException("Filing not found for this tenant.");

        if (g3.Status == "Filed")
            throw new InvalidOperationException("Filing already marked as filed.");
        g3.Status = "Filed";
        g3.AckNo = command.AckNo.Trim();
        g3.FiledOn = filedOn;
        g3.FiledBy = CurrentUserName();
        await _db.SaveChangesAsync(cancellationToken);
        return MapGstr3b(g3);
    }

    public async Task<GstnSubmitResponse> SubmitToGstnAsync(Guid filingId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        var (g1, g3) = await LoadFilingAsync(filingId, tenantId, cancellationToken);
        var status = g1?.Status ?? g3!.Status;
        if (status == "Filed") throw new InvalidOperationException("Filing is already filed.");
        // Re-running from Submitted is the "resend EVC OTP" path: retsave is
        // idempotent for unchanged data, so this re-saves and re-sends the OTP.

        var returnType = g1 is not null ? "gstr1" : "gstr3b";
        var period = g1?.Period ?? g3!.Period;
        var payload = g1?.PayloadJson ?? g3!.PayloadJson;
        var retPeriod = WhiteBooksGst.WhiteBooksGstClient.ToRetPeriod(period);

        // Step 1 — retsave. GSTN can accept the call yet reject rows, so the
        // error report decides whether we may proceed.
        var save = await _gst.SaveReturnAsync(returnType, retPeriod, payload, cancellationToken);

        if (g1 is not null) { g1.ReferenceId = save.ReferenceId; g1.ErrorReportJson = save.ErrorReportJson; }
        else { g3!.ReferenceId = save.ReferenceId; g3.ErrorReportJson = save.ErrorReportJson; }

        if (save.HasErrors)
        {
            // Do NOT submit — submitting locks bad data on the portal.
            if (g1 is not null) g1.Status = "SaveFailed"; else g3!.Status = "SaveFailed";
            await _db.SaveChangesAsync(cancellationToken);
            return new GstnSubmitResponse
            {
                FilingId = filingId,
                Status = FilingStatus.SaveFailed,
                ReferenceId = save.ReferenceId,
                ReadyToFile = false,
                ErrorReportJson = save.ErrorReportJson,
                Message = "GSTN rejected some rows during save. Correct the flagged invoices and submit again — nothing has been locked on the portal.",
            };
        }

        // Step 1b — a NIL return has to be declared as such to GSTN, via the
        // isNil flag on proceed-to-file. Only GSTR-1/5/6 take that flag; a NIL
        // GSTR-3B is simply an all-zero return through the ordinary path.
        var isNil = NilReturnDetector.IsNil(returnType, payload);
        if (isNil && returnType == "gstr1")
            await _gst.ProceedToFileAsync(returnType, retPeriod, isNil: true, cancellationToken);

        // Step 2 — send the EVC OTP to the authorised signatory. (There is no
        // retsubmit in the WhiteBooks contract; the save IS the submission, and
        // retevcfile does the filing.)
        await _gst.RequestEvcOtpAsync(returnType, cancellationToken);
        var now = DateTime.UtcNow;

        if (g1 is not null) { g1.Status = "Submitted"; g1.SubmittedOn = now; }
        else { g3!.Status = "Submitted"; g3.SubmittedOn = now; }
        await _db.SaveChangesAsync(cancellationToken);

        return new GstnSubmitResponse
        {
            FilingId = filingId,
            Status = FilingStatus.Submitted,
            ReferenceId = g1?.ReferenceId ?? g3!.ReferenceId,
            ReadyToFile = true,
            Message = isNil
                ? "Saved to GSTN as a NIL return and an EVC OTP has been sent to the authorised signatory's registered mobile/email — enter it to file."
                : "Saved to GSTN and an EVC OTP has been sent to the authorised signatory's registered mobile/email — enter it to file.",
        };
    }

    public async Task<FilingResponse> FileWithEvcAsync(Guid filingId, FileWithEvcCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Otp)) throw new ArgumentException("Filing OTP is required.", nameof(command));
        var tenantId = RequireTenantId();
        var (g1, g3) = await LoadFilingAsync(filingId, tenantId, cancellationToken);
        var status = g1?.Status ?? g3!.Status;
        if (status == "Filed") throw new InvalidOperationException("Filing is already filed.");
        if (status != "Submitted")
            throw new InvalidOperationException("Submit the return to GSTN before filing it.");

        var returnType = g1 is not null ? "gstr1" : "gstr3b";
        var period = g1?.Period ?? g3!.Period;
        var retPeriod = WhiteBooksGst.WhiteBooksGstClient.ToRetPeriod(period);

        // Step 3 — retevcfile. The body is the return payload: for GSTR-3B the
        // same JSON we saved; for GSTR-1 the chksum/sec_sum summary, which only
        // GSTN can produce, so it is fetched from retsum first.
        var filePayload = returnType == "gstr3b"
            ? (g3!.PayloadJson)
            : await BuildGstr1FilePayloadAsync(retPeriod, cancellationToken);

        var result = await _gst.FileReturnAsync(
            returnType, retPeriod, command.Otp.Trim(), filePayload, cancellationToken);

        // Prefer GSTN's own filing timestamp over our clock.
        var filedOn = result.FilingDate ?? DateTime.UtcNow;
        var filedBy = CurrentUserName();

        if (g1 is not null)
        {
            g1.Status = "Filed"; g1.AckNo = result.Arn; g1.FiledOn = filedOn; g1.FiledBy = filedBy;
            g1.ErrorReportJson = null;
            await _db.SaveChangesAsync(cancellationToken);
            return MapGstr1(g1);
        }
        g3!.Status = "Filed"; g3.AckNo = result.Arn; g3.FiledOn = filedOn; g3.FiledBy = filedBy;
        g3.Cin = command.Cin;
        g3.ErrorReportJson = null;
        await _db.SaveChangesAsync(cancellationToken);
        return MapGstr3b(g3);
    }

    // GSTR-1's retevcfile body is a checksum summary, not the return itself:
    // { gstin, ret_period, chksum, newSumFlag, sec_sum[] }. The chksum and
    // sec_sum are computed by GSTN over what it actually holds, so they must be
    // read back from retsum rather than derived locally — a mismatch is exactly
    // the guard GSTN uses to prove we are filing what we saved.
    private async Task<string> BuildGstr1FilePayloadAsync(string retPeriod, CancellationToken ct)
    {
        var summaryJson = await _gst.GetReturnSummaryRawAsync("gstr1", retPeriod, ct);

        using var doc = System.Text.Json.JsonDocument.Parse(summaryJson);
        var root = doc.RootElement;
        var scope = root.TryGetProperty("data", out var d) ? d : root;

        if (!scope.TryGetProperty("chksum", out var chksum))
        {
            throw new InvalidOperationException(
                "GSTN's return summary did not include a checksum, so the GSTR-1 filing payload cannot be built. "
                + "Re-save the return and try again.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["gstin"] = RequireGstin(),
            ["ret_period"] = retPeriod,
            ["chksum"] = chksum.GetString(),
            ["newSumFlag"] = true,
        };
        if (scope.TryGetProperty("sec_sum", out var secSum))
            payload["sec_sum"] = System.Text.Json.JsonSerializer.Deserialize<object>(secSum.GetRawText());

        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private string RequireGstin()
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved; X-Tenant-Id header is required.");
        return tenant.GSTIN;
    }

    public async Task<string> GetGstnSummaryAsync(string type, string period, CancellationToken cancellationToken = default)
    {
        RequireTenantId();
        ParsePeriod(period); // validates YYYYMM
        var returnType = type?.Trim().ToLowerInvariant() switch
        {
            "gstr1" => "gstr1",
            "gstr3b" => "gstr3b",
            _ => throw new ArgumentException("type must be gstr1 or gstr3b.", nameof(type)),
        };
        var retPeriod = WhiteBooksGst.WhiteBooksGstClient.ToRetPeriod(period);
        return await _gst.GetReturnSummaryRawAsync(returnType, retPeriod, cancellationToken);
    }

    private async Task<(Gstr1Filing? G1, Gstr3bFiling? G3)> LoadFilingAsync(Guid filingId, Guid tenantId, CancellationToken ct)
    {
        var g1 = await _db.Gstr1Filings.FirstOrDefaultAsync(f => f.FilingId == filingId && f.TenantId == tenantId, ct);
        if (g1 is not null) return (g1, null);
        var g3 = await _db.Gstr3bFilings.FirstOrDefaultAsync(f => f.FilingId == filingId && f.TenantId == tenantId, ct);
        if (g3 is not null) return (null, g3);
        throw new InvalidOperationException("Filing not found for this tenant.");
    }

    public async Task<IReadOnlyList<FilingResponse>> ListAsync(string? period, FilingType? type, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();

        var results = new List<FilingResponse>();
        if (type is null or FilingType.Gstr1)
        {
            var q = _db.Gstr1Filings.AsNoTracking().Where(f => f.TenantId == tenantId);
            if (!string.IsNullOrWhiteSpace(period)) q = q.Where(f => f.Period == period);
            results.AddRange((await q.ToListAsync(cancellationToken)).Select(MapGstr1));
        }
        if (type is null or FilingType.Gstr3b)
        {
            var q = _db.Gstr3bFilings.AsNoTracking().Where(f => f.TenantId == tenantId);
            if (!string.IsNullOrWhiteSpace(period)) q = q.Where(f => f.Period == period);
            results.AddRange((await q.ToListAsync(cancellationToken)).Select(MapGstr3b));
        }
        return results
            .OrderByDescending(r => r.Period)
            .ThenByDescending(r => r.CreatedOn)
            .ToList();
    }

    public async Task<FilingDetailResponse?> GetAsync(Guid filingId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        var g1 = await _db.Gstr1Filings.AsNoTracking().FirstOrDefaultAsync(f => f.FilingId == filingId && f.TenantId == tenantId, cancellationToken);
        if (g1 is not null) return MapGstr1Detail(g1);
        var g3 = await _db.Gstr3bFilings.AsNoTracking().FirstOrDefaultAsync(f => f.FilingId == filingId && f.TenantId == tenantId, cancellationToken);
        return g3 is null ? null : MapGstr3bDetail(g3);
    }

    public async Task<FilingResponse?> LatestAsync(string period, FilingType type, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        if (type == FilingType.Gstr1)
        {
            var g1 = await _db.Gstr1Filings.AsNoTracking()
                .Where(f => f.TenantId == tenantId && f.Period == period)
                .OrderByDescending(f => f.CreatedOn)
                .FirstOrDefaultAsync(cancellationToken);
            return g1 is null ? null : MapGstr1(g1);
        }
        var g3 = await _db.Gstr3bFilings.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.Period == period)
            .OrderByDescending(f => f.CreatedOn)
            .FirstOrDefaultAsync(cancellationToken);
        return g3 is null ? null : MapGstr3b(g3);
    }

    private Guid RequireTenantId()
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved; X-Tenant-Id header is required.");
        return tenant.TenantId;
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

    // Display name of the signed-in user, for the filing audit trail.
    private string? CurrentUserName()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user is null) return null;
        return user.FindFirst("name")?.Value
            ?? user.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
            ?? user.FindFirst("sub")?.Value;
    }

    private static FilingStatus ParseStatus(string s)
        => string.Equals(s, "Filed", StringComparison.OrdinalIgnoreCase) ? FilingStatus.Filed
         : string.Equals(s, "Submitted", StringComparison.OrdinalIgnoreCase) ? FilingStatus.Submitted
         : string.Equals(s, "SaveFailed", StringComparison.OrdinalIgnoreCase) ? FilingStatus.SaveFailed
         : FilingStatus.Locked;

    private static FilingResponse MapGstr1(Gstr1Filing f) => new()
    {
        FilingId = f.FilingId,
        Type = FilingType.Gstr1,
        Period = f.Period,
        Status = ParseStatus(f.Status),
        AckNo = f.AckNo,
        FiledOn = f.FiledOn,
        CreatedOn = f.CreatedOn,
        ReferenceId = f.ReferenceId,
        SubmittedOn = f.SubmittedOn,
        FiledBy = f.FiledBy,
    };

    private static FilingResponse MapGstr3b(Gstr3bFiling f) => new()
    {
        FilingId = f.FilingId,
        Type = FilingType.Gstr3b,
        Period = f.Period,
        Status = ParseStatus(f.Status),
        AckNo = f.AckNo,
        FiledOn = f.FiledOn,
        CreatedOn = f.CreatedOn,
        ReferenceId = f.ReferenceId,
        SubmittedOn = f.SubmittedOn,
        FiledBy = f.FiledBy,
    };

    private static FilingDetailResponse MapGstr1Detail(Gstr1Filing f) => new()
    {
        FilingId = f.FilingId,
        Type = FilingType.Gstr1,
        Period = f.Period,
        Status = ParseStatus(f.Status),
        AckNo = f.AckNo,
        FiledOn = f.FiledOn,
        CreatedOn = f.CreatedOn,
        ReferenceId = f.ReferenceId,
        SubmittedOn = f.SubmittedOn,
        FiledBy = f.FiledBy,
        PayloadJson = f.PayloadJson,
        ErrorReportJson = f.ErrorReportJson,
    };

    private static FilingDetailResponse MapGstr3bDetail(Gstr3bFiling f) => new()
    {
        FilingId = f.FilingId,
        Type = FilingType.Gstr3b,
        Period = f.Period,
        Status = ParseStatus(f.Status),
        AckNo = f.AckNo,
        FiledOn = f.FiledOn,
        CreatedOn = f.CreatedOn,
        ReferenceId = f.ReferenceId,
        SubmittedOn = f.SubmittedOn,
        FiledBy = f.FiledBy,
        PayloadJson = f.PayloadJson,
        ErrorReportJson = f.ErrorReportJson,
    };
}

using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Exceptions;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using GSTAutoPilot.Infrastructure.Services.WhiteBooksGst;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class Gstr2bService : IGstr2bService
{
    private readonly TenantDbContext _db;
    private readonly IWhiteBooksGstClient _gst;
    private readonly IReconService _recon;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public Gstr2bService(TenantDbContext db, IWhiteBooksGstClient gst, IReconService recon, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _gst = gst;
        _recon = recon;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<Gstr2bFetchResponse> FetchAsync(string filingPeriod, CancellationToken cancellationToken = default)
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved.");

        if (!TryParsePeriod(filingPeriod, out _, out _))
        {
            throw new ArgumentException("filingPeriod must be in YYYYMM format (e.g. 202604).", nameof(filingPeriod));
        }

        // Fail loud — NEVER fabricate GSTR-2B. A real client filing must
        // reconcile against actual GSTN data, so if we can't reach GSTN we stop
        // and tell the user why rather than returning sample rows.
        if (!_gst.IsConfigured)
        {
            throw new GstnNotConnectedException(GstnNotConnectedException.NotConfigured,
                "GST API is not configured for this company. Enter the GST-API credentials in Settings before fetching GSTR-2B.");
        }
        if (!_gst.HasSession)
        {
            throw new GstnNotConnectedException(GstnNotConnectedException.NoSession,
                "Not connected to GSTN. Click “Connect to GSTN” and complete the OTP before fetching GSTR-2B.");
        }

        var fetchedOn = DateTime.UtcNow;

        // Real GSTN pull via the WhiteBooks GSP. The returned JSON carries
        // B2B / B2BA / CDNR / CDNRA / ISD / IMPG / IMPGSEZ — all parsed here. A
        // large 2B is split across files (filenum 1..fc); the first response
        // declares the file count, so we page through and merge.
        var retPeriod = WhiteBooksGstClient.ToRetPeriod(filingPeriod);
        var firstJson = await _gst.FetchGstr2bRawAsync(retPeriod, "1", cancellationToken);
        var records = Gstr2bJsonParser.Parse(firstJson, tenant.TenantId, filingPeriod, fetchedOn);
        var fileCount = Gstr2bJsonParser.ExtractFileCount(firstJson);
        for (var fileNum = 2; fileNum <= fileCount; fileNum++)
        {
            var partJson = await _gst.FetchGstr2bRawAsync(retPeriod, fileNum.ToString(), cancellationToken);
            records.AddRange(Gstr2bJsonParser.Parse(partJson, tenant.TenantId, filingPeriod, fetchedOn));
        }
        var source = fileCount > 1 ? $"GSTN ({fileCount} files)" : "GSTN";
        foreach (var r in records) r.Source = source; // persist provenance

        // Replace any prior snapshot for the period with this fetch.
        var existing = await _db.GSTR2BRecords
            .Where(g => g.FilingPeriod == filingPeriod)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            _db.GSTR2BRecords.RemoveRange(existing);
        }
        _db.GSTR2BRecords.AddRange(records);
        await _db.SaveChangesAsync(cancellationToken);

        // Auto-reconcile now that real 2B is in place. The fetch is already
        // committed, so a recon failure surfaces to the caller without losing
        // the 2B snapshot (a manual Run Recon can retry).
        await _recon.RunAsync(filingPeriod, cancellationToken);

        return new Gstr2bFetchResponse
        {
            FilingPeriod = filingPeriod,
            RecordsFetched = records.Count,
            FetchedOn = fetchedOn,
            Source = source,
            Records = records.Select(MapToResponse).ToList(),
        };
    }

    public async Task<Gstr2bFetchResponse> GetAsync(string filingPeriod, CancellationToken cancellationToken = default)
    {
        if (!TryParsePeriod(filingPeriod, out _, out _))
        {
            throw new ArgumentException("filingPeriod must be in YYYYMM format (e.g. 202604).", nameof(filingPeriod));
        }

        var records = await _db.GSTR2BRecords.AsNoTracking()
            .Where(g => g.FilingPeriod == filingPeriod)
            .OrderBy(g => g.RecordType).ThenBy(g => g.SupplierName)
            .ToListAsync(cancellationToken);

        // Report the persisted provenance so the UI can distinguish a genuine
        // GSTN pull from legacy rows (null Source => pre-provenance / possibly
        // stale mock data).
        var storedSource = records
            .Select(r => r.Source)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? (records.Count > 0 ? "STORED (unknown source)" : "STORED");

        return new Gstr2bFetchResponse
        {
            FilingPeriod = filingPeriod,
            RecordsFetched = records.Count,
            FetchedOn = records.Count > 0 ? records.Max(r => r.FetchedOn) : default,
            Source = storedSource,
            Records = records.Select(MapToResponse).ToList(),
        };
    }

    private static bool TryParsePeriod(string period, out int year, out int month)
    {
        year = 0;
        month = 0;
        if (string.IsNullOrWhiteSpace(period) || period.Length != 6) return false;
        if (!int.TryParse(period.AsSpan(0, 4), out year)) return false;
        if (!int.TryParse(period.AsSpan(4, 2), out month)) return false;
        return month >= 1 && month <= 12;
    }

    private static Gstr2bRecordResponse MapToResponse(GSTR2B g) => new()
    {
        GSTR2BId = g.GSTR2BId,
        SupplierGSTIN = g.SupplierGSTIN,
        SupplierName = g.SupplierName,
        InvoiceNo = g.InvoiceNo,
        InvoiceDate = g.InvoiceDate,
        TaxableAmount = g.TaxableAmount,
        IGSTAmount = g.IGSTAmount,
        CGSTAmount = g.CGSTAmount,
        SGSTAmount = g.SGSTAmount,
        FilingPeriod = g.FilingPeriod,
        RecordType = g.RecordType,
    };
}

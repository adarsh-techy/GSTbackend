using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.Persistence;
using GSTAutoPilot.Infrastructure.Services.EwbApi;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class EWayBillService : IEWayBillService
{
    private static readonly TimeSpan CancelWindow = TimeSpan.FromHours(24);
    private static readonly HashSet<string> AllowedModes = new(StringComparer.OrdinalIgnoreCase)
    {
        EWayBillMode.Road, EWayBillMode.Rail, EWayBillMode.Air, EWayBillMode.Ship,
    };

    private readonly TenantDbContext _db;
    private readonly CarolERPDbContext _carol;
    private readonly IInvoiceService _invoiceService;
    private readonly ICompanyService _companyService;
    private readonly IWhiteBooksEWayBillClient _ewbClient;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public EWayBillService(
        TenantDbContext db,
        CarolERPDbContext carol,
        IInvoiceService invoiceService,
        ICompanyService companyService,
        IWhiteBooksEWayBillClient ewbClient,
        IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _carol = carol;
        _invoiceService = invoiceService;
        _companyService = companyService;
        _ewbClient = ewbClient;
        _httpContextAccessor = httpContextAccessor;
    }

    // Same deterministic shape as EInvoiceService — gives the same synthetic
    // InvoiceId for a given BillId so EWB lookups by InvoiceId line up with
    // IRN lookups for the same bill.
    private static Guid DeterministicGuid(int id)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[12..], id);
        return new Guid(bytes);
    }

    public async Task<EWayBillResponse> GenerateForBillAsync(int billId, GenerateEWayBillRequest? request, CancellationToken cancellationToken = default)
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved.");

        var invoice = await _invoiceService.GetByBillIdAsync(billId, cancellationToken)
            ?? throw new ArgumentException($"Invoice with BillId {billId} not found in CarolERP.", nameof(billId));

        var synthInvoiceId = DeterministicGuid(billId);
        var existing = await _db.EWayBills
            .Where(e => e.InvoiceId == synthInvoiceId && e.Status == EWayBillStatus.Active)
            .OrderByDescending(e => e.GeneratedDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return MapToResponse(existing, DisplaySource());

        var mode = NormalizeMode(request?.Mode);
        var distance = request?.Distance ?? 0m;
        if (distance < 0m) distance = 0m;
        var generated = DateTime.UtcNow;

        // From GSTIN comes from the active group's GST (per-company aware),
        // falling back to tenant.GSTIN when no company is picked. Same pattern
        // as GstSummaryService so multi-GST tenants file EWBs under the right
        // registration.
        var fromGstin = tenant.GSTIN;
        if (_carol.ActiveCompanyId is byte activeCoId)
        {
            var groups = await _carol.CompanyGroupsAsync(cancellationToken);
            var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(activeCoId));
            if (!string.IsNullOrWhiteSpace(group?.Gstin)) fromGstin = group!.Gstin;
        }

        var (ewbNumber, validUntil, source) = await ProduceAsync(invoice, tenant, fromGstin, mode, distance, request, generated, cancellationToken);

        var record = new EWayBill
        {
            TenantId = tenant.TenantId,
            InvoiceId = synthInvoiceId,
            EWBNumber = ewbNumber,
            InvoiceNo = invoice.InvoiceNumber,
            GeneratedDate = generated,
            ValidUntil = validUntil,
            FromGSTIN = fromGstin,
            FromAddress = string.IsNullOrWhiteSpace(request?.FromAddress) ? tenant.Name : request!.FromAddress!,
            ToGSTIN = invoice.PartyGSTIN ?? string.Empty,
            ToAddress = request?.ToAddress ?? invoice.PartyName ?? string.Empty,
            TransporterGSTIN = request?.TransporterGSTIN ?? string.Empty,
            TransporterName = request?.TransporterName ?? string.Empty,
            VehicleNumber = request?.VehicleNumber ?? string.Empty,
            Distance = distance,
            Mode = mode,
            Status = EWayBillStatus.Active,
        };

        _db.EWayBills.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
        return MapToResponse(record, source);
    }

    public async Task<EWayBillResponse> GenerateAsync(Guid invoiceId, GenerateEWayBillRequest? request, CancellationToken cancellationToken = default)
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved.");

        var inv = await _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new ArgumentException($"Invoice {invoiceId} not found.", nameof(invoiceId));

        var existing = await _db.EWayBills
            .Where(e => e.InvoiceId == invoiceId && e.Status == EWayBillStatus.Active)
            .OrderByDescending(e => e.GeneratedDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return MapToResponse(existing, DisplaySource());

        var mode = NormalizeMode(request?.Mode);
        var distance = request?.Distance ?? 0m;
        if (distance < 0m) distance = 0m;
        var generated = DateTime.UtcNow;

        var invoice = ToInvoiceResponse(inv);
        var (ewbNumber, validUntil, source) = await ProduceAsync(invoice, tenant, tenant.GSTIN, mode, distance, request, generated, cancellationToken);

        var record = new EWayBill
        {
            TenantId = tenant.TenantId,
            InvoiceId = invoiceId,
            EWBNumber = ewbNumber,
            InvoiceNo = inv.InvoiceNumber,
            GeneratedDate = generated,
            ValidUntil = validUntil,
            FromGSTIN = tenant.GSTIN,
            FromAddress = string.IsNullOrWhiteSpace(request?.FromAddress) ? tenant.Name : request!.FromAddress!,
            ToGSTIN = inv.PartyGSTIN,
            ToAddress = request?.ToAddress ?? inv.PartyName,
            TransporterGSTIN = request?.TransporterGSTIN ?? string.Empty,
            TransporterName = request?.TransporterName ?? string.Empty,
            VehicleNumber = request?.VehicleNumber ?? string.Empty,
            Distance = distance,
            Mode = mode,
            Status = EWayBillStatus.Active,
        };

        _db.EWayBills.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
        return MapToResponse(record, source);
    }

    // Produce the EWB number + validity: the real WhiteBooks/NIC portal when the
    // e-Way Bill API is configured, otherwise a deterministic offline STUB number
    // (same behaviour the whole module shipped with before the GSP wiring).
    private async Task<(string EwbNumber, DateTime ValidUntil, string Source)> ProduceAsync(
        InvoiceResponse invoice, Tenant tenant, string fromGstin, string mode, decimal distance,
        GenerateEWayBillRequest? request, DateTime generated, CancellationToken cancellationToken)
    {
        if (_ewbClient.IsConfigured)
        {
            var company = await _companyService.GetAsync(cancellationToken)
                ?? new CompanyDto { CompanyName = tenant.Name, GSTIN = fromGstin };
            var sellerGstin = IsReal(_ewbClient.ActiveGstin) ? _ewbClient.ActiveGstin : fromGstin;
            var payload = EWayBillPayloadBuilder.Build(
                invoice, company, sellerGstin, distance, mode,
                request?.TransporterGSTIN, request?.TransporterName, request?.VehicleNumber);
            var result = await _ewbClient.GenerateAsync(payload, cancellationToken);
            return (result.EwbNo, result.ValidUntil, _ewbClient.SourceLabel);
        }

        var canonical = string.Join('|',
            fromGstin, invoice.InvoiceNumber,
            invoice.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            invoice.TotalAmount.ToString("F2", CultureInfo.InvariantCulture),
            generated.Ticks.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var ewbSeed = (long)(BitConverter.ToUInt64(hash, 0) & 0x7FFFFFFFFFFFFFFFL);
        var ewbNumber = (ewbSeed % 1_000_000_000_000L).ToString("D12", CultureInfo.InvariantCulture);
        return (ewbNumber, generated.Add(ComputeValidity(distance)), "STUB");
    }

    public async Task<EWayBillResponse> CancelAsync(Guid ewbId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Cancellation reason is required.", nameof(reason));
        }

        var record = await _db.EWayBills.FirstOrDefaultAsync(e => e.EWBId == ewbId, cancellationToken)
            ?? throw new ArgumentException($"E-Way Bill {ewbId} not found.", nameof(ewbId));

        if (record.Status == EWayBillStatus.Cancelled)
        {
            throw new InvalidOperationException("E-Way Bill is already cancelled.");
        }

        var elapsed = DateTime.UtcNow - record.GeneratedDate;
        if (elapsed > CancelWindow)
        {
            throw new InvalidOperationException(
                $"E-Way Bill can only be cancelled within 24 hours of generation; this EWB is {elapsed.TotalHours:F1}h old.");
        }

        // Cancel on the portal first (when live) so we never mark an EWB
        // cancelled locally while it stays active at NIC. CnlRsn "4" = Others;
        // the free-text reason becomes the remark.
        if (_ewbClient.IsConfigured)
        {
            await _ewbClient.CancelAsync(record.EWBNumber, "4", reason.Trim(), cancellationToken);
        }

        record.Status = EWayBillStatus.Cancelled;
        record.CancelledOn = DateTime.UtcNow;
        record.CancelReason = reason.Trim();
        await _db.SaveChangesAsync(cancellationToken);

        return MapToResponse(record, DisplaySource());
    }

    public async Task<EWayBillResponse> UpdateVehicleAsync(Guid ewbId, string newVehicleNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newVehicleNumber))
        {
            throw new ArgumentException("Vehicle number is required.", nameof(newVehicleNumber));
        }

        var record = await _db.EWayBills.FirstOrDefaultAsync(e => e.EWBId == ewbId, cancellationToken)
            ?? throw new ArgumentException($"E-Way Bill {ewbId} not found.", nameof(ewbId));

        if (record.Status == EWayBillStatus.Cancelled)
        {
            throw new InvalidOperationException("Cannot update vehicle on a cancelled E-Way Bill.");
        }
        if (DateTime.UtcNow > record.ValidUntil)
        {
            throw new InvalidOperationException("E-Way Bill validity has expired; vehicle update not permitted.");
        }

        var newVehicle = newVehicleNumber.Trim().ToUpperInvariant();

        // Push the Part-B (vehicle) update to the portal first when live.
        // reasonCode "2" = "Due to Transhipment"; transMode mirrors the EWB.
        if (_ewbClient.IsConfigured)
        {
            var payload = new
            {
                ewbNo = long.TryParse(record.EWBNumber, out var n) ? n : 0,
                vehicleNo = newVehicle,
                fromPlace = record.FromAddress,
                fromState = StateCodeInt(record.FromGSTIN),
                reasonCode = "2",
                reasonRem = "Vehicle updated",
                transMode = TransModeCode(record.Mode),
                transDocNo = string.Empty,
                transDocDate = string.Empty,
            };
            await _ewbClient.UpdateVehicleAsync(payload, cancellationToken);
        }

        record.VehicleNumber = newVehicle;
        await _db.SaveChangesAsync(cancellationToken);

        return MapToResponse(record, DisplaySource());
    }

    public async Task<EWayBillResponse?> GetByInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var record = await _db.EWayBills.AsNoTracking()
            .Where(e => e.InvoiceId == invoiceId)
            .OrderByDescending(e => e.GeneratedDate)
            .FirstOrDefaultAsync(cancellationToken);
        return record is null ? null : MapToResponse(record, DisplaySource());
    }

    public async Task<IReadOnlyList<EWayBillResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Multi-GST tenants pick a company in the sidebar; that company's
        // GST group has its own e-Way Bill portal account, so the list should
        // only show EWBs filed under that GST. FromGSTIN on each row already
        // carries the seller-side GSTIN at generate time (taken from the
        // invoice), so filtering on it is enough — no schema change needed.
        // When no X-Company-Id is set, pass through and show every EWB.
        var query = _db.EWayBills.AsNoTracking().AsQueryable();
        if (_carol.ActiveCompanyId is byte activeCoId)
        {
            var groups = await _carol.CompanyGroupsAsync(cancellationToken);
            var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(activeCoId));
            if (!string.IsNullOrWhiteSpace(group?.Gstin))
            {
                query = query.Where(e => e.FromGSTIN == group.Gstin);
            }
        }
        var rows = await query
            .OrderByDescending(e => e.GeneratedDate)
            .ToListAsync(cancellationToken);
        var source = DisplaySource();
        return rows.Select(r => MapToResponse(r, source)).ToList();
    }

    // Display source for already-stored records: we don't persist per-record
    // provenance (no schema change), so reflect whether the EWB API is live now.
    private string DisplaySource() => _ewbClient.IsConfigured ? _ewbClient.SourceLabel : "STUB";

    private static InvoiceResponse ToInvoiceResponse(Invoice inv) => new()
    {
        BillId = 0,
        InvoiceNumber = inv.InvoiceNumber,
        InvoiceDate = inv.InvoiceDate,
        PartyName = inv.PartyName,
        PartyGSTIN = inv.PartyGSTIN,
        PlaceOfSupply = inv.PlaceOfSupply,
        TaxableValue = inv.TaxableValue,
        CGST = inv.CGST,
        SGST = inv.SGST,
        IGST = inv.IGST,
        TotalAmount = inv.TotalAmount,
        Lines = inv.Lines.Select(l => new InvoiceLineResponse
        {
            Description = l.Description,
            HSNCode = l.HSNCode,
            Quantity = l.Quantity,
            Rate = l.Rate,
            TaxableValue = l.TaxableValue,
            GstRate = l.GstRate,
            CGST = l.CGST,
            SGST = l.SGST,
            IGST = l.IGST,
            Total = l.Total,
        }).ToList(),
    };

    private static EWayBillResponse MapToResponse(EWayBill r, string source)
    {
        var effectiveStatus = r.Status;
        if (r.Status == EWayBillStatus.Active && DateTime.UtcNow > r.ValidUntil)
        {
            effectiveStatus = EWayBillStatus.Expired;
        }

        return new EWayBillResponse
        {
            EWBId = r.EWBId,
            InvoiceId = r.InvoiceId,
            EWBNumber = r.EWBNumber,
            InvoiceNo = r.InvoiceNo,
            GeneratedDate = r.GeneratedDate,
            ValidUntil = r.ValidUntil,
            FromGSTIN = r.FromGSTIN,
            FromAddress = r.FromAddress,
            ToGSTIN = r.ToGSTIN,
            ToAddress = r.ToAddress,
            TransporterGSTIN = r.TransporterGSTIN,
            TransporterName = r.TransporterName,
            VehicleNumber = r.VehicleNumber,
            Distance = r.Distance,
            Mode = r.Mode,
            Status = effectiveStatus,
            CancelledOn = r.CancelledOn,
            CancelReason = r.CancelReason,
            CreatedOn = r.CreatedOn,
            Source = source,
        };
    }

    private static TimeSpan ComputeValidity(decimal distance)
    {
        var days = distance <= 0m ? 1 : (int)Math.Ceiling((double)distance / 200d);
        if (days < 1) days = 1;
        return TimeSpan.FromDays(days);
    }

    private static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return EWayBillMode.Road;
        var trimmed = mode.Trim();
        if (!AllowedModes.Contains(trimmed))
        {
            throw new ArgumentException($"Mode must be one of: Road, Rail, Air, Ship. Got '{mode}'.", nameof(mode));
        }
        return AllowedModes.First(m => m.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static string TransModeCode(string? mode) => mode switch
    {
        EWayBillMode.Rail => "2",
        EWayBillMode.Air => "3",
        EWayBillMode.Ship => "4",
        _ => "1",
    };

    private static int StateCodeInt(string? gstin)
        => !string.IsNullOrWhiteSpace(gstin) && gstin.Length >= 2 && int.TryParse(gstin[..2], out var sc) ? sc : 32;

    private static bool IsReal(string? v) => !string.IsNullOrWhiteSpace(v);
}

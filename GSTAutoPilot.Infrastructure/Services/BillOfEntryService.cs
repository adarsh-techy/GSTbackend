using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class BillOfEntryService : IBillOfEntryService
{
    private readonly TenantDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public BillOfEntryService(TenantDbContext db, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<IReadOnlyList<BillOfEntryDto>> ListAsync(string period, CancellationToken cancellationToken = default)
    {
        var p = ValidatePeriod(period);
        var tenantId = TenantId();
        var rows = await _db.BillsOfEntry.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.Period == p)
            .OrderByDescending(b => b.BoEDate).ThenBy(b => b.BoENumber)
            .ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    public async Task<BillOfEntryDto> CreateAsync(SaveBillOfEntryCommand command, CancellationToken cancellationToken = default)
    {
        var row = new BillOfEntry { TenantId = TenantId(), CreatedOn = DateTime.UtcNow };
        Apply(row, command);
        row.UpdatedOn = row.CreatedOn;
        _db.BillsOfEntry.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<BillOfEntryDto?> UpdateAsync(int boeId, SaveBillOfEntryCommand command, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId();
        var row = await _db.BillsOfEntry.FirstOrDefaultAsync(b => b.BoEId == boeId && b.TenantId == tenantId, cancellationToken);
        if (row is null) return null;
        Apply(row, command);
        row.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<bool> DeleteAsync(int boeId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId();
        var row = await _db.BillsOfEntry.FirstOrDefaultAsync(b => b.BoEId == boeId && b.TenantId == tenantId, cancellationToken);
        if (row is null) return false;
        _db.BillsOfEntry.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<BillOfEntryPeriodTotals> GetPeriodTotalsAsync(string period, CancellationToken cancellationToken = default)
    {
        var p = ValidatePeriod(period);
        var tenantId = TenantId();
        var rows = await _db.BillsOfEntry.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.Period == p)
            .ToListAsync(cancellationToken);
        return new BillOfEntryPeriodTotals
        {
            Count = rows.Count,
            AssessableValue = decimal.Round(rows.Sum(r => r.AssessableValue), 2),
            IGSTAmount = decimal.Round(rows.Sum(r => r.IGSTAmount), 2),
            CessAmount = decimal.Round(rows.Sum(r => r.CessAmount), 2),
        };
    }

    private static void Apply(BillOfEntry row, SaveBillOfEntryCommand cmd)
    {
        row.Period = ValidatePeriod(cmd.Period);
        row.BoENumber = string.IsNullOrWhiteSpace(cmd.BoENumber)
            ? throw new ArgumentException("BoE number is required.", nameof(cmd.BoENumber))
            : cmd.BoENumber.Trim();
        row.BoEDate = cmd.BoEDate == default ? throw new ArgumentException("BoE date is required.", nameof(cmd.BoEDate)) : cmd.BoEDate;
        row.PortCode = Trim(cmd.PortCode);
        row.SupplierName = Trim(cmd.SupplierName);
        row.SupplierGSTIN = Trim(cmd.SupplierGSTIN);
        row.AssessableValue = Round(cmd.AssessableValue);
        row.IGSTAmount = Round(cmd.IGSTAmount);
        row.CessAmount = Round(cmd.CessAmount);
        row.Remarks = Trim(cmd.Remarks);
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static decimal Round(decimal d) => decimal.Round(d, 2);

    private static string ValidatePeriod(string period)
    {
        var p = (period ?? string.Empty).Trim();
        if (p.Length != 6 || !int.TryParse(p.AsSpan(0, 4), out _)
            || !int.TryParse(p.AsSpan(4, 2), out var m) || m < 1 || m > 12)
            throw new ArgumentException("period must be in YYYYMM format (e.g. 202604).", nameof(period));
        return p;
    }

    private static BillOfEntryDto Map(BillOfEntry b) => new()
    {
        BoEId = b.BoEId,
        Period = b.Period,
        BoENumber = b.BoENumber,
        BoEDate = b.BoEDate,
        PortCode = b.PortCode,
        SupplierName = b.SupplierName,
        SupplierGSTIN = b.SupplierGSTIN,
        AssessableValue = b.AssessableValue,
        IGSTAmount = b.IGSTAmount,
        CessAmount = b.CessAmount,
        Remarks = b.Remarks,
    };

    private Guid TenantId()
        => (_httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant)?.TenantId
            ?? throw new InvalidOperationException("Tenant not resolved; X-Tenant-Id header is required.");
}

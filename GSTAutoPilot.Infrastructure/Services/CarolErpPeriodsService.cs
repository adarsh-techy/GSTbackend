using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GSTAutoPilot.Infrastructure.Services;

public class CarolErpPeriodsService : ICarolErpPeriodsService
{
    private readonly CarolDocumentReader _reader;
    private readonly SpOutwardService _spOutward;
    private readonly SpInwardService _spInward;
    private readonly CarolERPDbContext _carol;
    private readonly IHttpContextAccessor _http;
    private readonly ErpQueryCache _cache;
    private readonly PerformanceOptions _perf;

    public CarolErpPeriodsService(
        CarolDocumentReader reader,
        SpOutwardService spOutward,
        SpInwardService spInward,
        CarolERPDbContext carol,
        IHttpContextAccessor http,
        ErpQueryCache cache,
        IOptions<PerformanceOptions> perf)
    {
        _reader = reader;
        _spOutward = spOutward;
        _spInward = spInward;
        _carol = carol;
        _http = http;
        _cache = cache;
        _perf = perf.Value;
    }

    // Every page mounts usePeriod(), so this is on the critical path of the
    // first render app-wide. On the SP path it is also the widest read there
    // is (a whole window of invoice lines, counted and discarded), so the
    // result is cached for a few minutes — monthly invoice counts do not
    // change meaningfully faster than that.
    public Task<IReadOnlyList<CarolErpPeriod>> ListPeriodsAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = (_http.HttpContext?.Items["Tenant"] as Tenant)?.TenantId;
        var key = $"periods:{tenantId}:{_carol.ActiveCompanyId?.ToString() ?? "all"}";
        return _cache.GetOrAddAsync(
            key,
            TimeSpan.FromSeconds(_perf.PeriodsCacheSeconds),
            BuildPeriodsAsync,
            cancellationToken);
    }

    private async Task<IReadOnlyList<CarolErpPeriod>> BuildPeriodsAsync(CancellationToken cancellationToken)
    {
        // When an SP is configured for a direction it is the source of truth, so
        // the period counts must come from it too (otherwise the selector shows
        // the table-mapping count while the lists show the SP).
        var sales = _spOutward.IsConfigured
            ? await _spOutward.OutwardCountsByPeriodAsync(cancellationToken)
            : await _reader.OutwardCountsByPeriodAsync(cancellationToken);
        var purchases = _spInward.IsConfigured
            ? await _spInward.InwardCountsByPeriodAsync(cancellationToken)
            : await _reader.InwardCountsByPeriodAsync(cancellationToken);

        var byPeriod = new Dictionary<string, CarolErpPeriod>();
        foreach (var (period, count) in sales)
        {
            byPeriod[period] = new CarolErpPeriod { Period = period, SalesCount = count };
        }
        foreach (var (period, count) in purchases)
        {
            if (!byPeriod.TryGetValue(period, out var existing))
            {
                existing = new CarolErpPeriod { Period = period };
                byPeriod[period] = existing;
            }
            existing.PurchaseCount = count;
        }

        return byPeriod.Values
            .OrderByDescending(p => p.Period)
            .ToList();
    }
}

using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;

namespace GSTAutoPilot.Infrastructure.Services;

public class CarolErpPeriodsService : ICarolErpPeriodsService
{
    private readonly CarolDocumentReader _reader;
    private readonly SpOutwardService _spOutward;
    private readonly SpInwardService _spInward;

    public CarolErpPeriodsService(CarolDocumentReader reader, SpOutwardService spOutward, SpInwardService spInward)
    {
        _reader = reader;
        _spOutward = spOutward;
        _spInward = spInward;
    }

    public async Task<IReadOnlyList<CarolErpPeriod>> ListPeriodsAsync(CancellationToken cancellationToken = default)
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

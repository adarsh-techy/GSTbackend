using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface ICarolErpPeriodsService
{
    Task<IReadOnlyList<CarolErpPeriod>> ListPeriodsAsync(CancellationToken cancellationToken = default);
}

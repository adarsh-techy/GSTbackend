using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface ICompanyService
{
    Task<CompanyDto?> GetAsync(CancellationToken cancellationToken = default);
}

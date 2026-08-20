using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/company")]
[Authorize]
public class CompanyController : ControllerBase
{
    private readonly ICompanyService _companyService;

    public CompanyController(ICompanyService companyService)
    {
        _companyService = companyService;
    }

    [HttpGet]
    public async Task<ActionResult<CompanyDto>> Get(CancellationToken cancellationToken)
    {
        var company = await _companyService.GetAsync(cancellationToken);
        return company is null ? NotFound() : Ok(company);
    }
}

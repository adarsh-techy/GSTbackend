using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Infrastructure.CarolERP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.API.Controllers;

// Lists CarolERP companies for the resolved tenant — grouped by effective
// GSTIN. A tenant typically has many branch rows in `company` but only 1-2
// distinct GST registrations; the dropdown wants one entry per GST. The
// active group is then selected via X-Company-Id (header carries the
// group's representative CoId; ApplyCompanyFilter expands it to every
// member CoId's DocIds upstream). Null/missing header = "All companies".
[ApiController]
[Route("api/companies")]
[Authorize]
public class CompaniesController : ControllerBase
{
    private readonly CarolERPDbContext _carol;

    public CompaniesController(CarolERPDbContext carol)
    {
        _carol = carol;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CompanySummaryDto>>> List(CancellationToken cancellationToken)
    {
        var groups = await _carol.CompanyGroupsAsync(cancellationToken);
        if (groups.Count == 0) return Ok(Array.Empty<CompanySummaryDto>());

        // Bill counts per CoId across the whole CarolERP install — summed
        // into the group total. DocId → CoId path is the same one the filter
        // uses, so counts match what the user will see when they pick.
        var docToCompany = await _carol.DocIdToCompanyMapAsync(cancellationToken);
        var billsByDoc = await _carol.PurchaseHeaders.AsNoTracking()
            .Where(h => h.DocId != null)
            .GroupBy(h => h.DocId!.Value)
            .Select(g => new { DocId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var billsByCo = new Dictionary<byte, int>();
        foreach (var row in billsByDoc)
        {
            if (docToCompany.TryGetValue(row.DocId, out var co))
                billsByCo[co] = billsByCo.GetValueOrDefault(co) + row.Count;
        }

        var dtos = groups.Select(g => new CompanySummaryDto
        {
            CoId = g.RepCoId,
            CoName = g.CoName,
            GstNo = string.IsNullOrWhiteSpace(g.Gstin) ? null : g.Gstin,
            BillCount = g.MemberCoIds.Sum(coId => billsByCo.GetValueOrDefault(coId)),
            StateId = null, // group-level — varies by branch, not meaningful here
        }).ToList();
        return Ok(dtos);
    }
}

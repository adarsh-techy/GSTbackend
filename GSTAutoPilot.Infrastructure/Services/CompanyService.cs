using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Infrastructure.CarolERP;

namespace GSTAutoPilot.Infrastructure.Services;

public class CompanyService : ICompanyService
{
    private readonly CarolERPDbContext _carol;

    public CompanyService(CarolERPDbContext carol)
    {
        _carol = carol;
    }

    public async Task<CompanyDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        // Flavor-aware raw-SQL projection in ListCompaniesAsync dodges column
        // drift (GstNo vs GSTNumber, EmailSales vs Email) and substitutes
        // NULL for Flooratex-only fields when the tenant is KSCC flavor.
        var rows = await _carol.ListCompaniesAsync(cancellationToken);

        // Prefer the active company (sidebar selection). Multi-GST tenants
        // like KSCC need the sidebar brand-tag + Settings → Company section
        // to reflect the currently-selected GST registration — without this
        // both stay frozen on CoId 1 (the main entity) even when the user
        // switches to a second GST in the dropdown. Falls back to the first
        // row when X-Company-Id is unset or the CoId isn't in this tenant.
        var c = _carol.ActiveCompanyId is byte activeCoId
            ? rows.FirstOrDefault(r => r.CoId == activeCoId) ?? rows.FirstOrDefault()
            : rows.FirstOrDefault();
        if (c is null) return null;
        return new CompanyDto
        {
            CompanyName = c.CoName ?? string.Empty,
            Address1 = c.Address1,
            Address2 = c.Address2,
            Address3 = c.Address3,
            Phone = c.Phone,
            GSTIN = c.GstNo?.Trim(),
            PAN = c.Pan,
            BankName = c.BankName,
            AccountNo = c.AccountNo,
            IFSCCode = c.IFSCCode,
            BranchName = c.BranchName,
            Email = c.Email,
            PinCode = c.PinCode,
            IECode = c.IECode,
            BankAccName = c.BankAccName,
        };
    }
}

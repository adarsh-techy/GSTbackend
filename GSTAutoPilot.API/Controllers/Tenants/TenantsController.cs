using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.API.Controllers;

// Lists active tenants for the header selector. This is intentionally NOT
// tenant-scoped — it queries the master DB directly and doesn't require
// X-Tenant-Id (so it works for users about to switch tenant).
[ApiController]
[Route("api/tenants")]
[Authorize]
public class TenantsController : ControllerBase
{
    private readonly MasterDbContext _master;

    public TenantsController(MasterDbContext master)
    {
        _master = master;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantSummaryDto>>> List(CancellationToken cancellationToken)
    {
        var rows = await _master.Tenants
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new TenantSummaryDto
            {
                TenantId = t.TenantId,
                Name = t.Name,
                Gstin = t.GSTIN,
                Flavor = t.CarolErpFlavor,
                IsActive = t.IsActive,
            })
            .ToListAsync(cancellationToken);
        return Ok(rows);
    }

    // Pre-login company picker: minimal active-tenant list (id + name only, no
    // GSTIN/secrets) so the login screen can let the user choose which client
    // they're signing into. AllowAnonymous + not tenant-scoped — the login
    // screen calls this BEFORE any tenant/JWT exists (and without the
    // X-Tenant-Id header, since the baked default may not exist on this server).
    [AllowAnonymous]
    [HttpGet("public")]
    public async Task<ActionResult<IReadOnlyList<TenantSummaryDto>>> PublicList(CancellationToken cancellationToken)
    {
        var rows = await _master.Tenants
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new TenantSummaryDto { TenantId = t.TenantId, Name = t.Name })
            .ToListAsync(cancellationToken);
        return Ok(rows);
    }

    // Onboarding: provision a new tenant (client) from supplied config — no code
    // change needed to add a client. Created INACTIVE; the admin activates it
    // after configuring SP profile / credentials / mappings.
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CreateTenantResponse>> Create([FromBody] CreateTenantRequest req, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Client name is required.");
        var gstin = (req.Gstin ?? string.Empty).Trim().ToUpperInvariant();
        if (gstin.Length != 15) return BadRequest("GSTIN must be exactly 15 characters.");
        if (string.IsNullOrWhiteSpace(req.AppDbConnection)) return BadRequest("App database connection is required.");
        if (string.IsNullOrWhiteSpace(req.CarolErpConnection)) return BadRequest("CarolERP connection is required.");
        if (await _master.Tenants.AnyAsync(t => t.GSTIN == gstin, cancellationToken))
            return Conflict($"A tenant with GSTIN {gstin} already exists.");

        var tenant = new Tenant
        {
            Name = req.Name.Trim(),
            GSTIN = gstin,
            ConnectionString = req.AppDbConnection.Trim(),
            CarolERPConnection = req.CarolErpConnection.Trim(),
            CarolErpFlavor = string.IsNullOrWhiteSpace(req.CarolErpFlavor) ? "Default" : req.CarolErpFlavor.Trim(),
            OutwardSP = string.IsNullOrWhiteSpace(req.OutwardSP) ? null : req.OutwardSP.Trim(),
            InwardSP = string.IsNullOrWhiteSpace(req.InwardSP) ? null : req.InwardSP.Trim(),
            SalesHeaderTable = string.IsNullOrWhiteSpace(req.SalesHeaderTable) ? "Bill_File_mas" : req.SalesHeaderTable.Trim(),
            SalesDocId = req.SalesDocId,
            SalesLineTable = string.IsNullOrWhiteSpace(req.SalesLineTable) ? "Bill_File_trn" : req.SalesLineTable.Trim(),
            IsActive = false,
        };
        _master.Tenants.Add(tenant);
        await _master.SaveChangesAsync(cancellationToken);
        return Ok(new CreateTenantResponse { TenantId = tenant.TenantId });
    }

    // Verifies a supplied connection string is reachable before the tenant is
    // created / activated. "carolerp" also confirms the company master is
    // readable; "app" reports how many migrations the tenant DB has applied.
    [HttpPost("test-connection")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TestConnectionResult>> TestConnection([FromBody] TestConnectionRequest req, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(req.ConnectionString))
            return Ok(new TestConnectionResult { Ok = false, Message = "Connection string is empty." });

        var isCarol = string.Equals(req.Kind, "carolerp", StringComparison.OrdinalIgnoreCase);
        try
        {
            await using var conn = new SqlConnection(req.ConnectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = isCarol
                ? "SELECT COUNT(*) FROM company"
                : "SELECT CASE WHEN OBJECT_ID('dbo.__EFMigrationsHistory','U') IS NULL THEN -1 ELSE (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory) END";
            var scalar = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
            var message = isCarol
                ? $"Connected — {scalar} companies in the CarolERP database."
                : scalar < 0
                    ? "Connected, but the tenant DB has no schema yet — run the tenant-DB migration .sql scripts before activating."
                    : $"Connected — tenant DB reachable ({scalar} migrations applied).";
            return Ok(new TestConnectionResult { Ok = true, Message = message });
        }
        catch (Exception ex)
        {
            return Ok(new TestConnectionResult { Ok = false, Message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/activate")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> Activate(Guid id, CancellationToken cancellationToken)
    {
        var tenant = await _master.Tenants.FirstOrDefaultAsync(t => t.TenantId == id, cancellationToken);
        if (tenant is null) return NotFound();
        tenant.IsActive = true;
        await _master.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}

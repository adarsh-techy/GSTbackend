using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.API.Middleware;

public class TenantMiddleware
{
    private const string TenantHeader = "X-Tenant-Id";
    // Optional. Restricts CarolERP reads to a single CoId. Omit or "all" to
    // span every company in the tenant. Parsed as a byte (tinyint).
    private const string CompanyHeader = "X-Company-Id";
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, MasterDbContext masterDb)
    {
        if (!context.Request.Headers.TryGetValue(TenantHeader, out var tenantHeaderValue) ||
            !Guid.TryParse(tenantHeaderValue, out var tenantId))
        {
            await _next(context);
            return;
        }

        var tenant = await masterDb.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.IsActive);

        if (tenant is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Tenant not found or inactive.");
            return;
        }

        context.Items["Tenant"] = tenant;

        // Carry the tenant's universal Document Mapping rows alongside the
        // tenant so CarolERPDbContext can resolve which header/line tables and
        // DocIds back each GST category. Empty until first seeded via Settings,
        // in which case CarolERPDbContext falls back to the legacy Tenant.Sales*
        // columns and behaviour is unchanged.
        var mappings = await masterDb.DocumentMappings
            .AsNoTracking()
            .Where(d => d.TenantId == tenant.TenantId)
            .ToListAsync();
        context.Items["DocumentMappings"] = mappings;

        // Optional active-company gate. byte? — null means "all companies".
        if (context.Request.Headers.TryGetValue(CompanyHeader, out var coHeader)
            && byte.TryParse(coHeader.ToString(), out var coId)
            && coId > 0)
        {
            context.Items["CompanyId"] = coId;
        }

        _logger.LogInformation(
            "Resolved tenant {TenantId} ({Name}), {MappingCount} document mappings, company={CoId}",
            tenant.TenantId, tenant.Name, mappings.Count,
            context.Items["CompanyId"] ?? "ALL");

        await _next(context);
    }
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantMiddleware>();
}

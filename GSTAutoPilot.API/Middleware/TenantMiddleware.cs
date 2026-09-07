using GSTAutoPilot.API.Configuration;
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
    // Anonymous, tenant-less: the login screen's client picker.
    private static readonly PathString PublicTenantsPath = "/api/tenants/public";
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, MasterDbContext masterDb, TenantVisibility visibility)
    {
        if (!context.Request.Headers.TryGetValue(TenantHeader, out var tenantHeaderValue) ||
            !Guid.TryParse(tenantHeaderValue, out var tenantId))
        {
            await _next(context);
            return;
        }

        // This deployment is configured to serve only some of the tenants it holds.
        // 401 rather than 404 on purpose: a browser holding a now-restricted tenant
        // in localStorage gets logged out and lands back on the picker (which lists
        // only the permitted clients) instead of erroring on every request.
        if (!visibility.IsVisible(tenantId))
        {
            // ...except the pre-login picker, which needs no tenant and is exactly
            // what such a browser must reach to correct itself. Rejecting it here
            // left the stale list on screen with no way to refresh.
            if (context.Request.Path.StartsWithSegments(PublicTenantsPath))
            {
                await _next(context);
                return;
            }

            _logger.LogInformation("Rejected restricted tenant {TenantId}", tenantId);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("This client is not available on this deployment.");
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

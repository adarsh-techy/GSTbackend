using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GSTAutoPilot.Infrastructure.Persistence;

// Used only by `dotnet ef` at design time. TenantDbContext is normally built
// per-request from the resolved tenant's connection string, but EF tooling
// needs to instantiate the context without an HttpContext.
public class TenantDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer("Server=localhost;Database=_DesignTime_Tenant;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new TenantDbContext(options);
    }
}

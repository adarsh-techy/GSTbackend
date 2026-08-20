using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GSTAutoPilot.Infrastructure.CarolERP;

// Used only by `dotnet ef` at design time. CarolERPDbContext is normally built
// per-request from the resolved tenant's connection string, but EF tooling
// needs to instantiate the context without an HttpContext. This factory hands
// over a context bound to a placeholder connection — migrations are never
// generated against CarolERP (it's a read-only foreign schema).
public class CarolERPDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CarolERPDbContext>
{
    public CarolERPDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CarolERPDbContext>()
            .UseSqlServer("Server=localhost;Database=_DesignTime_CarolERP;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new CarolERPDbContext(options);
    }
}

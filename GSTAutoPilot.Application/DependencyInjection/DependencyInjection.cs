using Microsoft.Extensions.DependencyInjection;

namespace GSTAutoPilot.Application.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Application layer registrations (validators, pipeline behaviors, etc.)
        return services;
    }
}

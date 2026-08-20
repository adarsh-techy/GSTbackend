using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace GSTAutoPilot.API.Swagger;

public class TenantHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var route = context.ApiDescription.RelativePath ?? string.Empty;
        if (route.StartsWith("api/auth", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        operation.Parameters ??= new List<IOpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Tenant-Id",
            In = ParameterLocation.Header,
            Required = false,
            Description = "Tenant GUID resolved against the master Tenants table.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
}

using GSTAutoPilot.Application.Configuration;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.Persistence;
using GSTAutoPilot.Infrastructure.Services;
using GSTAutoPilot.Infrastructure.Services.Advisor;
using GSTAutoPilot.Infrastructure.Services.Bulk;
using GSTAutoPilot.Infrastructure.Services.EwbApi;
using GSTAutoPilot.Infrastructure.Services.WhiteBooks;
using GSTAutoPilot.Infrastructure.Services.WhiteBooksGst;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GSTAutoPilot.Infrastructure.DependencyInjection;

public static class DependencyInjection
{
    private static void ConfigureSqlServer(SqlServerDbContextOptionsBuilder sqlOptions) =>
        sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorNumbersToAdd: null);

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Persistence & Multi-Tenant DbContexts
        services.AddDbContext<MasterDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("MasterConnection"), ConfigureSqlServer));

        services.AddDbContext<TenantDbContext>((sp, options) =>
        {
            if (EF.IsDesignTime)
            {
                options.UseSqlServer("Server=localhost;Database=_DesignTime_Tenant;Trusted_Connection=True;TrustServerCertificate=True;", ConfigureSqlServer);
                return;
            }
            var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
            var tenant = httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
                ?? throw new InvalidOperationException(
                    "TenantDbContext requires a resolved tenant; ensure the X-Tenant-Id header is set and TenantMiddleware ran.");
            options.UseSqlServer(tenant.ConnectionString, ConfigureSqlServer);
        });

        services.AddDbContext<CarolERPDbContext>((sp, options) =>
        {
            if (EF.IsDesignTime)
            {
                options.UseSqlServer("Server=localhost;Database=_DesignTime_CarolERP;Trusted_Connection=True;TrustServerCertificate=True;", ConfigureSqlServer);
                return;
            }
            var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
            var tenant = httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
                ?? throw new InvalidOperationException(
                    "CarolERPDbContext requires a resolved tenant; ensure the X-Tenant-Id header is set and TenantMiddleware ran.");
            if (string.IsNullOrWhiteSpace(tenant.CarolERPConnection))
            {
                throw new InvalidOperationException(
                    $"Tenant '{tenant.Name}' has no CarolERPConnection configured. Set Tenants.CarolERPConnection in MasterDb before calling CarolERP-backed endpoints.");
            }
            // Target SQL Server 2014 (compat level 120) for CarolERP DB
            options.UseSqlServer(tenant.CarolERPConnection, sql =>
            {
                ConfigureSqlServer(sql);
                sql.UseCompatibilityLevel(120);
            });
        });

        // Core ERP & Sales Line Providers
        services.AddScoped<SalesLineProvider>();
        services.AddScoped<CarolDocumentReader>();
        services.AddScoped<SpOutwardService>();
        services.AddScoped<SpInwardService>();

        // Domain & Application Services
        services.AddScoped<IInvoiceService, InvoiceService>();
        InvoiceService.ApplyGstRules(
            configuration.GetSection(GstRulesOptions.SectionName).Get<GstRulesOptions>()
            ?? new GstRulesOptions());

        services.AddScoped<IGstr3bService, Gstr3bService>();
        services.AddScoped<IPurchaseInvoiceService, PurchaseInvoiceService>();
        services.AddScoped<IGstr2bService, Gstr2bService>();
        services.AddScoped<IReconService, ReconService>();
        services.AddScoped<IGstSummaryService, GstSummaryService>();

        // External WhiteBooks Clients
        services.Configure<WhiteBooksOptions>(configuration.GetSection(WhiteBooksOptions.SectionName));
        services.AddHttpClient<IWhiteBooksClient, WhiteBooksClient>();

        services.Configure<WhiteBooksGstOptions>(configuration.GetSection(WhiteBooksGstOptions.SectionName));
        services.AddHttpClient<IWhiteBooksGstClient, WhiteBooksGstClient>();

        services.Configure<WhiteBooksEWayBillOptions>(configuration.GetSection(WhiteBooksEWayBillOptions.SectionName));
        services.AddHttpClient<IWhiteBooksEWayBillClient, WhiteBooksEWayBillClient>();

        // Additional Business Services
        services.AddScoped<IEInvoiceService, EInvoiceService>();
        services.AddScoped<IEWayBillService, EWayBillService>();
        services.AddScoped<IGstinValidationService, GstinValidationService>();
        services.AddScoped<ICarolErpPeriodsService, CarolErpPeriodsService>();
        services.AddScoped<IDocumentMappingService, DocumentMappingService>();
        services.AddScoped<IBillOfEntryService, BillOfEntryService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserRolesService, UserRolesService>();
        services.AddScoped<ICompanyService, CompanyService>();
        services.AddScoped<ITenantSettingsService, TenantSettingsService>();
        services.AddScoped<IInvoicePdfService, InvoicePdfService>();
        services.AddScoped<IExportService, ExportService>();
        services.AddScoped<IGstnReturnService, GstnReturnService>();
        services.AddScoped<IFilingService, FilingService>();
        services.AddScoped<IReturnValidationService, ReturnValidationService>();

        // Bulk Operations & Rate Limiter
        services.AddSingleton<OperationRateLimiter>();
        services.AddScoped<IBulkOperationsService, BulkOperationsService>();

        // AI Advisor & Security Options
        services.Configure<Sec175Options>(configuration.GetSection(Sec175Options.SectionName));
        services.Configure<AdvisorOptions>(configuration.GetSection(AdvisorOptions.SectionName));
        services.AddScoped<IGstAdvisorService, GstAdvisorService>();

        services.AddDataProtection();
        services.AddScoped<ISecretProtector, SecretProtector>();
        services.AddScoped<IEmailService, EmailService>();

        return services;
    }
}

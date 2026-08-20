using System.Text;
using GSTAutoPilot.API.Middleware;
using GSTAutoPilot.API.Swagger;
using GSTAutoPilot.Application.DependencyInjection;
using GSTAutoPilot.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Run cleanly under the Windows Service Control Manager when installed as a
// service; a no-op when launched as a normal console process.
builder.Host.UseWindowsService();

// Clean Architecture Dependency Injections
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Configure CORS for Vercel and local frontend clients
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(
                "http://localhost:5173",
                "http://localhost:3000",
                "https://gs-tclient.vercel.app",
                "https://gs-tclient-git-main-adarsh-techys-projects.vercel.app"
              )
              .SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Global Exception Handler & ProblemDetails
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Behind a reverse proxy (IIS / nginx) that terminates TLS, the real scheme and
// client IP arrive in X-Forwarded-* headers — honour them so HTTPS detection
// and logging are correct.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllers(o =>
    {
        // Every GSTN/WhiteBooks rejection answers in one shape, with the portal
        // code and a next-step action, from whichever endpoint raised it.
        o.Filters.Add<GSTAutoPilot.API.Filters.GstnExceptionFilter>();
    })
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddHttpContextAccessor();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "GSTAutoPilot API",
        Version = "v1",
        Description = "Multi-tenant GST SaaS API"
    });
    options.OperationFilter<TenantHeaderOperationFilter>();
});

var jwtSection = builder.Configuration.GetSection("Jwt");
// `?? throw` alone only catches a MISSING key. An unset-but-present value — the
// "[set via user-secrets...]" placeholder in the committed appsettings.json — would
// otherwise be used as a real signing key, which is far worse than failing:
// the key is public, so anyone could mint a token for any tenant. Fail loudly.
var jwtKey = jwtSection["Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.TrimStart().StartsWith('['))
{
    throw new InvalidOperationException(
        "Jwt:Key is not configured. In Development set it with "
        + "`dotnet user-secrets set \"Jwt:Key\" \"<long random string>\"`; "
        + "elsewhere supply the Jwt__Key environment variable. See README.md.");
}
// HMAC-SHA256 needs a >=256-bit key; a shorter one fails later with an opaque
// cryptographic error rather than pointing at the config.
if (System.Text.Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException(
        $"Jwt:Key must be at least 32 bytes for HMAC-SHA256 (got {System.Text.Encoding.UTF8.GetByteCount(jwtKey)}).");
}
var jwtIssuer = jwtSection["Issuer"];
var jwtAudience = jwtSection["Audience"];

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// Configure Global Exception Middleware Pipeline
app.UseExceptionHandler();

// Apply X-Forwarded-* before anything that inspects scheme/host (HTTPS redirect,
// auth). Must run first in the pipeline.
app.UseForwardedHeaders();

app.UseCors("AllowFrontend");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "GSTAutoPilot API v1");
    });
}

// On by default. Set Hosting:EnableHttpsRedirection=false (env:
// Hosting__EnableHttpsRedirection=false) when the app runs HTTP-only behind a
// TLS-terminating proxy, to avoid redirect loops.
if (app.Configuration.GetValue("Hosting:EnableHttpsRedirection", true))
{
    app.UseHttpsRedirection();
}

// Ensure wwwroot exists so UseStaticFiles can serve uploaded logos. The
// IWebHostEnvironment.WebRootPath is fixed at construction time, so a missing
// directory at startup silently disables static-file middleware.
var webRoot = app.Environment.WebRootPath;
if (string.IsNullOrWhiteSpace(webRoot))
{
    webRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
}
Directory.CreateDirectory(Path.Combine(webRoot, "uploads", "logos"));
// Serve the bundled React SPA from wwwroot ("/" -> index.html, then static assets).
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseTenantResolution();
app.UseAuthorization();

app.MapControllers();

// SPA client-side routes (e.g. /gstr1, /recon) fall back to index.html so a
// deep-link / refresh loads the app instead of 404ing.
app.MapFallbackToFile("index.html");

app.Run();

using System.IO.Compression;
using System.Text;
using GSTAutoPilot.API.Middleware;
using Microsoft.AspNetCore.ResponseCompression;
using GSTAutoPilot.API.Swagger;
using GSTAutoPilot.Application.DependencyInjection;
using GSTAutoPilot.Infrastructure.DependencyInjection;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Run cleanly under the Windows Service Control Manager when installed as a
// service — but never under IIS.
//
// UseWindowsService() decides whether it is running as a service by looking for a
// non-interactive session, which is exactly what the IIS worker process (w3wp) is.
// Hosted in IIS it therefore tried to attach to the Service Control Manager, failed
// immediately, and died before any logging was configured. IIS could only report the
// generic "HTTP Error 500.30 - ASP.NET Core app failed to start", and stdout logging
// showed nothing because the failure happened before the log file was opened. The
// same build ran perfectly as a console app, which is what made this hard to spot.
//
// ANCM sets these variables for both in-process and out-of-process hosting.
var underIis =
    Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH") is not null
    || Environment.GetEnvironmentVariable("ASPNETCORE_IIS_HTTPAUTH") is not null
    || Environment.GetEnvironmentVariable("ASPNETCORE_IIS_APP_POOL_ID") is not null;

if (!underIis)
{
    builder.Host.UseWindowsService();
}

// Which tenants this deployment lists and serves (config: "Tenants:Only" /
// "Tenants:Hidden"). Singleton — the configuration is read once at startup.
builder.Services.AddSingleton<GSTAutoPilot.API.Configuration.TenantVisibility>();

// Clean Architecture Dependency Injections
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Configure CORS for all frontend clients (local, deployed VPS, Vercel, etc.)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()
              .WithExposedHeaders("Content-Disposition", "X-Custom-Header")
              .SetPreflightMaxAge(TimeSpan.FromHours(1));
    });

    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()
              .WithExposedHeaders("Content-Disposition", "X-Custom-Header")
              .SetPreflightMaxAge(TimeSpan.FromHours(1));
    });
});

// Response compression. The invoice/GSTR endpoints return the whole period as
// one JSON document (every invoice, every line), and the client talks to this
// API over the public internet, so these bodies are transfer-bound rather than
// query-bound. JSON compresses roughly 5-10x.
//
// EnableForHttps is on deliberately: the BREACH/CRIME attacks that made this
// off-by-default apply to responses that mix a secret (a session cookie or an
// anti-forgery token) with attacker-controlled input. This API authenticates
// with a bearer token held in localStorage and sets no cookies, so there is no
// such secret in the response body.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/json", "application/problem+json" });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

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

// The deployed master DB may predate UserRoles.Permissions. Probe here, before the
// first MasterDbContext is resolved on the first request — EF compiles and caches
// the model on first use, so probing any later would not affect the mapping.
try
{
    var masterConnection = app.Configuration.GetConnectionString("MasterConnection");
    if (!string.IsNullOrWhiteSpace(masterConnection)
        && !await MasterSchema.ProbeAsync(masterConnection))
    {
        app.Logger.LogWarning(
            "master.UserRoles has no Permissions column. Per-user module permissions are " +
            "not persisted and non-admin users fall back to the full assignable module set. " +
            "Add a nullable Permissions column to restore per-user grants.");
    }
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Could not probe the master schema; assuming UserRoles.Permissions exists.");
}

// Configure Global Exception Middleware Pipeline
app.UseExceptionHandler();

// Apply X-Forwarded-* before anything that inspects scheme/host (HTTPS redirect,
// auth). Must run first in the pipeline.
app.UseForwardedHeaders();

app.UseRouting();
app.UseCors("AllowFrontend");

// Ahead of the static-file and controller middleware so it covers both the
// SPA bundle and the API responses.
app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "GSTAutoPilot API v1");
    });
}

// Off by default. Set Hosting:EnableHttpsRedirection=true (env:
// Hosting__EnableHttpsRedirection=true) only when the app directly terminates TLS.
if (app.Configuration.GetValue("Hosting:EnableHttpsRedirection", false))
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
try
{
    Directory.CreateDirectory(Path.Combine(webRoot, "uploads", "logos"));
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Could not create uploads/logos directory at startup: {Message}", ex.Message);
}
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

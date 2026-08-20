using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace GSTAutoPilot.Infrastructure.Services;

// Standalone authentication mode. The CarolERP Employee table has a legacy
// password hash that we are intentionally not honouring yet -- every user signs
// in with the fixed default password defined below. The SSO integration that
// will replace this check is tracked separately; once the legacy hash class is
// provided, swap the password comparison in LoginAsync.
// TODO: Replace with legacy hash comparison when SSO integration is done.
public class AuthService : IAuthService
{
    private const string StandaloneDefaultPassword = "123";
    private static readonly short[] AllowedMasTypes = { 7, 10 };

    private readonly MasterDbContext _master;
    private readonly CarolERPDbContext _carol;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;

    public AuthService(
        MasterDbContext master,
        CarolERPDbContext carol,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _master = master;
        _carol = carol;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    public async Task<LoginResult?> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Username)) return null;

        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved; X-Tenant-Id header is required to log in.");

        // TODO: Replace with legacy hash comparison when SSO integration is done.
        if (!string.Equals(command.Password, StandaloneDefaultPassword, StringComparison.Ordinal))
        {
            return null;
        }

        var employee = await (
            from e in _carol.Employees
            where e.EmplCode == command.Username
            join m in _carol.Masters on e.MasId equals m.MasId
            where AllowedMasTypes.Contains(m.MasType)
            select e
        ).FirstOrDefaultAsync(cancellationToken);

        if (employee is null) return null;

        var existingRole = await _master.UserRoles
            .FirstOrDefaultAsync(
                u => u.TenantId == tenant.TenantId && u.EmplCode == employee.EmplCode,
                cancellationToken);

        UserRole role;
        if (existingRole is not null)
        {
            if (!existingRole.IsActive) return null;
            role = existingRole;
        }
        else
        {
            var tenantHasAnyRole = await _master.UserRoles
                .AnyAsync(u => u.TenantId == tenant.TenantId, cancellationToken);
            if (tenantHasAnyRole)
            {
                // Access is gated by an explicit UserRoles row once the tenant has any user.
                return null;
            }
            // Bootstrap: first CarolERP employee to log in for this tenant becomes Admin.
            role = new UserRole
            {
                TenantId = tenant.TenantId,
                EmplId = employee.EmplId,
                EmplCode = employee.EmplCode,
                DisplayName = employee.EmplName,
                Role = "Admin",
                IsActive = true,
                CreatedOn = DateTime.UtcNow,
            };
            _master.UserRoles.Add(role);
            await _master.SaveChangesAsync(cancellationToken);
        }

        var (accessToken, expiresAt) = IssueJwt(role, employee.EmplName ?? employee.EmplCode, tenant.TenantId);

        return new LoginResult
        {
            AccessToken = accessToken,
            ExpiresAt = expiresAt,
            EmplCode = role.EmplCode,
            DisplayName = role.DisplayName ?? employee.EmplName ?? role.EmplCode,
            Role = role.Role,
            TenantId = tenant.TenantId,
        };
    }

    private (string Token, DateTime ExpiresAt) IssueJwt(UserRole role, string displayName, Guid tenantId)
    {
        var jwtSection = _configuration.GetSection("Jwt");
        var key = jwtSection["Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured.");
        var issuer = jwtSection["Issuer"];
        var audience = jwtSection["Audience"];

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var expiresAt = DateTime.UtcNow.AddHours(8);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, role.EmplCode),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new("name", displayName),
            new("tenant_id", tenantId.ToString()),
            new("empl_id", role.EmplId.ToString()),
            new(ClaimTypes.Role, role.Role),
            new("role", role.Role),
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

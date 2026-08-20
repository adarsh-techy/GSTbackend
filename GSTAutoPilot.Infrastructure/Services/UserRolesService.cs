using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class UserRolesService : IUserRolesService
{
    private static readonly short[] AllowedMasTypes = { 7, 10 };

    private readonly MasterDbContext _master;
    private readonly CarolERPDbContext _carol;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public UserRolesService(
        MasterDbContext master,
        CarolERPDbContext carol,
        IHttpContextAccessor httpContextAccessor)
    {
        _master = master;
        _carol = carol;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<IReadOnlyList<CarolEmployeeDto>> ListCarolEmployeesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();

        var employees = await (
            from e in _carol.Employees
            join m in _carol.Masters on e.MasId equals m.MasId
            where AllowedMasTypes.Contains(m.MasType)
            orderby e.EmplCode
            select new { e.EmplId, e.EmplCode, e.EmplName }
        ).ToListAsync(cancellationToken);

        var assignedCodes = await _master.UserRoles
            .Where(u => u.TenantId == tenant.TenantId)
            .Select(u => u.EmplCode)
            .ToListAsync(cancellationToken);
        var assignedSet = assignedCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return employees.Select(e => new CarolEmployeeDto
        {
            EmplId = e.EmplId,
            EmplCode = e.EmplCode,
            DisplayName = e.EmplName ?? string.Empty,
            IsAssigned = assignedSet.Contains(e.EmplCode),
        }).ToList();
    }

    public async Task<IReadOnlyList<UserRoleDto>> ListUserRolesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var rows = await _master.UserRoles
            .Where(u => u.TenantId == tenant.TenantId)
            .OrderBy(u => u.EmplCode)
            .ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    public async Task<UserRoleDto> AddAsync(AddUserRoleCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.EmplCode))
            throw new ArgumentException("EmplCode is required.", nameof(command));
        var tenant = RequireTenant();

        var employee = await (
            from e in _carol.Employees
            where e.EmplCode == command.EmplCode
            join m in _carol.Masters on e.MasId equals m.MasId
            where AllowedMasTypes.Contains(m.MasType)
            select e
        ).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Employee '{command.EmplCode}' not found in CarolERP with MasType 7 or 10.");

        var duplicate = await _master.UserRoles
            .FirstOrDefaultAsync(
                u => u.TenantId == tenant.TenantId && u.EmplCode == command.EmplCode,
                cancellationToken);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"User '{command.EmplCode}' is already assigned for this tenant.");
        }

        var entity = new UserRole
        {
            TenantId = tenant.TenantId,
            EmplId = employee.EmplId,
            EmplCode = employee.EmplCode,
            DisplayName = command.DisplayName ?? employee.EmplName,
            Role = string.IsNullOrWhiteSpace(command.Role) ? "User" : command.Role,
            IsActive = true,
            CreatedOn = DateTime.UtcNow,
        };
        _master.UserRoles.Add(entity);
        await _master.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<bool> RemoveAsync(int userRoleId, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var entity = await _master.UserRoles
            .FirstOrDefaultAsync(u => u.UserRoleId == userRoleId && u.TenantId == tenant.TenantId, cancellationToken);
        if (entity is null) return false;
        _master.UserRoles.Remove(entity);
        await _master.SaveChangesAsync(cancellationToken);
        return true;
    }

    private Tenant RequireTenant()
        => _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant
            ?? throw new InvalidOperationException("Tenant not resolved; X-Tenant-Id header is required.");

    private static UserRoleDto Map(UserRole u) => new()
    {
        UserRoleId = u.UserRoleId,
        EmplId = u.EmplId,
        EmplCode = u.EmplCode,
        DisplayName = u.DisplayName,
        Role = u.Role,
        IsActive = u.IsActive,
        CreatedOn = u.CreatedOn,
    };
}

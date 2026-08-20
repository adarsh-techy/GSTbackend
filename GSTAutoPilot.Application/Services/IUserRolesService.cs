using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IUserRolesService
{
    Task<IReadOnlyList<CarolEmployeeDto>> ListCarolEmployeesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserRoleDto>> ListUserRolesAsync(CancellationToken cancellationToken = default);
    Task<UserRoleDto> AddAsync(AddUserRoleCommand command, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(int userRoleId, CancellationToken cancellationToken = default);
}

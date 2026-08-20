using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IAuthService
{
    Task<LoginResult?> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default);
}

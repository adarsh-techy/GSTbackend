namespace GSTAutoPilot.Application.DTOs;

public class LoginCommand
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginResult
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string EmplCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
    public Guid TenantId { get; set; }
}

public class CarolEmployeeDto
{
    public int EmplId { get; set; }
    public string EmplCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsAssigned { get; set; }
}

public class UserRoleDto
{
    public int UserRoleId { get; set; }
    public int EmplId { get; set; }
    public string EmplCode { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Role { get; set; } = string.Empty;

    /// <summary>Effective module keys — an Admin row always resolves to every key.</summary>
    public List<string> Permissions { get; set; } = new();

    public bool IsActive { get; set; }
    public DateTime CreatedOn { get; set; }
}

public class AddUserRoleCommand
{
    public int EmplId { get; set; }
    public string EmplCode { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Role { get; set; } = "User";

    /// <summary>Module keys ticked by the admin. Ignored when Role is Admin.</summary>
    public List<string> Permissions { get; set; } = new();
}

public class UpdateUserRoleCommand
{
    public string Role { get; set; } = "User";
    public List<string> Permissions { get; set; } = new();
}

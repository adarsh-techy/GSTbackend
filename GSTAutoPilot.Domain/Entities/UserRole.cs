namespace GSTAutoPilot.Domain.Entities;

public class UserRole
{
    public int UserRoleId { get; set; }
    public Guid TenantId { get; set; }
    public int EmplId { get; set; }
    public string EmplCode { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Role { get; set; } = "User";

    /// <summary>
    /// Comma-separated module keys this user may open (see ModulePermissions).
    /// Ignored for the Admin role, which always holds every permission.
    /// </summary>
    public string? Permissions { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}

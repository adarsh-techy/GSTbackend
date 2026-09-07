namespace GSTAutoPilot.API.Configuration;

/// <summary>
/// Restricts which tenants this deployment will list or serve, so a single client
/// can be shown during a demo or handover without touching any tenant row.
///
/// Configured under "Tenants":
///   "Only"   — allowlist. When non-empty, nothing outside it is listed or served.
///   "Hidden" — denylist, used when "Only" is empty.
/// "Only" wins when both are set.
///
/// This is an access gate, not a data change: every tenant stays exactly as it is
/// in the master DB, and emptying the config restores all of them on restart.
/// </summary>
public sealed class TenantVisibility
{
    public TenantVisibility(IConfiguration configuration)
    {
        OnlyIds = ReadGuids(configuration, "Tenants:Only");
        HiddenIds = ReadGuids(configuration, "Tenants:Hidden");
    }

    /// <summary>Allowlisted tenant ids. Empty means "no allowlist configured".</summary>
    public IReadOnlyCollection<Guid> OnlyIds { get; }

    /// <summary>Denylisted tenant ids. Only consulted when <see cref="OnlyIds"/> is empty.</summary>
    public IReadOnlyCollection<Guid> HiddenIds { get; }

    /// <summary>True when this deployment is showing fewer tenants than it holds.</summary>
    public bool IsRestricted => OnlyIds.Count > 0 || HiddenIds.Count > 0;

    public bool IsVisible(Guid tenantId) =>
        OnlyIds.Count > 0 ? OnlyIds.Contains(tenantId) : !HiddenIds.Contains(tenantId);

    private static HashSet<Guid> ReadGuids(IConfiguration configuration, string key) =>
        (configuration.GetSection(key).Get<string[]>() ?? [])
            .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
}

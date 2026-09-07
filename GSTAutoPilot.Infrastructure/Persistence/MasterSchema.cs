using Microsoft.Data.SqlClient;

namespace GSTAutoPilot.Infrastructure.Persistence;

/// <summary>
/// Columns the master database may or may not have, probed once at startup so the
/// EF model can adapt to the deployed schema.
///
/// UserRoles.Permissions reached the code model ahead of the live database. EF put
/// the column into every SELECT against UserRoles, so every login failed with
/// "Invalid column name 'Permissions'" — the whole API was unreachable. The live
/// database is not ours to migrate, so the model bends to it instead.
/// </summary>
public static class MasterSchema
{
    /// <summary>
    /// True when master.UserRoles actually has a Permissions column. Defaults to true
    /// so a failed probe keeps the historical behaviour instead of silently widening
    /// what non-admin users can reach.
    /// </summary>
    public static bool HasUserRolePermissions { get; private set; } = true;

    /// <summary>
    /// Probes the deployed schema. Must run before the first MasterDbContext is
    /// resolved: EF compiles and caches the model on first use, so a later probe
    /// would not change the mapping.
    /// </summary>
    public static async Task<bool> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = 'UserRoles' AND COLUMN_NAME = 'Permissions')
            THEN 1 ELSE 0 END
            """;

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        HasUserRolePermissions = scalar is int flag && flag == 1;
        return HasUserRolePermissions;
    }
}

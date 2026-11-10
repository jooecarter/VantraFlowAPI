using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>
/// Flow's own workspaces and their members (owner | admin | member). A Flow workspace links to one Orbit and one
/// Projects workspace through its connections; who is in it is decided here, not copied from the apps.
/// </summary>
public sealed class WorkspacesRepository(SqlConnectionFactory factory)
{
    private const string WorkspaceCols = "w.Id, w.Name, w.TimeZone, w.OwnerVantraId, w.MonthlyRunQuota, w.CreatedAt";

    public async Task<IReadOnlyList<(WorkspaceRow Workspace, string Role)>> ForUserAsync(string vantraId)
    {
        using var conn = factory.Flow();
        var rows = await conn.QueryAsync<WorkspaceRow, string, (WorkspaceRow, string)>(@"
SELECT w.Id, w.Name, w.TimeZone, w.OwnerVantraId, w.MonthlyRunQuota, w.CreatedAt, m.Role
FROM flow.WorkspaceMembers m
JOIN flow.Workspaces w ON w.Id = m.WorkspaceId
WHERE m.VantraId = @VantraId
ORDER BY w.CreatedAt;", (w, role) => (w, role), new { VantraId = vantraId }, splitOn: "Role");
        return rows.ToList();
    }

    public async Task<WorkspaceRow?> GetAsync(Guid id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<WorkspaceRow>($"SELECT {WorkspaceCols} FROM flow.Workspaces w WHERE w.Id = @Id;", new { Id = id });
    }

    /// <summary>The person's role in the workspace, or null when they aren't in it.</summary>
    public async Task<string?> RoleAsync(Guid workspaceId, string vantraId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT Role FROM flow.WorkspaceMembers WHERE WorkspaceId = @WorkspaceId AND VantraId = @VantraId;",
            new { WorkspaceId = workspaceId, VantraId = vantraId });
    }

    public async Task<WorkspaceRow> CreateAsync(string name, string timeZone, string ownerVantraId, string? email, string? ownerName)
    {
        var id = Guid.NewGuid();
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
INSERT INTO flow.Workspaces (Id, Name, TimeZone, OwnerVantraId) VALUES (@Id, @Name, @TimeZone, @Owner);
INSERT INTO flow.WorkspaceMembers (WorkspaceId, VantraId, Role, Email, Name, AddedBy) VALUES (@Id, @Owner, N'owner', @Email, @OwnerName, @Owner);",
            new { Id = id, Name = name, TimeZone = timeZone, Owner = ownerVantraId, Email = email, OwnerName = ownerName });
        return (await GetAsync(id))!;
    }

    public async Task UpdateAsync(Guid id, string name, string timeZone)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Workspaces SET Name = @Name, TimeZone = @TimeZone, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;",
            new { Id = id, Name = name, TimeZone = timeZone });
    }

    public async Task<IReadOnlyList<MemberRow>> MembersAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<MemberRow>(@"
SELECT WorkspaceId, VantraId, Role, Email, Name, AddedAt FROM flow.WorkspaceMembers
WHERE WorkspaceId = @WorkspaceId ORDER BY CASE Role WHEN N'owner' THEN 0 WHEN N'admin' THEN 1 ELSE 2 END, Name;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    /// <summary>Adds (or re-roles) a member; false when they're the owner (whose role doesn't change here).</summary>
    public async Task<bool> UpsertMemberAsync(Guid workspaceId, string vantraId, string role, string? email, string? name, string by)
    {
        using var conn = factory.Flow();
        var n = await conn.ExecuteAsync(@"
UPDATE flow.WorkspaceMembers SET Role = @Role, Email = COALESCE(@Email, Email), Name = COALESCE(@Name, Name)
WHERE WorkspaceId = @WorkspaceId AND VantraId = @VantraId AND Role <> N'owner';
IF @@ROWCOUNT = 0 AND NOT EXISTS (SELECT 1 FROM flow.WorkspaceMembers WHERE WorkspaceId = @WorkspaceId AND VantraId = @VantraId)
    INSERT INTO flow.WorkspaceMembers (WorkspaceId, VantraId, Role, Email, Name, AddedBy) VALUES (@WorkspaceId, @VantraId, @Role, @Email, @Name, @By);",
            new { WorkspaceId = workspaceId, VantraId = vantraId, Role = role, Email = email, Name = name, By = by });
        return n > 0;
    }

    public async Task<bool> RemoveMemberAsync(Guid workspaceId, string vantraId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(
            "DELETE FROM flow.WorkspaceMembers WHERE WorkspaceId = @WorkspaceId AND VantraId = @VantraId AND Role <> N'owner';",
            new { WorkspaceId = workspaceId, VantraId = vantraId }) > 0;
    }

    /// <summary>This month's runs and today's emails (flow.Usage).</summary>
    public async Task<(int Runs, int Emails)> UsageAsync(Guid workspaceId, DateTime nowUtc)
    {
        using var conn = factory.Flow();
        var row = await conn.QuerySingleOrDefaultAsync<(int Runs, int Emails)>(@"
SELECT
    ISNULL((SELECT Runs FROM flow.Usage WHERE WorkspaceId = @WorkspaceId AND Period = @Month), 0) AS Runs,
    ISNULL((SELECT Emails FROM flow.Usage WHERE WorkspaceId = @WorkspaceId AND Period = @Day), 0) AS Emails;",
            new { WorkspaceId = workspaceId, Month = UsagePeriods.Month(nowUtc), Day = UsagePeriods.Day(nowUtc) });
        return row;
    }
}

public static class UsagePeriods
{
    public static string Month(DateTime utc) => utc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
    public static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

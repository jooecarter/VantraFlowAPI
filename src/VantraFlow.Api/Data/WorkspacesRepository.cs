using System.Data;
using Dapper;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Workspaces;

namespace VantraFlow.Api.Data;

/// <summary>What the workspace endpoints need of the database (<see cref="WorkspacesRepository"/>; a fake in tests).</summary>
public interface IWorkspaceStore
{
    Task<IReadOnlyList<(WorkspaceRow Workspace, string Role)>> ForUserAsync(string vantraId);
    Task<WorkspaceRow?> GetAsync(Guid id);
    /// <summary>The person's role in the workspace, or null when they aren't in it.</summary>
    Task<string?> RoleAsync(Guid workspaceId, string vantraId);
    /// <summary>A new workspace with its creator as owner; <paramref name="look"/> only once db/005 has run.</summary>
    Task<WorkspaceRow> CreateAsync(string name, string timeZone, string ownerVantraId, string? email, string? ownerName, WorkspaceLook? look = null);
    /// <summary>How many workspaces the person is the primary owner of.</summary>
    Task<int> OwnedCountAsync(string vantraId);
    Task UpdateAsync(Guid id, string name, string timeZone);
    /// <summary>db/005: set the colour and / or the logo (null clears).</summary>
    Task SetLookAsync(Guid id, bool setColor, string? color, bool setLogo, string? logo);
    /// <summary>db/005: the workspaces' looks.</summary>
    Task<IReadOnlyDictionary<Guid, WorkspaceLook>> LooksAsync(IReadOnlyCollection<Guid> ids);
    Task<IReadOnlyDictionary<Guid, int>> MemberCountsAsync(IReadOnlyCollection<Guid> ids);
    Task<IReadOnlyList<MemberRow>> MembersAsync(Guid workspaceId);
    /// <summary>Adds a member; false when they're already in it.</summary>
    Task<bool> AddMemberAsync(Guid workspaceId, string vantraId, string role, string? email, string? name, string by);
    /// <summary>
    /// A membership change decided by <paramref name="decide"/> (<see cref="WorkspaceRules"/>) on the members as locked,
    /// then applied in the same transaction. Null when there's no such workspace.
    /// </summary>
    Task<MemberPlan?> ApplyAsync(Guid workspaceId, Func<WorkspaceState, MemberPlan> decide);
    /// <summary>People added to the workspace by someone else since then (the daily invitation cap counts them).</summary>
    Task<int> AddedSinceAsync(Guid workspaceId, DateTime sinceUtc);
    Task<(int Runs, int Emails)> UsageAsync(Guid workspaceId, DateTime nowUtc);
    /// <summary>Switches every flow off (deleting: nothing new starts meanwhile).</summary>
    Task PauseAllAsync(Guid workspaceId, string reason);
    /// <summary>Deletes the workspace and everything in it. Safe to run again after a failure part way.</summary>
    Task DeleteAllAsync(Guid workspaceId);
}

/// <summary>
/// Flow's own workspaces and their members (owner | admin | member). A Flow workspace links to one Orbit and one
/// Projects workspace through its connections; who is in it is decided here, not copied from the apps.
/// </summary>
public sealed class WorkspacesRepository(SqlConnectionFactory factory) : IWorkspaceStore
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

    public async Task<string?> RoleAsync(Guid workspaceId, string vantraId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT Role FROM flow.WorkspaceMembers WHERE WorkspaceId = @WorkspaceId AND VantraId = @VantraId;",
            new { WorkspaceId = workspaceId, VantraId = vantraId });
    }

    public async Task<WorkspaceRow> CreateAsync(string name, string timeZone, string ownerVantraId, string? email, string? ownerName, WorkspaceLook? look = null)
    {
        var id = Guid.NewGuid();
        using var conn = factory.Flow();
        var insert = look is null
            ? "INSERT INTO flow.Workspaces (Id, Name, TimeZone, OwnerVantraId) VALUES (@Id, @Name, @TimeZone, @Owner);"
            : "INSERT INTO flow.Workspaces (Id, Name, TimeZone, OwnerVantraId, Color, Logo) VALUES (@Id, @Name, @TimeZone, @Owner, @Color, @Logo);";
        await conn.ExecuteAsync(insert + @"
INSERT INTO flow.WorkspaceMembers (WorkspaceId, VantraId, Role, Email, Name, AddedBy) VALUES (@Id, @Owner, N'owner', @Email, @OwnerName, @Owner);",
            new { Id = id, Name = name, TimeZone = timeZone, Owner = ownerVantraId, Email = email, OwnerName = ownerName, look?.Color, look?.Logo });
        return (await GetAsync(id))!;
    }

    public async Task<int> OwnedCountAsync(string vantraId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM flow.Workspaces WHERE OwnerVantraId = @VantraId;", new { VantraId = vantraId });
    }

    public async Task UpdateAsync(Guid id, string name, string timeZone)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Workspaces SET Name = @Name, TimeZone = @TimeZone, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;",
            new { Id = id, Name = name, TimeZone = timeZone });
    }

    public async Task SetLookAsync(Guid id, bool setColor, string? color, bool setLogo, string? logo)
    {
        if (!setColor && !setLogo) return;
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.Workspaces
   SET Color = CASE WHEN @SetColor = 1 THEN @Color ELSE Color END,
       Logo = CASE WHEN @SetLogo = 1 THEN @Logo ELSE Logo END,
       UpdatedAt = SYSUTCDATETIME()
 WHERE Id = @Id;",
            new { Id = id, SetColor = setColor, Color = color, SetLogo = setLogo, Logo = logo });
    }

    public async Task<IReadOnlyDictionary<Guid, WorkspaceLook>> LooksAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return new Dictionary<Guid, WorkspaceLook>();
        using var conn = factory.Flow();
        var rows = await conn.QueryAsync<WorkspaceLook>(
            "SELECT Id AS WorkspaceId, Color, Logo FROM flow.Workspaces WHERE Id IN @Ids;", new { Ids = ids.ToList() });
        return rows.ToDictionary(r => r.WorkspaceId);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> MemberCountsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return new Dictionary<Guid, int>();
        using var conn = factory.Flow();
        var rows = await conn.QueryAsync<(Guid WorkspaceId, int Members)>(
            "SELECT WorkspaceId, COUNT(*) AS Members FROM flow.WorkspaceMembers WHERE WorkspaceId IN @Ids GROUP BY WorkspaceId;", new { Ids = ids.ToList() });
        return rows.ToDictionary(r => r.WorkspaceId, r => r.Members);
    }

    public async Task<IReadOnlyList<MemberRow>> MembersAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<MemberRow>(@"
SELECT WorkspaceId, VantraId, Role, Email, Name, AddedAt FROM flow.WorkspaceMembers
WHERE WorkspaceId = @WorkspaceId ORDER BY CASE Role WHEN N'owner' THEN 0 WHEN N'admin' THEN 1 ELSE 2 END, Name;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<bool> AddMemberAsync(Guid workspaceId, string vantraId, string role, string? email, string? name, string by)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
INSERT INTO flow.WorkspaceMembers (WorkspaceId, VantraId, Role, Email, Name, AddedBy)
SELECT @WorkspaceId, @VantraId, @Role, @Email, @Name, @By
WHERE NOT EXISTS (SELECT 1 FROM flow.WorkspaceMembers WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @WorkspaceId AND VantraId = @VantraId);",
            new { WorkspaceId = workspaceId, VantraId = vantraId, Role = role, Email = email, Name = name, By = by }) > 0;
    }

    public async Task<MemberPlan?> ApplyAsync(Guid workspaceId, Func<WorkspaceState, MemberPlan> decide)
    {
        using var conn = factory.Flow();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        // The workspace row first (every membership change locks it), then its members.
        var primary = await conn.ExecuteScalarAsync<string?>(
            "SELECT OwnerVantraId FROM flow.Workspaces WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", new { Id = workspaceId }, tx);
        if (primary is null) { tx.Rollback(); return null; }
        var members = (await conn.QueryAsync<MemberState>(
            "SELECT VantraId, Role, AddedAt FROM flow.WorkspaceMembers WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @Id;", new { Id = workspaceId }, tx)).ToList();
        var plan = decide(new WorkspaceState(members, primary));
        if (!plan.Ok) { tx.Rollback(); return plan; }
        foreach (var c in plan.Changes)
            await conn.ExecuteAsync("UPDATE flow.WorkspaceMembers SET Role = @Role WHERE WorkspaceId = @Id AND VantraId = @VantraId;",
                new { Id = workspaceId, c.VantraId, c.Role }, tx);
        foreach (var r in plan.Removals)
            await conn.ExecuteAsync("DELETE FROM flow.WorkspaceMembers WHERE WorkspaceId = @Id AND VantraId = @VantraId;",
                new { Id = workspaceId, VantraId = r }, tx);
        if (!string.Equals(plan.PrimaryOwner, primary, StringComparison.Ordinal))
            await conn.ExecuteAsync("UPDATE flow.Workspaces SET OwnerVantraId = @Owner, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;",
                new { Id = workspaceId, Owner = plan.PrimaryOwner }, tx);
        tx.Commit();
        return plan;
    }

    public async Task<int> AddedSinceAsync(Guid workspaceId, DateTime sinceUtc)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM flow.WorkspaceMembers
WHERE WorkspaceId = @WorkspaceId AND AddedAt >= @Since AND AddedBy IS NOT NULL AND AddedBy <> VantraId;",
            new { WorkspaceId = workspaceId, Since = sinceUtc });
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

    public async Task PauseAllAsync(Guid workspaceId, string reason)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Flows SET Enabled = 0, PausedReason = @Reason WHERE WorkspaceId = @WorkspaceId AND Enabled = 1;",
            new { WorkspaceId = workspaceId, Reason = reason });
    }

    /// <summary>
    /// The run history goes first, in batches (it can be large); then, in one transaction, everything else — the
    /// flows, their versions, connections, subscriptions, outside-app tokens, poll cursors, invitations, usage — and
    /// the members and the workspace last. A failure part way leaves the workspace (and its owners) in place, so
    /// deleting again finishes the job. Tables from a later migration that hasn't run are skipped. (A step already
    /// running as it goes may still write its log line; the hourly prune takes that with the old runs.)
    /// </summary>
    public async Task DeleteAllAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
SET NOCOUNT ON;
DECLARE @n INT = 1;
WHILE @n > 0
BEGIN
    DELETE TOP (5000) s FROM flow.RunSteps s WHERE s.RunId IN (SELECT r.Id FROM flow.Runs r WHERE r.WorkspaceId = @WorkspaceId);
    SET @n = @@ROWCOUNT;
END;
SET @n = 1;
WHILE @n > 0
BEGIN
    DELETE TOP (5000) FROM flow.Runs WHERE WorkspaceId = @WorkspaceId;
    SET @n = @@ROWCOUNT;
END;
SET @n = 1;
WHILE @n > 0
BEGIN
    DELETE TOP (5000) FROM flow.Events WHERE WorkspaceId = @WorkspaceId;
    SET @n = @@ROWCOUNT;
END;
SET @n = 1;
WHILE @n > 0
BEGIN
    DELETE TOP (5000) FROM flow.FiredKeys WHERE WorkspaceId = @WorkspaceId;
    SET @n = @@ROWCOUNT;
END;",
            new { WorkspaceId = workspaceId }, commandTimeout: 600);

        await conn.ExecuteAsync(@"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRAN;
IF OBJECT_ID(N'flow.FlowVersions', N'U') IS NOT NULL DELETE FROM flow.FlowVersions WHERE WorkspaceId = @WorkspaceId;
IF OBJECT_ID(N'flow.PollCursors', N'U') IS NOT NULL DELETE FROM flow.PollCursors WHERE WorkspaceId = @WorkspaceId;
IF OBJECT_ID(N'flow.OAuthPending', N'U') IS NOT NULL DELETE FROM flow.OAuthPending WHERE WorkspaceId = @WorkspaceId;
IF OBJECT_ID(N'flow.OAuthConnections', N'U') IS NOT NULL DELETE FROM flow.OAuthConnections WHERE WorkspaceId = @WorkspaceId;
IF OBJECT_ID(N'flow.Invites', N'U') IS NOT NULL DELETE FROM flow.Invites WHERE WorkspaceId = @WorkspaceId;
DELETE s FROM flow.RunSteps s WHERE s.RunId IN (SELECT r.Id FROM flow.Runs r WHERE r.WorkspaceId = @WorkspaceId);
DELETE FROM flow.Runs WHERE WorkspaceId = @WorkspaceId;
DELETE FROM flow.Events WHERE WorkspaceId = @WorkspaceId;
DELETE FROM flow.FiredKeys WHERE WorkspaceId = @WorkspaceId;
DELETE FROM flow.Flows WHERE WorkspaceId = @WorkspaceId;
DELETE FROM flow.Subscriptions WHERE WorkspaceId = @WorkspaceId;
DELETE FROM flow.Connections WHERE WorkspaceId = @WorkspaceId;
DELETE FROM flow.Usage WHERE WorkspaceId = @WorkspaceId;
DELETE FROM flow.WorkspaceMembers WHERE WorkspaceId = @WorkspaceId;
DELETE FROM flow.Workspaces WHERE Id = @WorkspaceId;
COMMIT;",
            new { WorkspaceId = workspaceId }, commandTimeout: 120);
    }
}

public static class UsagePeriods
{
    public static string Month(DateTime utc) => utc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
    public static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

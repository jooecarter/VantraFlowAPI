using Dapper;

namespace VantraFlow.Api.Data;

/// <summary>A flow whose failures are due an email: who to tell, and since when.</summary>
public sealed record AlertDue(string FlowId, Guid WorkspaceId, string FlowName, string WorkspaceName, string Recipient, string? Email, string? Name, DateTime? AlertedAt);

/// <summary>One failed run for the digest.</summary>
public sealed record AlertFailure(string Id, DateTime StartedAt, string? Error);

/// <summary>A flow's alert setting: its own choice (null: the workspace's), who asked, and the workspace's default.</summary>
public sealed record FlowAlertSetting(bool? On, string? To, bool WorkspaceDefault, string CreatedBy);

/// <summary>
/// Failure alerts (db/002). Only called once <see cref="FlowSchema.AlertsReadyAsync"/> has said yes, so nothing here
/// runs against a database without the columns. A flow alerts when its own setting is on, or it has none and the
/// workspace's default is on; the email goes to whoever turned it on (<c>AlertTo</c>), else the flow's creator, while
/// they're still in the workspace. At most one email per flow per hour: <see cref="ClaimAsync"/> moves
/// <c>AlertedAt</c> only when the last one is an hour old, and the failures since then go in that one email.
/// </summary>
public sealed class AlertsRepository(SqlConnectionFactory factory) : IWorkspaceAlerts
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);
    /// <summary>How far back a first alert looks (so switching alerts on never mails old failures).</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(2);

    public const string DueSql = @"
SELECT TOP (@Take) f.Id AS FlowId, f.WorkspaceId, f.Name AS FlowName, w.Name AS WorkspaceName,
       ISNULL(f.AlertTo, f.CreatedBy) AS Recipient, m.Email, m.Name, f.AlertedAt
FROM flow.Flows f
JOIN flow.Workspaces w ON w.Id = f.WorkspaceId
JOIN flow.WorkspaceMembers m ON m.WorkspaceId = f.WorkspaceId AND m.VantraId = ISNULL(f.AlertTo, f.CreatedBy)
WHERE ISNULL(f.AlertOnFailure, w.AlertOnFailure) = 1
  AND (f.AlertedAt IS NULL OR f.AlertedAt <= @HourAgo)
  AND EXISTS (SELECT 1 FROM flow.Runs r
              WHERE r.FlowId = f.Id AND r.WorkspaceId = f.WorkspaceId AND r.Status = N'failed' AND r.Test = 0
                AND r.FinishedAt >= @Lookback AND (f.AlertedAt IS NULL OR r.FinishedAt > f.AlertedAt));";

    /// <summary>The flows with failures since their last alert, whose last alert is an hour old (or never sent).</summary>
    public async Task<IReadOnlyList<AlertDue>> DueAsync(DateTime now, int take = 50)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<AlertDue>(DueSql, new { Take = take, HourAgo = now - Every, Lookback = now - Lookback })).ToList();
    }

    /// <summary>The claim: true for exactly one caller when the flow's last alert is an hour old.</summary>
    public async Task<bool> ClaimAsync(Guid workspaceId, string flowId, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.Flows SET AlertedAt = @Now
WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND (AlertedAt IS NULL OR AlertedAt <= @HourAgo);",
            new { WorkspaceId = workspaceId, Id = flowId, Now = now, HourAgo = now - Every }) == 1;
    }

    /// <summary>The flow's runs that failed after <paramref name="since"/>, newest first (at most <paramref name="take"/>), and how many in all.</summary>
    public async Task<(IReadOnlyList<AlertFailure> Runs, int Count)> FailuresAsync(Guid workspaceId, string flowId, DateTime since, int take = 5)
    {
        using var conn = factory.Flow();
        using var multi = await conn.QueryMultipleAsync(@"
SELECT TOP (@Take) Id, StartedAt, Error FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId AND Status = N'failed' AND Test = 0 AND FinishedAt > @Since
ORDER BY FinishedAt DESC;
SELECT COUNT(*) FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId AND Status = N'failed' AND Test = 0 AND FinishedAt > @Since;",
            new { WorkspaceId = workspaceId, FlowId = flowId, Since = since, Take = take });
        var runs = (await multi.ReadAsync<AlertFailure>()).ToList();
        return (runs, await multi.ReadSingleAsync<int>());
    }

    public async Task<FlowAlertSetting?> SettingAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<FlowAlertSetting>(@"
SELECT f.AlertOnFailure AS [On], f.AlertTo AS [To], w.AlertOnFailure AS WorkspaceDefault, f.CreatedBy
FROM flow.Flows f JOIN flow.Workspaces w ON w.Id = f.WorkspaceId
WHERE f.WorkspaceId = @WorkspaceId AND f.Id = @Id;", new { WorkspaceId = workspaceId, Id = flowId });
    }

    /// <summary>A flow's own choice: on (to <paramref name="to"/>), off, or null (the workspace's default).</summary>
    public async Task SetFlowAsync(Guid workspaceId, string flowId, bool? on, string? to)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.Flows SET AlertOnFailure = @On, AlertTo = @To WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = flowId, On = on, To = on == true ? to : null });
    }

    public async Task<bool> WorkspaceDefaultAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<bool>("SELECT AlertOnFailure FROM flow.Workspaces WHERE Id = @Id;", new { Id = workspaceId });
    }

    public async Task SetWorkspaceDefaultAsync(Guid workspaceId, bool on)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Workspaces SET AlertOnFailure = @On, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;", new { Id = workspaceId, On = on });
    }
}

/// <summary>The workspace's failure-alert default (<see cref="AlertsRepository"/>; a fake in tests). Only once db/002 has run.</summary>
public interface IWorkspaceAlerts
{
    Task<bool> WorkspaceDefaultAsync(Guid workspaceId);
    Task SetWorkspaceDefaultAsync(Guid workspaceId, bool on);
}

using Dapper;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Data;

/// <summary>
/// Auto-pause and the daily failure digest (db/010). Only called once <see cref="FlowSchema.ReliabilityReadyAsync"/> has
/// said yes, so nothing here runs against a database without the columns.
/// </summary>
public sealed class ReliabilityRepository(SqlConnectionFactory factory, EngineRepository engine, FlowsRepository flows) : IReliabilityStore
{
    public async Task<IReadOnlyList<DigestWorkspace>> DigestCandidatesAsync(DateTime latestLocalDay)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<DigestWorkspace>(@"
SELECT Id, Name, TimeZone, CAST(DigestSentOn AS DATETIME2(0)) AS DigestSentOn FROM flow.Workspaces
WHERE FailureDigest = 1 AND (DigestSentOn IS NULL OR DigestSentOn < @Day);", new { Day = latestLocalDay.Date })).ToList();
    }

    public async Task<bool> ClaimDigestAsync(Guid workspaceId, DateTime localDay)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.Workspaces SET DigestSentOn = @Day
WHERE Id = @Id AND FailureDigest = 1 AND (DigestSentOn IS NULL OR DigestSentOn < @Day);", new { Id = workspaceId, Day = localDay.Date }) == 1;
    }

    public async Task<IReadOnlyList<DigestRun>> FailedRunsAsync(Guid workspaceId, DateTime since, int take)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<DigestRun>(@"
SELECT TOP (@Take) Id, FlowId, FlowName, Error, FinishedAt FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId AND Status = N'failed' AND Test = 0 AND FinishedAt >= @Since
ORDER BY FinishedAt DESC;", new { WorkspaceId = workspaceId, Since = since, Take = take })).ToList();
    }

    public async Task<IReadOnlyList<MailContact>> RecipientsAsync(Guid workspaceId, bool admins)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<MailContact>(@"
SELECT Email, Name FROM flow.WorkspaceMembers
WHERE WorkspaceId = @WorkspaceId AND (Role = N'owner' OR (@Admins = 1 AND Role = N'admin')) AND Email IS NOT NULL AND Email <> N'';",
            new { WorkspaceId = workspaceId, Admins = admins })).ToList();
    }

    public Task<bool> CountEmailAsync(Guid workspaceId, DateTime now, int cap) => engine.CountEmailAsync(workspaceId, now, cap);

    public async Task<AutoPauseFlow?> AutoPauseOfAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<AutoPauseFlow>(@"
SELECT f.Id AS FlowId, f.Name AS FlowName, w.Name AS WorkspaceName, f.Enabled, CAST(f.PauseAfterFailures AS INT) AS PauseAfterFailures, f.UpdatedAt
FROM flow.Flows f JOIN flow.Workspaces w ON w.Id = f.WorkspaceId
WHERE f.WorkspaceId = @WorkspaceId AND f.Id = @Id;", new { WorkspaceId = workspaceId, Id = flowId });
    }

    public async Task<IReadOnlyList<string>> LastStatusesAsync(Guid workspaceId, string flowId, DateTime since, int take)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<string>(@"
SELECT TOP (@Take) Status FROM flow.Runs
WHERE FlowId = @FlowId AND WorkspaceId = @WorkspaceId AND Test = 0 AND Status IN (N'succeeded', N'failed') AND StartedAt >= @Since
ORDER BY StartedAt DESC;", new { WorkspaceId = workspaceId, FlowId = flowId, Since = since, Take = take })).ToList();
    }

    public Task<bool> PauseAsync(Guid workspaceId, string flowId, string reason) => flows.PauseAsync(workspaceId, flowId, reason);

    // ── Settings ──

    public async Task<bool> DigestOnAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<bool>("SELECT FailureDigest FROM flow.Workspaces WHERE Id = @Id;", new { Id = workspaceId });
    }

    public async Task SetDigestAsync(Guid workspaceId, bool on)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Workspaces SET FailureDigest = @On, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;", new { Id = workspaceId, On = on });
    }

    public async Task<int?> PauseAfterAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int?>("SELECT CAST(PauseAfterFailures AS INT) FROM flow.Flows WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = flowId });
    }

    public async Task SetPauseAfterAsync(Guid workspaceId, string flowId, int? n)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Flows SET PauseAfterFailures = @N WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = flowId, N = n });
    }
}

using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>The flows a copy or an import reads and adds to (<see cref="FlowsRepository"/>; a fake in tests).</summary>
public interface IFlowStore
{
    Task<IReadOnlyList<FlowRow>> ListAsync(Guid workspaceId);
    Task<FlowRow?> GetAsync(Guid workspaceId, string id);
    Task InsertAsync(FlowRow f);
}

/// <summary>
/// Flows: the studio's list and editor, and what the engine looks up to start runs. Switching one on or off (or a pause)
/// tells outgoing webhooks (db/012, <see cref="Services.Developer.IDevEvents"/>) when it changed anything.
/// </summary>
public sealed class FlowsRepository(SqlConnectionFactory factory, Services.Developer.IDevEvents? events = null) : IFlowStore
{
    private const string FlowCols =
        "Id, WorkspaceId, Name, Description, Enabled, Definition, Version, TriggerApp, TriggerEvent, TriggerScope, HookId, HookSecretCipher, " +
        "TemplateId, PausedReason, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, LastRunAt, LastRunStatus";

    public async Task<IReadOnlyList<FlowRow>> ListAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowRow>($"SELECT {FlowCols} FROM flow.Flows WHERE WorkspaceId = @WorkspaceId ORDER BY UpdatedAt DESC;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    /// <summary>Each flow's runs and failures since <paramref name="since"/> (test runs left out).</summary>
    public async Task<IReadOnlyList<FlowStatsRow>> StatsAsync(Guid workspaceId, DateTime since)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowStatsRow>(@"
SELECT FlowId, COUNT(*) AS Runs, SUM(CASE WHEN Status = N'failed' THEN 1 ELSE 0 END) AS Failures
FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId AND StartedAt >= @Since AND Test = 0
GROUP BY FlowId;", new { WorkspaceId = workspaceId, Since = since })).ToList();
    }

    /// <summary>Each flow's runs and failures per day (UTC) since <paramref name="since"/>, test runs left out.</summary>
    public async Task<IReadOnlyList<FlowDayRow>> DailyAsync(Guid workspaceId, DateTime since)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowDayRow>(@"
SELECT FlowId, CAST(StartedAt AS date) AS [Day], COUNT(*) AS Runs, SUM(CASE WHEN Status = N'failed' THEN 1 ELSE 0 END) AS Failures
FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId AND StartedAt >= @Since AND Test = 0
GROUP BY FlowId, CAST(StartedAt AS date);", new { WorkspaceId = workspaceId, Since = since })).ToList();
    }

    public async Task<FlowRow?> GetAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<FlowRow>($"SELECT {FlowCols} FROM flow.Flows WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id });
    }

    public async Task<FlowRow?> ByHookAsync(string hookId)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<FlowRow>($"SELECT {FlowCols} FROM flow.Flows WHERE HookId = @HookId;", new { HookId = hookId });
    }

    public async Task InsertAsync(FlowRow f)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
INSERT INTO flow.Flows (Id, WorkspaceId, Name, Description, Enabled, Definition, Version, TriggerApp, TriggerEvent, TriggerScope, HookId, HookSecretCipher, TemplateId, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt)
VALUES (@Id, @WorkspaceId, @Name, @Description, 0, @Definition, 1, @TriggerApp, @TriggerEvent, @TriggerScope, @HookId, @HookSecretCipher, @TemplateId, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt);",
            new { f.Id, f.WorkspaceId, f.Name, f.Description, f.Definition, f.TriggerApp, f.TriggerEvent, f.TriggerScope, f.HookId, f.HookSecretCipher, f.TemplateId, f.CreatedBy, f.CreatedAt, f.UpdatedBy, f.UpdatedAt });
    }

    public async Task UpdateAsync(FlowRow f)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.Flows
SET Name = @Name, Description = @Description, Definition = @Definition, Version = Version + 1,
    TriggerApp = @TriggerApp, TriggerEvent = @TriggerEvent, TriggerScope = @TriggerScope, HookId = @HookId, HookSecretCipher = @HookSecretCipher,
    UpdatedBy = @UpdatedBy, UpdatedAt = @UpdatedAt
WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { f.Id, f.WorkspaceId, f.Name, f.Description, f.Definition, f.TriggerApp, f.TriggerEvent, f.TriggerScope, f.HookId, f.HookSecretCipher, f.UpdatedBy, f.UpdatedAt });
    }

    public async Task SetEnabledAsync(Guid workspaceId, string id, bool enabled, string by)
    {
        using var conn = factory.Flow();
        var was = await conn.ExecuteScalarAsync<bool?>(@"
UPDATE flow.Flows SET Enabled = @Enabled, PausedReason = NULL, UpdatedBy = @By, UpdatedAt = SYSUTCDATETIME()
OUTPUT DELETED.Enabled
WHERE WorkspaceId = @WorkspaceId AND Id = @Id;", new { WorkspaceId = workspaceId, Id = id, Enabled = enabled, By = by });
        if (was is { } before && before != enabled && events is not null) await events.FlowSwitchedAsync(workspaceId, id, enabled);
    }

    /// <summary>The loop breaker or a lost connection switched it off; true when it was on.</summary>
    public async Task<bool> PauseAsync(Guid workspaceId, string id, string reason)
    {
        using var conn = factory.Flow();
        var paused = await conn.ExecuteAsync(@"
UPDATE flow.Flows SET Enabled = 0, PausedReason = @Reason WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND Enabled = 1;",
            new { WorkspaceId = workspaceId, Id = id, Reason = reason.Length > 200 ? reason[..200] : reason }) > 0;
        if (paused && events is not null) await events.FlowSwitchedAsync(workspaceId, id, false);
        return paused;
    }

    public async Task DeleteAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
DELETE FROM flow.FiredKeys WHERE WorkspaceId = @WorkspaceId AND FlowId = @Id;
DELETE FROM flow.Flows WHERE WorkspaceId = @WorkspaceId AND Id = @Id;", new { WorkspaceId = workspaceId, Id = id });
    }

    /// <summary>The enabled flows an app event could start (the matcher decides).</summary>
    public async Task<IReadOnlyList<FlowRow>> ListeningAsync(Guid workspaceId, string app, string? scope)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowRow>($@"
SELECT {FlowCols} FROM flow.Flows
WHERE WorkspaceId = @WorkspaceId AND TriggerApp = @App AND Enabled = 1 AND (TriggerScope IS NULL OR TriggerScope = @Scope);",
            new { WorkspaceId = workspaceId, App = app, Scope = scope })).ToList();
    }

    /// <summary>Every enabled schedule flow, with its workspace's time zone (the minute tick).</summary>
    public async Task<IReadOnlyList<(FlowRow Flow, string TimeZone)>> SchedulesAsync()
    {
        using var conn = factory.Flow();
        var rows = await conn.QueryAsync<FlowRow, string, (FlowRow, string)>(@"
SELECT f.Id, f.WorkspaceId, f.Name, f.Description, f.Enabled, f.Definition, f.Version, f.TriggerApp, f.TriggerEvent, f.TriggerScope, f.HookId,
       f.HookSecretCipher, f.TemplateId, f.PausedReason, f.CreatedBy, f.CreatedAt, f.UpdatedBy, f.UpdatedAt, f.LastRunAt, f.LastRunStatus, w.TimeZone
FROM flow.Flows f
JOIN flow.Workspaces w ON w.Id = f.WorkspaceId
WHERE f.Enabled = 1 AND f.TriggerApp = N'schedule';", (f, tz) => (f, tz), splitOn: "TimeZone");
        return rows.ToList();
    }

    /// <summary>The scopes enabled flows listen to in an app (Orbit: table ids; Projects: <c>*</c> when any).</summary>
    public async Task<IReadOnlyList<string>> ScopesInUseAsync(Guid workspaceId, string app)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<string>(@"
SELECT DISTINCT ISNULL(TriggerScope, N'*') FROM flow.Flows WHERE WorkspaceId = @WorkspaceId AND TriggerApp = @App AND Enabled = 1;",
            new { WorkspaceId = workspaceId, App = app })).ToList();
    }

    /// <summary>The enabled flows that use an app anywhere (to pause when it's disconnected).</summary>
    public async Task<IReadOnlyList<string>> UsingAppAsync(Guid workspaceId, string app)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<string>(@"
SELECT Id FROM flow.Flows
WHERE WorkspaceId = @WorkspaceId AND Enabled = 1 AND (TriggerApp = @App OR Definition LIKE @Needle);",
            new { WorkspaceId = workspaceId, App = app, Needle = $"%\"app\":\"{app}\"%" })).ToList();
    }
}

using Dapper;
using VantraFlow.Api.Services.Insights;

namespace VantraFlow.Api.Data;

/// <summary>
/// Insights' reads, added up in SQL: each flow's runs per day from the runs still kept (<c>IX_Runs_Insights</c>, db/006;
/// <c>IX_Runs_Workspace</c> before) and, once db/006 has run, the daily totals the prune keeps after them
/// (<c>flow.RunDays</c>); how long runs took; the steps that failed (<c>IX_RunSteps_Failed</c>); and the time-saved
/// figures. Test runs are never counted.
/// </summary>
public sealed class InsightsRepository(SqlConnectionFactory factory)
{
    private const string LiveDays = @"
SELECT CAST(StartedAt AS date) AS [Day], FlowId, MAX(FlowName) AS FlowName,
       SUM(CASE WHEN Status = N'succeeded' THEN 1 ELSE 0 END) AS Succeeded,
       SUM(CASE WHEN Status = N'failed' THEN 1 ELSE 0 END) AS Failed,
       SUM(CASE WHEN Status IN (N'succeeded', N'failed') THEN 0 ELSE 1 END) AS Other,
       SUM(CASE WHEN Status = N'succeeded' THEN StepCount ELSE 0 END) AS Steps
FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId AND StartedAt >= @From AND Test = 0
GROUP BY CAST(StartedAt AS date), FlowId";

    /// <summary>Each flow's runs per day since <c>@From</c>, from the runs still kept.</summary>
    public const string DaysSql = LiveDays + ";";

    /// <summary>As <see cref="DaysSql"/>, with the daily totals kept after the runs were pruned (db/006): each run is in one or the other.</summary>
    public const string DaysKeptSql = @"
SELECT [Day], FlowId, MAX(FlowName) AS FlowName, SUM(Succeeded) AS Succeeded, SUM(Failed) AS Failed, SUM(Other) AS Other, SUM(Steps) AS Steps
FROM (" + LiveDays + @"
    UNION ALL
    SELECT [Day], FlowId, FlowName, Succeeded, Failed, Other, Steps
    FROM flow.RunDays
    WHERE WorkspaceId = @WorkspaceId AND [Day] >= CAST(@From AS date)
) AS d
GROUP BY [Day], FlowId;";

    public async Task<IReadOnlyList<InsightDayRow>> DaysAsync(Guid workspaceId, DateTime from, bool kept)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<InsightDayRow>(kept ? DaysKeptSql : DaysSql, new { WorkspaceId = workspaceId, From = from }, commandTimeout: 60)).ToList();
    }

    /// <summary>The median and 95th percentile of finished runs' durations: each flow's, then all of them (FlowId null).</summary>
    public const string DurationsSql = @"
SELECT DISTINCT FlowId,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY DurationMs) OVER (PARTITION BY FlowId) AS MedianMs,
       PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY DurationMs) OVER (PARTITION BY FlowId) AS P95Ms,
       COUNT(*) OVER (PARTITION BY FlowId) AS Measured
FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId AND StartedAt >= @From AND Test = 0 AND Status IN (N'succeeded', N'failed') AND DurationMs IS NOT NULL;
SELECT TOP (1) CAST(NULL AS NVARCHAR(40)) AS FlowId,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY DurationMs) OVER () AS MedianMs,
       PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY DurationMs) OVER () AS P95Ms,
       COUNT(*) OVER () AS Measured
FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId AND StartedAt >= @From AND Test = 0 AND Status IN (N'succeeded', N'failed') AND DurationMs IS NOT NULL;";

    public async Task<IReadOnlyList<InsightDurationRow>> DurationsAsync(Guid workspaceId, DateTime from)
    {
        using var conn = factory.Flow();
        using var grid = await conn.QueryMultipleAsync(DurationsSql, new { WorkspaceId = workspaceId, From = from }, commandTimeout: 60);
        var list = (await grid.ReadAsync<InsightDurationRow>()).ToList();
        list.AddRange(await grid.ReadAsync<InsightDurationRow>());
        return list;
    }

    /// <summary>
    /// The steps that failed most since <c>@From</c> (of one flow when <c>@FlowId</c> is set): each with its latest failure,
    /// then the most common errors of each (three at most).
    /// </summary>
    public const string FailingStepsSql = @"
WITH f AS (
    SELECT r.FlowId, s.StepId, s.Name, s.Type, s.App, s.StepAction, s.Error, s.RunId, s.StartedAt,
           ROW_NUMBER() OVER (PARTITION BY r.FlowId, s.StepId ORDER BY s.StartedAt DESC) AS Latest,
           COUNT(*) OVER (PARTITION BY r.FlowId, s.StepId) AS Failures
    FROM flow.RunSteps s
    JOIN flow.Runs r ON r.Id = s.RunId
    WHERE s.WorkspaceId = @WorkspaceId AND s.Status = N'failed' AND s.StartedAt >= @From AND r.Test = 0
      AND (@FlowId IS NULL OR r.FlowId = @FlowId)
)
SELECT TOP (@Take) FlowId, StepId, Name, Type, App, StepAction, Failures, StartedAt AS LastAt, RunId AS LastRunId, Error AS LastError
FROM f
WHERE Latest = 1
ORDER BY Failures DESC, StartedAt DESC;
WITH f AS (
    SELECT r.FlowId, s.StepId, LEFT(ISNULL(s.Error, N''), 300) AS Error
    FROM flow.RunSteps s
    JOIN flow.Runs r ON r.Id = s.RunId
    WHERE s.WorkspaceId = @WorkspaceId AND s.Status = N'failed' AND s.StartedAt >= @From AND r.Test = 0
      AND (@FlowId IS NULL OR r.FlowId = @FlowId)
),
e AS (
    SELECT FlowId, StepId, Error, COUNT(*) AS [Count],
           ROW_NUMBER() OVER (PARTITION BY FlowId, StepId ORDER BY COUNT(*) DESC) AS Rank
    FROM f
    WHERE Error <> N''
    GROUP BY FlowId, StepId, Error
)
SELECT FlowId, StepId, Error, [Count] FROM e WHERE Rank <= 3;";

    public async Task<(IReadOnlyList<InsightStepRow> Steps, IReadOnlyList<InsightErrorRow> Errors)> FailingStepsAsync(Guid workspaceId, DateTime from, string? flowId,
        int take = 8)
    {
        using var conn = factory.Flow();
        using var grid = await conn.QueryMultipleAsync(FailingStepsSql, new { WorkspaceId = workspaceId, From = from, FlowId = flowId, Take = take }, commandTimeout: 60);
        var steps = (await grid.ReadAsync<InsightStepRow>()).ToList();
        var keep = steps.Select(s => (s.FlowId, s.StepId)).ToHashSet();
        var errors = (await grid.ReadAsync<InsightErrorRow>()).Where(e => keep.Contains((e.FlowId, e.StepId))).ToList();
        return (steps, errors);
    }

    /// <summary>The workspace's flows, with their own minutes per run once db/006 has run.</summary>
    public async Task<IReadOnlyList<InsightFlowRow>> FlowsAsync(Guid workspaceId, bool kept)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<InsightFlowRow>(kept ? FlowsKeptSql : FlowsSql, new { WorkspaceId = workspaceId })).ToList();
    }

    public const string FlowsSql = @"
SELECT Id, Name, Enabled, TriggerApp, CAST(NULL AS DECIMAL(7, 2)) AS MinutesPerRun FROM flow.Flows WHERE WorkspaceId = @WorkspaceId;";

    public const string FlowsKeptSql = @"
SELECT Id, Name, Enabled, TriggerApp, MinutesPerRun FROM flow.Flows WHERE WorkspaceId = @WorkspaceId;";

    /// <summary>The workspace's minutes per step (db/006; null: the default).</summary>
    public async Task<decimal?> MinutesPerStepAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<decimal?>("SELECT MinutesPerStep FROM flow.Workspaces WHERE Id = @WorkspaceId;", new { WorkspaceId = workspaceId });
    }

    /// <summary>This month's runs, as counted against the quota.</summary>
    public async Task<int> RunsThisMonthAsync(Guid workspaceId, DateTime nowUtc)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int?>("SELECT Runs FROM flow.Usage WHERE WorkspaceId = @WorkspaceId AND Period = @Period;",
            new { WorkspaceId = workspaceId, Period = InsightsReport.UsageMonth(nowUtc) }) ?? 0;
    }

    /// <summary>Keeps the time-saved figures (db/006): the workspace's minutes per step, and the flows' own minutes per run given.</summary>
    public async Task SetTimeSavedAsync(Guid workspaceId, decimal? minutesPerStep, IReadOnlyList<(string FlowId, decimal? MinutesPerRun)> flows)
    {
        using var conn = factory.Flow();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("UPDATE flow.Workspaces SET MinutesPerStep = @Minutes WHERE Id = @WorkspaceId;",
            new { WorkspaceId = workspaceId, Minutes = minutesPerStep }, tx);
        foreach (var (flowId, minutes) in flows)
            await conn.ExecuteAsync("UPDATE flow.Flows SET MinutesPerRun = @Minutes WHERE WorkspaceId = @WorkspaceId AND Id = @FlowId;",
                new { WorkspaceId = workspaceId, FlowId = flowId, Minutes = minutes }, tx);
        tx.Commit();
    }
}

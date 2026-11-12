using Dapper;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Data;

/// <summary>
/// The engine's queue and run log (db/001), as Orbit's AutomationEngineRepository (db/055): events and runs are
/// claimed under a lease with UPDLOCK + READPAST, so any number of API instances can work through them, each row
/// by one at a time; a lost instance's lease simply runs out and another picks the row up.
/// </summary>
public sealed class EngineRepository(SqlConnectionFactory factory)
{
    private const string RunCols =
        "Seq, Id, WorkspaceId, FlowId, FlowName, FlowVersion, Definition, TriggerData, Context, Position, Attempt, Status, Test, ReplayOf, EventId, StepCount, " +
        "Error, StartedAt, FinishedAt, DurationMs, NotBefore";

    // ── Events ──

    public async Task EnqueueAsync(Guid workspaceId, string source, string kind, string? scope, string? flowId, string payload, bool flowCaused,
        DateTime at, string? replayOf = null, string? id = null)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
INSERT INTO flow.Events (Id, WorkspaceId, Source, Kind, Scope, FlowId, Payload, FlowCaused, ReplayOf, At, NotBefore)
VALUES (@Id, @WorkspaceId, @Source, @Kind, @Scope, @FlowId, @Payload, @FlowCaused, @ReplayOf, @At, @At);",
            new
            {
                Id = id ?? Services.Connectors.FlowSecrets.NewRowId("fev_"),
                WorkspaceId = workspaceId, Source = source, Kind = kind, Scope = scope, FlowId = flowId, Payload = payload,
                FlowCaused = flowCaused, ReplayOf = replayOf, At = at,
            });
    }

    public async Task<IReadOnlyList<FlowEventRow>> ClaimEventsAsync(string me, DateTime now, int take, TimeSpan lease)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowEventRow>(@"
WITH due AS (
    SELECT TOP (@Take) *
    FROM flow.Events WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE Status = N'queued' AND NotBefore <= @Now AND (LeaseUntil IS NULL OR LeaseUntil < @Now)
    ORDER BY NotBefore, Seq
)
UPDATE due SET LeaseUntil = @LeaseUntil, LeaseBy = @Me, Attempts = Attempts + 1
OUTPUT INSERTED.Seq, INSERTED.Id, INSERTED.WorkspaceId, INSERTED.Source, INSERTED.Kind, INSERTED.Scope, INSERTED.FlowId, INSERTED.Payload,
       INSERTED.FlowCaused, INSERTED.Test, INSERTED.ReplayOf, INSERTED.At, INSERTED.Attempts;",
            new { Take = take, Now = now, LeaseUntil = now.Add(lease), Me = me })).OrderBy(e => e.Seq).ToList();
    }

    public async Task CompleteEventAsync(long seq)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("DELETE FROM flow.Events WHERE Seq = @Seq;", new { Seq = seq });
    }

    public async Task ReleaseEventAsync(long seq, DateTime notBefore, string? error)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Events SET LeaseUntil = NULL, LeaseBy = NULL, NotBefore = @NotBefore, LastError = @Error WHERE Seq = @Seq;",
            new { Seq = seq, NotBefore = notBefore, Error = Clip(error) });
    }

    public async Task DeadEventAsync(long seq, string? error)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Events SET Status = N'dead', LeaseUntil = NULL, LeaseBy = NULL, LastError = @Error WHERE Seq = @Seq;",
            new { Seq = seq, Error = Clip(error) });
    }

    /// <summary>True for exactly one caller, ever (until pruned): an event for a flow, a schedule slot, an app delivery.</summary>
    public async Task<bool> ClaimKeyAsync(Guid workspaceId, string flowId, string key)
    {
        using var conn = factory.Flow();
        try
        {
            return await conn.ExecuteAsync(@"
INSERT INTO flow.FiredKeys (WorkspaceId, FlowId, FiredKey)
SELECT @WorkspaceId, @FlowId, @FiredKey
WHERE NOT EXISTS (SELECT 1 FROM flow.FiredKeys WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId AND FiredKey = @FiredKey);",
            new { WorkspaceId = workspaceId, FlowId = flowId, FiredKey = key.Length > 200 ? key[..200] : key }) > 0;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601)
        {
            return false; // another instance claimed it first
        }
    }

    // ── Quotas ──

    /// <summary>Counts one run against the month; false (nothing counted) when the workspace is at its quota.</summary>
    public async Task<bool> CountRunAsync(Guid workspaceId, DateTime now, int quota)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>(@"
MERGE flow.Usage WITH (HOLDLOCK) AS u
USING (SELECT @WorkspaceId AS WorkspaceId, @Period AS Period) AS s
ON u.WorkspaceId = s.WorkspaceId AND u.Period = s.Period
WHEN NOT MATCHED THEN INSERT (WorkspaceId, Period, Runs, Emails) VALUES (s.WorkspaceId, s.Period, 0, 0);
UPDATE flow.Usage SET Runs = Runs + 1 WHERE WorkspaceId = @WorkspaceId AND Period = @Period AND Runs < @Quota;
SELECT @@ROWCOUNT;", new { WorkspaceId = workspaceId, Period = UsagePeriods.Month(now), Quota = quota }) > 0;
    }

    /// <summary>Counts one email against today; false (nothing counted) at the daily cap.</summary>
    public async Task<bool> CountEmailAsync(Guid workspaceId, DateTime now, int cap)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>(@"
MERGE flow.Usage WITH (HOLDLOCK) AS u
USING (SELECT @WorkspaceId AS WorkspaceId, @Period AS Period) AS s
ON u.WorkspaceId = s.WorkspaceId AND u.Period = s.Period
WHEN NOT MATCHED THEN INSERT (WorkspaceId, Period, Runs, Emails) VALUES (s.WorkspaceId, s.Period, 0, 0);
UPDATE flow.Usage SET Emails = Emails + 1 WHERE WorkspaceId = @WorkspaceId AND Period = @Period AND Emails < @Cap;
SELECT @@ROWCOUNT;", new { WorkspaceId = workspaceId, Period = UsagePeriods.Day(now), Cap = cap }) > 0;
    }

    /// <summary>A workspace's runs started since <paramref name="since"/> (the per-minute rate), or one flow's (the loop breaker).</summary>
    public async Task<int> RunsSinceAsync(Guid workspaceId, string? flowId, DateTime since)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId AND StartedAt >= @Since AND Test = 0 AND (@FlowId IS NULL OR FlowId = @FlowId);",
            new { WorkspaceId = workspaceId, FlowId = flowId, Since = since });
    }

    // ── Runs ──

    public async Task InsertRunAsync(RunRow r)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
INSERT INTO flow.Runs (Id, WorkspaceId, FlowId, FlowName, FlowVersion, Definition, TriggerData, Context, Position, Attempt, Status, Test, ReplayOf, EventId, StepCount, Error, StartedAt, FinishedAt, DurationMs, NotBefore)
VALUES (@Id, @WorkspaceId, @FlowId, @FlowName, @FlowVersion, @Definition, @TriggerData, @Context, @Position, @Attempt, @Status, @Test, @ReplayOf, @EventId, @StepCount, @Error, @StartedAt, @FinishedAt, @DurationMs, @NotBefore);
IF @Finished = 1
    UPDATE flow.Flows SET LastRunAt = @StartedAt, LastRunStatus = @Status WHERE WorkspaceId = @WorkspaceId AND Id = @FlowId AND @Test = 0;",
            new
            {
                r.Id, r.WorkspaceId, r.FlowId, r.FlowName, r.FlowVersion, r.Definition, r.TriggerData, r.Context, r.Position, r.Attempt, r.Status,
                r.Test, r.ReplayOf, r.EventId, r.StepCount, Error = Clip(r.Error), r.StartedAt, r.FinishedAt, r.DurationMs, r.NotBefore,
                Finished = RunStatuses.Finished(r.Status),
            });
    }

    public async Task<IReadOnlyList<RunRow>> ClaimRunsAsync(string me, DateTime now, int take, TimeSpan lease)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<RunRow>(@"
WITH due AS (
    SELECT TOP (@Take) *
    FROM flow.Runs WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE Status IN (N'queued', N'running', N'waiting') AND NotBefore <= @Now AND (LeaseUntil IS NULL OR LeaseUntil < @Now)
    ORDER BY NotBefore, Seq
)
UPDATE due SET LeaseUntil = @LeaseUntil, LeaseBy = @Me, Status = N'running'
OUTPUT INSERTED.Seq, INSERTED.Id, INSERTED.WorkspaceId, INSERTED.FlowId, INSERTED.FlowName, INSERTED.FlowVersion, INSERTED.Definition,
       INSERTED.TriggerData, INSERTED.Context, INSERTED.Position, INSERTED.Attempt, INSERTED.Status, INSERTED.Test, INSERTED.ReplayOf,
       INSERTED.EventId, INSERTED.StepCount, INSERTED.Error, INSERTED.StartedAt, INSERTED.FinishedAt, INSERTED.DurationMs, INSERTED.NotBefore;",
            new { Take = take, Now = now, LeaseUntil = now.Add(lease), Me = me })).OrderBy(r => r.Seq).ToList();
    }

    /// <summary>Saves what a go at a run did: its log lines and where it is now, in one transaction.</summary>
    public async Task SaveSliceAsync(RunState s, IReadOnlyList<StepLog> logs, DateTime startedAt, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        var next = await conn.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(Seq), 0) FROM flow.RunSteps WITH (UPDLOCK, HOLDLOCK) WHERE RunId = @RunId;",
            new { RunId = s.Id }, tx);
        foreach (var l in logs)
            await conn.ExecuteAsync(@"
INSERT INTO flow.RunSteps (RunId, Seq, WorkspaceId, StepId, Type, App, StepAction, Name, Status, Attempt, Input, Output, Error, StartedAt, DurationMs)
VALUES (@RunId, @Seq, @WorkspaceId, @StepId, @Type, @App, @StepAction, @Name, @Status, @Attempt, @Input, @Output, @Error, @StartedAt, @DurationMs);",
                new
                {
                    RunId = s.Id, Seq = ++next, s.WorkspaceId, l.StepId, l.Type, l.App, StepAction = l.Action, Name = Clip(l.Name, 160), l.Status, l.Attempt,
                    l.Input, l.Output, Error = Clip(l.Error), l.StartedAt, l.DurationMs,
                }, tx);
        var finished = RunStatuses.Finished(s.Status);
        await conn.ExecuteAsync(@"
UPDATE flow.Runs
SET Status = @Status, Context = @Context, Position = @Position, Attempt = @Attempt, StepCount = @StepCount, Error = @Error,
    NotBefore = @NotBefore, LeaseUntil = NULL, LeaseBy = NULL,
    FinishedAt = CASE WHEN @Finished = 1 THEN @Now ELSE NULL END,
    DurationMs = CASE WHEN @Finished = 1 THEN @DurationMs ELSE NULL END
WHERE Id = @Id;
IF @Finished = 1
    UPDATE flow.Flows SET LastRunAt = @StartedAt, LastRunStatus = @Status WHERE WorkspaceId = @WorkspaceId AND Id = @FlowId AND @Test = 0;",
            new
            {
                s.Id, s.WorkspaceId, s.FlowId, s.Status, Context = s.Data.ToJsonString(), s.Position, s.Attempt, s.StepCount, Error = Clip(s.Error),
                s.NotBefore, Finished = finished, Now = now, StartedAt = startedAt, s.Test, DurationMs = DurationOf(startedAt, now),
            }, tx);
        tx.Commit();
    }

    public async Task<bool> CancelRunAsync(Guid workspaceId, string runId, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.Runs SET Status = N'cancelled', FinishedAt = @Now, LeaseUntil = NULL, LeaseBy = NULL, Error = N'Cancelled.'
WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND Status IN (N'queued', N'waiting');",
            new { WorkspaceId = workspaceId, Id = runId, Now = now }) > 0;
    }

    /// <summary>
    /// A workspace's runs, newest first: of one flow, in one status, started in [<paramref name="from"/>,
    /// <paramref name="to"/>) and before the cursor <paramref name="before"/>, and matching <paramref name="q"/> — a
    /// run's id (or the start of one) or any text in its trigger data (a record's name, an email, a webhook's value).
    /// </summary>
    public async Task<IReadOnlyList<RunRow>> RunsAsync(Guid workspaceId, string? flowId, string? status, int take, DateTime? before,
        DateTime? from = null, DateTime? to = null, string? q = null)
    {
        using var conn = factory.Flow();
        var term = string.IsNullOrWhiteSpace(q) ? null : Like(q.Trim());
        return (await conn.QueryAsync<RunRow>($@"
SELECT TOP (@Take) {RunCols} FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId
  AND (@FlowId IS NULL OR FlowId = @FlowId)
  AND (@Status IS NULL OR Status = @Status)
  AND (@Before IS NULL OR StartedAt < @Before)
  AND (@From IS NULL OR StartedAt >= @From)
  AND (@To IS NULL OR StartedAt < @To)
  AND (@Prefix IS NULL OR Id LIKE @Prefix ESCAPE N'\' OR TriggerData LIKE @Term ESCAPE N'\')
ORDER BY StartedAt DESC;",
            new
            {
                WorkspaceId = workspaceId, FlowId = flowId, Status = status, Take = take, Before = before, From = from, To = to,
                Prefix = term is null ? null : term + "%", Term = term is null ? null : "%" + term + "%",
            })).ToList();
    }

    /// <summary>Text for LIKE: its wildcards and the escape character taken literally.</summary>
    public static string Like(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    public async Task<RunRow?> RunAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<RunRow>($"SELECT {RunCols} FROM flow.Runs WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id });
    }

    public async Task<IReadOnlyList<RunStepRow>> StepsAsync(string runId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<RunStepRow>(@"
SELECT RunId, Seq, StepId, Type, App, StepAction, Name, Status, Attempt, Input, Output, Error, StartedAt, DurationMs
FROM flow.RunSteps WHERE RunId = @RunId ORDER BY Seq;", new { RunId = runId })).ToList();
    }

    // ── Housekeeping ──

    /// <summary>
    /// Old claims and dead events after a week; runs and their logs after <paramref name="keepRunsDays"/> days. With
    /// <paramref name="keepDays"/> (db/006 has run) the runs it deletes are added to their flow's day in
    /// <c>flow.RunDays</c> first, in the same transaction, so Insights still counts them (<see cref="PruneRunsKeepingDaysSql"/>).
    /// </summary>
    public async Task PruneAsync(DateTime now, int keepRunsDays, bool keepDays = false)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
DELETE TOP (5000) FROM flow.FiredKeys WHERE At < @Week;
DELETE TOP (5000) FROM flow.Events WHERE Status = N'dead' AND At < @Week;
DELETE TOP (5000) FROM flow.RunSteps WHERE StartedAt < @Runs;
DELETE TOP (5000) FROM flow.Usage WHERE LEN(Period) = 10 AND Period < @OldDay;",
            new { Week = now.AddDays(-7), Runs = now.AddDays(-keepRunsDays), OldDay = UsagePeriods.Day(now.AddDays(-40)) }, commandTimeout: 120);
        await conn.ExecuteAsync(keepDays ? PruneRunsKeepingDaysSql : PruneRunsSql, new { Runs = now.AddDays(-keepRunsDays) }, commandTimeout: 120);
    }

    public const string PruneRunsSql = @"
DELETE TOP (5000) FROM flow.Runs WHERE StartedAt < @Runs AND Status IN (N'succeeded', N'failed', N'skipped', N'cancelled');";

    /// <summary>The runs' prune once db/006 has run: what it deletes is added to each flow's day (test runs left out), at once.</summary>
    public const string PruneRunsKeepingDaysSql = @"
DECLARE @Gone TABLE (WorkspaceId UNIQUEIDENTIFIER NOT NULL, FlowId NVARCHAR(40) NOT NULL, FlowName NVARCHAR(160) NOT NULL, [Day] DATE NOT NULL,
                     Status NVARCHAR(12) NOT NULL, Test BIT NOT NULL, StepCount INT NOT NULL);
BEGIN TRY
BEGIN TRANSACTION;
DELETE TOP (5000) FROM flow.Runs
OUTPUT DELETED.WorkspaceId, DELETED.FlowId, DELETED.FlowName, CAST(DELETED.StartedAt AS date), DELETED.Status, DELETED.Test, DELETED.StepCount INTO @Gone
WHERE StartedAt < @Runs AND Status IN (N'succeeded', N'failed', N'skipped', N'cancelled');
MERGE flow.RunDays WITH (HOLDLOCK) AS d
USING (
    SELECT WorkspaceId, [Day], FlowId, MAX(FlowName) AS FlowName,
           SUM(CASE WHEN Status = N'succeeded' THEN 1 ELSE 0 END) AS Succeeded,
           SUM(CASE WHEN Status = N'failed' THEN 1 ELSE 0 END) AS Failed,
           SUM(CASE WHEN Status IN (N'succeeded', N'failed') THEN 0 ELSE 1 END) AS Other,
           SUM(CASE WHEN Status = N'succeeded' THEN StepCount ELSE 0 END) AS Steps
    FROM @Gone WHERE Test = 0
    GROUP BY WorkspaceId, [Day], FlowId
) AS g
ON d.WorkspaceId = g.WorkspaceId AND d.[Day] = g.[Day] AND d.FlowId = g.FlowId
WHEN MATCHED THEN UPDATE SET Succeeded = d.Succeeded + g.Succeeded, Failed = d.Failed + g.Failed, Other = d.Other + g.Other, Steps = d.Steps + g.Steps, FlowName = g.FlowName
WHEN NOT MATCHED THEN INSERT (WorkspaceId, [Day], FlowId, FlowName, Succeeded, Failed, Other, Steps)
    VALUES (g.WorkspaceId, g.[Day], g.FlowId, g.FlowName, g.Succeeded, g.Failed, g.Other, g.Steps);
COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;";

    /// <summary>
    /// A run's duration in milliseconds for its INT column, worked out here: a run with a long Delay, Wait until or Approval
    /// can last more than the 24.8 days an INT of milliseconds holds (SQL's DATEDIFF(MILLISECOND) overflows there), so
    /// it's held at the column's largest value instead of failing the save.
    /// </summary>
    public static int DurationOf(DateTime startedAt, DateTime now) =>
        (int)Math.Clamp(Math.Round((now - startedAt).TotalMilliseconds), 0, int.MaxValue);

    private static string? Clip(string? s, int max = 1000) => s is null ? null : s.Length <= max ? s : s[..max];
}

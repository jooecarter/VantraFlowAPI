using Dapper;

namespace VantraFlow.Api.Data;

/// <summary>A form trigger's address (db/014).</summary>
public sealed record FlowFormRow(
    string Id, Guid WorkspaceId, string FlowId, string Slug, string Token, string Title, string Access, bool Published, bool ShowInApps,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>A date flow's scan: where it's up to and when it looks next (db/014).</summary>
public sealed record DateCursorRow(string FlowId, Guid WorkspaceId, DateTime StartedAt, DateTime ScannedUntil, DateTime NextScanAt, int Failures);

/// <summary>
/// Forms, their submissions, and the date triggers' ledger and scans (db/014). <see cref="ReadyAsync"/> probes for the index
/// db/014 makes last; until then the new triggers stay hidden and refused, and nothing here is called.
/// </summary>
public sealed class TriggersRepository(SqlConnectionFactory factory)
{
    public const string ProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DateCursors_Due' AND object_id = OBJECT_ID(N'flow.DateCursors'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);
    private static volatile bool ready;
    private static DateTime checkedAt = DateTime.MinValue;

    /// <summary>Has db/014 run? A yes is kept for the life of the process, a no for <see cref="NegativeTtl"/>.</summary>
    public async Task<bool> ReadyAsync()
    {
        if (ready) return true;
        if (DateTime.UtcNow - checkedAt < NegativeTtl) return false;
        try
        {
            using var conn = factory.Flow();
            ready = await conn.ExecuteScalarAsync<bool>(ProbeSql);
        }
        catch (Microsoft.Data.SqlClient.SqlException) { ready = false; }
        checkedAt = DateTime.UtcNow;
        return ready;
    }

    private const string FormCols = "Id, WorkspaceId, FlowId, Slug, Token, Title, Access, Published, ShowInApps, CreatedAt, UpdatedAt";

    // ── Forms ──

    public async Task<FlowFormRow?> FormByTokenAsync(string token)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<FlowFormRow>($"SELECT {FormCols} FROM flow.FlowForms WHERE Token = @Token;", new { Token = token });
    }

    public async Task<FlowFormRow?> FormByFlowAsync(string flowId)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<FlowFormRow>($"SELECT {FormCols} FROM flow.FlowForms WHERE FlowId = @FlowId;", new { FlowId = flowId });
    }

    public async Task<IReadOnlyList<FlowFormRow>> FormsAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowFormRow>($"SELECT {FormCols} FROM flow.FlowForms WHERE WorkspaceId = @WorkspaceId;", new { WorkspaceId = workspaceId })).ToList();
    }

    /// <summary>Whether another flow's form in the workspace has this slug.</summary>
    public async Task<bool> SlugTakenAsync(Guid workspaceId, string slug, string flowId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM flow.FlowForms WHERE WorkspaceId = @WorkspaceId AND Slug = @Slug AND FlowId <> @FlowId;",
            new { WorkspaceId = workspaceId, Slug = slug, FlowId = flowId }) > 0;
    }

    /// <summary>Adds or updates a flow's form row (one per flow). False when the slug was taken in between (the unique index).</summary>
    public async Task<bool> SaveFormAsync(FlowFormRow f)
    {
        using var conn = factory.Flow();
        try
        {
            await conn.ExecuteAsync(@"
UPDATE flow.FlowForms SET Slug = @Slug, Title = @Title, Access = @Access, Published = @Published, ShowInApps = @ShowInApps, UpdatedAt = SYSUTCDATETIME()
WHERE FlowId = @FlowId;
IF @@ROWCOUNT = 0
    INSERT INTO flow.FlowForms (Id, WorkspaceId, FlowId, Slug, Token, Title, Access, Published, ShowInApps)
    VALUES (@Id, @WorkspaceId, @FlowId, @Slug, @Token, @Title, @Access, @Published, @ShowInApps);",
                new { f.Id, f.WorkspaceId, f.FlowId, f.Slug, f.Token, f.Title, f.Access, f.Published, f.ShowInApps });
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { return false; }
    }

    public async Task SetPublishedAsync(string flowId, bool published)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.FlowForms SET Published = @Published, UpdatedAt = SYSUTCDATETIME() WHERE FlowId = @FlowId;",
            new { FlowId = flowId, Published = published });
    }

    public async Task DeleteFormAsync(string flowId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("DELETE FROM flow.FlowForms WHERE FlowId = @FlowId;", new { FlowId = flowId });
    }

    /// <summary>Team forms shown under Requests: published, "Show it in Orbit and Projects" on, their flow on.</summary>
    public async Task<IReadOnlyList<FlowFormRow>> RequestFormsAsync(IReadOnlyCollection<Guid> workspaceIds)
    {
        if (workspaceIds.Count == 0) return [];
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowFormRow>(@"
SELECT TOP (100) ff.Id, ff.WorkspaceId, ff.FlowId, ff.Slug, ff.Token, ff.Title, ff.Access, ff.Published, ff.ShowInApps, ff.CreatedAt, ff.UpdatedAt
FROM flow.FlowForms ff
JOIN flow.Flows f ON f.Id = ff.FlowId AND f.WorkspaceId = ff.WorkspaceId
WHERE ff.WorkspaceId IN @Ids AND ff.Published = 1 AND ff.ShowInApps = 1 AND ff.Access = N'team' AND f.Enabled = 1
ORDER BY ff.Title;", new { Ids = workspaceIds.ToArray() })).ToList();
    }

    // ── Submissions ──

    /// <summary>Records a submission; false when this submission key was already sent for the form (a retry).</summary>
    public async Task<bool> InsertSubmissionAsync(string id, string formId, Guid workspaceId, string flowId, string key, string? ipHash, string? vantraId, DateTime at)
    {
        using var conn = factory.Flow();
        try
        {
            await conn.ExecuteAsync(@"
INSERT INTO flow.FormSubmissions (Id, FormId, WorkspaceId, FlowId, SubmissionKey, IpHash, VantraId, SubmittedAt)
VALUES (@Id, @FormId, @WorkspaceId, @FlowId, @Key, @IpHash, @VantraId, @At);",
                new { Id = id, FormId = formId, WorkspaceId = workspaceId, FlowId = flowId, Key = key, IpHash = ipHash, VantraId = vantraId, At = at });
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { return false; }
    }

    public async Task SubmissionRunAsync(string id, string? runId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.FormSubmissions SET RunId = @RunId WHERE Id = @Id;", new { Id = id, RunId = runId });
    }

    /// <summary>A form's submissions since a time, all of them and from one address.</summary>
    public async Task<(int All, int FromAddress)> SubmissionsSinceAsync(string formId, string? ipHash, DateTime since)
    {
        using var conn = factory.Flow();
        var row = await conn.QuerySingleAsync<(int, int)>(@"
SELECT COUNT(*), ISNULL(SUM(CASE WHEN IpHash = @IpHash THEN 1 ELSE 0 END), 0)
FROM flow.FormSubmissions WHERE FormId = @FormId AND SubmittedAt >= @Since;", new { FormId = formId, IpHash = ipHash, Since = since });
        return row;
    }

    // ── Date triggers ──

    /// <summary>Spends the ledger's key for one firing; false when it fired before.</summary>
    public async Task<bool> ClaimFiringAsync(Guid workspaceId, string flowId, string recordId, string dateValue, string offsetKey, DateTime fireAt)
    {
        using var conn = factory.Flow();
        try
        {
            return await conn.ExecuteAsync(@"
INSERT INTO flow.DateFirings (WorkspaceId, FlowId, RecordId, DateValue, OffsetKey, FireAt)
SELECT @WorkspaceId, @FlowId, @RecordId, @DateValue, @OffsetKey, @FireAt
WHERE NOT EXISTS (SELECT 1 FROM flow.DateFirings WITH (UPDLOCK, HOLDLOCK)
                  WHERE FlowId = @FlowId AND RecordId = @RecordId AND DateValue = @DateValue AND OffsetKey = @OffsetKey);",
                new
                {
                    WorkspaceId = workspaceId, FlowId = flowId, RecordId = Cut(recordId, 80), DateValue = Cut(dateValue, 40), OffsetKey = Cut(offsetKey, 40), FireAt = fireAt,
                }) > 0;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { return false; }
    }

    public async Task FiringRunAsync(string flowId, string recordId, string dateValue, string offsetKey, string runId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.DateFirings SET RunId = @RunId WHERE FlowId = @FlowId AND RecordId = @RecordId AND DateValue = @DateValue AND OffsetKey = @OffsetKey;",
            new { FlowId = flowId, RecordId = Cut(recordId, 80), DateValue = Cut(dateValue, 40), OffsetKey = Cut(offsetKey, 40), RunId = runId });
    }

    /// <summary>
    /// A cursor for each enabled date flow (a new one starts now: nothing older fires), and none for flows that aren't any
    /// more; then up to <paramref name="take"/> due ones claimed under a lease.
    /// </summary>
    public async Task<IReadOnlyList<DateCursorRow>> ClaimCursorsAsync(string me, DateTime now, int take, TimeSpan lease)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
INSERT INTO flow.DateCursors (FlowId, WorkspaceId, StartedAt, ScannedUntil, NextScanAt)
SELECT f.Id, f.WorkspaceId, @Now, @Now, @Now
FROM flow.Flows f
WHERE f.Enabled = 1 AND f.TriggerEvent IN (N'date.relative', N'task.overdue')
  AND NOT EXISTS (SELECT 1 FROM flow.DateCursors c WHERE c.FlowId = f.Id);
DELETE c FROM flow.DateCursors c
WHERE NOT EXISTS (SELECT 1 FROM flow.Flows f WHERE f.Id = c.FlowId AND f.Enabled = 1 AND f.TriggerEvent IN (N'date.relative', N'task.overdue'));",
            new { Now = now });
        return (await conn.QueryAsync<DateCursorRow>(@"
WITH due AS (
    SELECT TOP (@Take) *
    FROM flow.DateCursors WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE NextScanAt <= @Now AND (LeaseUntil IS NULL OR LeaseUntil < @Now)
    ORDER BY NextScanAt
)
UPDATE due SET LeaseUntil = @LeaseUntil, LeaseBy = @Me
OUTPUT INSERTED.FlowId, INSERTED.WorkspaceId, INSERTED.StartedAt, INSERTED.ScannedUntil, INSERTED.NextScanAt, INSERTED.Failures;",
            new { Take = take, Now = now, LeaseUntil = now.Add(lease), Me = me })).ToList();
    }

    /// <summary>A scan done: up to <paramref name="scannedUntil"/>, next at <paramref name="next"/>; or failed, with why (nothing moves on).</summary>
    public async Task FinishCursorAsync(string flowId, DateTime? scannedUntil, DateTime next, string? error)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.DateCursors
SET ScannedUntil = COALESCE(@ScannedUntil, ScannedUntil), NextScanAt = @Next, LeaseUntil = NULL, LeaseBy = NULL,
    Failures = CASE WHEN @Error IS NULL THEN 0 ELSE Failures + 1 END, LastError = @Error
WHERE FlowId = @FlowId;", new { FlowId = flowId, ScannedUntil = scannedUntil, Next = next, Error = error is null ? null : Cut(error, 400) });
    }

    /// <summary>Old submissions and firings: kept 90 days (a ledger key older than that can't come due again).</summary>
    public async Task PruneAsync(DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
DELETE TOP (5000) FROM flow.FormSubmissions WHERE SubmittedAt < @Old;
DELETE TOP (5000) FROM flow.DateFirings WHERE FiredAt < @Old AND FireAt < @Old;", new { Old = now.AddDays(-90) });
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}

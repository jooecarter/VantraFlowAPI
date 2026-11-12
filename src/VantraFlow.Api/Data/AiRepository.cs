using Dapper;
using VantraFlow.Api.Services.Insights;

namespace VantraFlow.Api.Data;

/// <summary>
/// A workspace's AI credits used this month: <c>flow.Usage.AiDrafts</c> on the month's row (db/006). One pool for the plan's AI credits:
/// the AI flow builder's drafts and AI steps (<see cref="Services.Ai.AiCredits"/>) both count here.
/// </summary>
public sealed class AiRepository(SqlConnectionFactory factory)
{
    public async Task<int> UsedAsync(Guid workspaceId, DateTime nowUtc)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int?>("SELECT AiDrafts FROM flow.Usage WHERE WorkspaceId = @WorkspaceId AND Period = @Period;",
            new { WorkspaceId = workspaceId, Period = InsightsReport.UsageMonth(nowUtc) }) ?? 0;
    }

    /// <summary>Counts one draft against the month: (true, the count after) — or (false, the count) at the cap, nothing counted.</summary>
    public async Task<(bool Ok, int Used)> TryCountAsync(Guid workspaceId, DateTime nowUtc, int cap)
    {
        using var conn = factory.Flow();
        var r = await conn.QuerySingleAsync<(int Ok, int Used)>(CountSql, new { WorkspaceId = workspaceId, Period = InsightsReport.UsageMonth(nowUtc), Cap = cap });
        return (r.Ok == 1, r.Used);
    }

    public const string CountSql = @"
MERGE flow.Usage WITH (HOLDLOCK) AS u
USING (SELECT @WorkspaceId AS WorkspaceId, @Period AS Period) AS s
ON u.WorkspaceId = s.WorkspaceId AND u.Period = s.Period
WHEN NOT MATCHED THEN INSERT (WorkspaceId, Period, Runs, Emails, AiDrafts) VALUES (s.WorkspaceId, s.Period, 0, 0, 0);
UPDATE flow.Usage SET AiDrafts = AiDrafts + 1 WHERE WorkspaceId = @WorkspaceId AND Period = @Period AND AiDrafts < @Cap;
DECLARE @Ok INT = @@ROWCOUNT;
SELECT @Ok AS Ok, (SELECT AiDrafts FROM flow.Usage WHERE WorkspaceId = @WorkspaceId AND Period = @Period) AS Used;";

    /// <summary>Gives a draft back (the call it paid for failed).</summary>
    public async Task RefundAsync(Guid workspaceId, DateTime nowUtc)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Usage SET AiDrafts = AiDrafts - 1 WHERE WorkspaceId = @WorkspaceId AND Period = @Period AND AiDrafts > 0;",
            new { WorkspaceId = workspaceId, Period = InsightsReport.UsageMonth(nowUtc) });
    }
}

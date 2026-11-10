using Dapper;

namespace VantraFlow.Api.Data;

/// <summary>
/// Has db/001 run (and db/002, <see cref="AlertsReadyAsync"/>)? Until it has, every endpoint but sign-in answers 503 <c>not_ready</c> and the worker waits —
/// so the API can be deployed before the migration. The probe looks for the index db/001 makes last
/// (<c>IX_Runs_Due</c>), by name in <c>sys.indexes</c> (never a constraint's definition). A positive answer is
/// remembered for the life of the process, a negative one for <see cref="NegativeTtl"/>.
/// </summary>
public sealed class FlowSchema(SqlConnectionFactory factory)
{
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    public const string ProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Runs_Due' AND object_id = OBJECT_ID(N'flow.Runs'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    /// <summary>db/002 (failure alerts): the column it adds last, by name in <c>sys.columns</c>.</summary>
    public const string AlertsProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'flow.Flows') AND name = N'AlertedAt')
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    private volatile bool ready;
    private DateTime checkedAt = DateTime.MinValue;
    private volatile bool alertsReady;
    private DateTime alertsCheckedAt = DateTime.MinValue;

    /// <summary>Has db/002 run? Until it has, failure alerts hide themselves (remembered as <see cref="ReadyAsync"/> is).</summary>
    public async Task<bool> AlertsReadyAsync()
    {
        if (alertsReady) return true;
        if (DateTime.UtcNow - alertsCheckedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        alertsReady = await conn.ExecuteScalarAsync<bool>(AlertsProbeSql);
        alertsCheckedAt = DateTime.UtcNow;
        return alertsReady;
    }

    public async Task<bool> ReadyAsync()
    {
        if (ready) return true;
        if (DateTime.UtcNow - checkedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        ready = await conn.ExecuteScalarAsync<bool>(ProbeSql);
        checkedAt = DateTime.UtcNow;
        return ready;
    }
}

using Dapper;

namespace VantraFlow.Api.Data;

/// <summary>
/// Has db/001 run? Until it has, every endpoint but sign-in answers 503 <c>not_ready</c> and the worker waits —
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

    private volatile bool ready;
    private DateTime checkedAt = DateTime.MinValue;

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

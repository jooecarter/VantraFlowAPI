using Dapper;

namespace VantraFlow.Api.Data;

/// <summary>A file a step made (db/015): what later steps of the same run attach.</summary>
public sealed class RunFileRow
{
    public string Id { get; set; } = "";
    public Guid WorkspaceId { get; set; }
    public string RunId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "";
    public int Size { get; set; }
    public byte[] Bytes { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// flow.RunFiles (db/015): files a step makes (a report's PDF) for the steps after it in the same run — at most
/// <see cref="MaxBytes"/> each, kept <see cref="Keep"/>, then pruned hourly. Only a step of the run that made a file
/// can read it (by workspace, id and run). Never logs a file's name or bytes. <see cref="ReadyAsync"/> probes for
/// the index the script makes last.
/// </summary>
public class RunFilesRepository(SqlConnectionFactory factory)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    /// <summary>All the files one email carries.</summary>
    public const int MaxEmailBytes = 15 * 1024 * 1024;
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    public const string ProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunFiles_Expires' AND object_id = OBJECT_ID(N'flow.RunFiles'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    public const string InsertSql = @"
INSERT INTO flow.RunFiles (Id, WorkspaceId, RunId, StepId, Name, ContentType, Size, Bytes, CreatedAt, ExpiresAt)
VALUES (@Id, @WorkspaceId, @RunId, @StepId, @Name, @ContentType, @Size, @Bytes, @Now, @ExpiresAt);";

    public const string GetSql = @"
SELECT Id, WorkspaceId, RunId, StepId, Name, ContentType, Size, Bytes, CreatedAt, ExpiresAt
FROM flow.RunFiles WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND RunId = @RunId AND ExpiresAt > @Now;";

    public const string PruneSql = "DELETE TOP (2000) FROM flow.RunFiles WHERE ExpiresAt < @Now;";

    private static volatile bool ready;
    private static DateTime checkedAt = DateTime.MinValue;

    public virtual async Task<bool> ReadyAsync()
    {
        if (ready) return true;
        if (DateTime.UtcNow - checkedAt < TimeSpan.FromSeconds(30)) return false;
        try
        {
            using var conn = factory.Flow();
            ready = await conn.ExecuteScalarAsync<bool>(ProbeSql);
        }
        catch (Microsoft.Data.SqlClient.SqlException) { ready = false; }
        checkedAt = DateTime.UtcNow;
        return ready;
    }

    public virtual async Task<string> AddAsync(Guid workspaceId, string runId, string stepId, string name, string contentType, byte[] bytes, DateTime now)
    {
        var id = "rf_" + Guid.NewGuid().ToString("N")[..24];
        using var conn = factory.Flow();
        await conn.ExecuteAsync(InsertSql, new { Id = id, WorkspaceId = workspaceId, RunId = runId, StepId = stepId, Name = name, ContentType = contentType,
            Size = bytes.Length, Bytes = bytes, Now = now, ExpiresAt = now + Keep });
        return id;
    }

    public virtual async Task<RunFileRow?> GetAsync(Guid workspaceId, string runId, string id, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<RunFileRow>(GetSql, new { WorkspaceId = workspaceId, Id = id, RunId = runId, Now = now });
    }

    public async Task<int> PruneAsync(DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(PruneSql, new { Now = now });
    }
}

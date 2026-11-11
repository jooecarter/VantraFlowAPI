using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>A flow's saved versions (db/003): one per save, the newest <see cref="Keep"/> kept.</summary>
public sealed class VersionsRepository(SqlConnectionFactory factory)
{
    public const int Keep = 100;

    /// <summary>Keeps a save (once per version number) and lets the oldest beyond <see cref="Keep"/> go.</summary>
    public async Task InsertAsync(FlowVersionRow v)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM flow.FlowVersions WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId AND Version = @Version)
    INSERT INTO flow.FlowVersions (WorkspaceId, FlowId, Version, Name, Definition, StepCount, Note, SavedBy, SavedByName, SavedAt)
    VALUES (@WorkspaceId, @FlowId, @Version, @Name, @Definition, @StepCount, @Note, @SavedBy, @SavedByName, @SavedAt);
DELETE FROM flow.FlowVersions WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId AND Version <= @Version - @Keep;",
            new { v.WorkspaceId, v.FlowId, v.Version, v.Name, v.Definition, v.StepCount, v.Note, v.SavedBy, v.SavedByName, v.SavedAt, Keep });
    }

    /// <summary>A flow's versions, newest first, without their definitions.</summary>
    public async Task<IReadOnlyList<FlowVersionRow>> ListAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowVersionRow>(@"
SELECT WorkspaceId, FlowId, Version, Name, N'' AS Definition, StepCount, Note, SavedBy, SavedByName, SavedAt
FROM flow.FlowVersions WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId ORDER BY Version DESC;",
            new { WorkspaceId = workspaceId, FlowId = flowId })).ToList();
    }

    public async Task<FlowVersionRow?> GetAsync(Guid workspaceId, string flowId, int version)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<FlowVersionRow>(@"
SELECT WorkspaceId, FlowId, Version, Name, Definition, StepCount, Note, SavedBy, SavedByName, SavedAt
FROM flow.FlowVersions WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId AND Version = @Version;",
            new { WorkspaceId = workspaceId, FlowId = flowId, Version = version });
    }

    public async Task DeleteAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("DELETE FROM flow.FlowVersions WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId;",
            new { WorkspaceId = workspaceId, FlowId = flowId });
    }
}

using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>Folders, tags, owners, comments, mention emails and the activity log (db/011; <see cref="TeamRepository"/>, a fake in tests).</summary>
public interface ITeamStore
{
    /// <summary>Has db/011 run? Until it has, everything here hides itself (503 <c>not_ready</c>).</summary>
    Task<bool> ReadyAsync();

    Task<IReadOnlyList<FolderRow>> FoldersAsync(Guid workspaceId);
    /// <summary>False when the workspace already has a folder of that name.</summary>
    Task<bool> InsertFolderAsync(FolderRow folder);
    /// <summary>False when another folder has that name.</summary>
    Task<bool> RenameFolderAsync(Guid workspaceId, string id, string name);
    /// <summary>Deletes the folder; its flows go back to the top level.</summary>
    Task DeleteFolderAsync(Guid workspaceId, string id);

    Task<IReadOnlyList<TagRow>> TagsAsync(Guid workspaceId);
    /// <summary>False when the workspace already has a tag of that name.</summary>
    Task<bool> InsertTagAsync(TagRow tag);
    /// <summary>False when another tag has that name.</summary>
    Task<bool> UpdateTagAsync(Guid workspaceId, string id, string name, string color);
    /// <summary>Deletes the tag and takes it off every flow.</summary>
    Task DeleteTagAsync(Guid workspaceId, string id);

    /// <summary>Every flow's folder, owner and comment count.</summary>
    Task<IReadOnlyList<FlowPlacementRow>> PlacementsAsync(Guid workspaceId);
    Task<FlowPlacementRow?> PlacementAsync(Guid workspaceId, string flowId);
    Task<IReadOnlyList<FlowTagRow>> FlowTagsAsync(Guid workspaceId);
    Task SetFolderAsync(Guid workspaceId, string flowId, string? folderId);
    Task SetOwnerAsync(Guid workspaceId, string flowId, string ownerId);
    Task SetTagsAsync(Guid workspaceId, string flowId, IReadOnlyList<string> tagIds);
    /// <summary>A deleted flow's tags, comments and mention notices.</summary>
    Task ForgetFlowAsync(Guid workspaceId, string flowId);

    Task<IReadOnlyList<CommentRow>> CommentsAsync(Guid workspaceId, string flowId);
    Task<CommentRow?> CommentAsync(Guid workspaceId, string id);
    Task InsertCommentAsync(CommentRow comment);
    Task UpdateCommentAsync(Guid workspaceId, string id, string body, DateTime editedAt);
    Task DeleteCommentAsync(Guid workspaceId, string id);
    /// <summary>
    /// May this person be emailed about a mention on this flow now (none in the last hour)? Claims it if so, so two
    /// comments at once send one email.
    /// </summary>
    Task<bool> ClaimMentionAsync(Guid workspaceId, string flowId, string vantraId, DateTime now);

    Task InsertActivityAsync(ActivityRow row);
    Task<IReadOnlyList<ActivityRow>> ActivityAsync(Guid workspaceId, ActivityQuery query);

    /// <summary>The workspace's secrets by name (db/007; none before).</summary>
    Task<IReadOnlyList<SecretNameRow>> SecretNamesAsync(Guid workspaceId);
    /// <summary>The apps the workspace has connected, each with its connection's name (never a token).</summary>
    Task<IReadOnlyList<ConnectedAppRow>> ConnectedAppsAsync(Guid workspaceId);
}

public sealed class TeamRepository(SqlConnectionFactory factory) : ITeamStore
{
    /// <summary>db/011: the index it makes last, by name in <c>sys.indexes</c>.</summary>
    public const string ProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Activity_Flow' AND object_id = OBJECT_ID(N'flow.Activity'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    // A positive answer is kept for the process; a negative one for FlowSchema.NegativeTtl (as FlowSchema's probes).
    private static volatile bool ready;
    private static DateTime checkedAt = DateTime.MinValue;

    public async Task<bool> ReadyAsync()
    {
        if (ready) return true;
        if (DateTime.UtcNow - checkedAt < FlowSchema.NegativeTtl) return false;
        using var conn = factory.Flow();
        ready = await conn.ExecuteScalarAsync<bool>(ProbeSql);
        checkedAt = DateTime.UtcNow;
        return ready;
    }

    private static bool IsDuplicate(Microsoft.Data.SqlClient.SqlException e) => e.Number is 2601 or 2627;

    // ── Folders ──

    public async Task<IReadOnlyList<FolderRow>> FoldersAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FolderRow>(
            "SELECT Id, WorkspaceId, Name, CreatedBy, CreatedAt FROM flow.Folders WHERE WorkspaceId = @WorkspaceId ORDER BY Name;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<bool> InsertFolderAsync(FolderRow f)
    {
        using var conn = factory.Flow();
        try
        {
            await conn.ExecuteAsync(@"
INSERT INTO flow.Folders (Id, WorkspaceId, Name, CreatedBy, CreatedAt, UpdatedAt) VALUES (@Id, @WorkspaceId, @Name, @CreatedBy, @CreatedAt, @CreatedAt);",
                new { f.Id, f.WorkspaceId, f.Name, f.CreatedBy, f.CreatedAt });
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (IsDuplicate(e)) { return false; }
    }

    public async Task<bool> RenameFolderAsync(Guid workspaceId, string id, string name)
    {
        using var conn = factory.Flow();
        try
        {
            await conn.ExecuteAsync("UPDATE flow.Folders SET Name = @Name, UpdatedAt = SYSUTCDATETIME() WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
                new { WorkspaceId = workspaceId, Id = id, Name = name });
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (IsDuplicate(e)) { return false; }
    }

    public async Task DeleteFolderAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
SET XACT_ABORT ON;
BEGIN TRAN;
UPDATE flow.Flows SET FolderId = NULL WHERE WorkspaceId = @WorkspaceId AND FolderId = @Id;
DELETE FROM flow.Folders WHERE WorkspaceId = @WorkspaceId AND Id = @Id;
COMMIT;", new { WorkspaceId = workspaceId, Id = id });
    }

    // ── Tags ──

    public async Task<IReadOnlyList<TagRow>> TagsAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<TagRow>(
            "SELECT Id, WorkspaceId, Name, Color, CreatedBy, CreatedAt FROM flow.Tags WHERE WorkspaceId = @WorkspaceId ORDER BY Name;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<bool> InsertTagAsync(TagRow t)
    {
        using var conn = factory.Flow();
        try
        {
            await conn.ExecuteAsync(@"
INSERT INTO flow.Tags (Id, WorkspaceId, Name, Color, CreatedBy, CreatedAt) VALUES (@Id, @WorkspaceId, @Name, @Color, @CreatedBy, @CreatedAt);",
                new { t.Id, t.WorkspaceId, t.Name, t.Color, t.CreatedBy, t.CreatedAt });
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (IsDuplicate(e)) { return false; }
    }

    public async Task<bool> UpdateTagAsync(Guid workspaceId, string id, string name, string color)
    {
        using var conn = factory.Flow();
        try
        {
            await conn.ExecuteAsync("UPDATE flow.Tags SET Name = @Name, Color = @Color WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
                new { WorkspaceId = workspaceId, Id = id, Name = name, Color = color });
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (IsDuplicate(e)) { return false; }
    }

    public async Task DeleteTagAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
SET XACT_ABORT ON;
BEGIN TRAN;
DELETE FROM flow.FlowTags WHERE WorkspaceId = @WorkspaceId AND TagId = @Id;
DELETE FROM flow.Tags WHERE WorkspaceId = @WorkspaceId AND Id = @Id;
COMMIT;", new { WorkspaceId = workspaceId, Id = id });
    }

    // ── Where flows are ──

    private const string PlacementSql = @"
SELECT f.Id, f.Name, f.FolderId, f.OwnerId, f.CreatedBy,
       (SELECT COUNT(*) FROM flow.FlowComments c WHERE c.WorkspaceId = f.WorkspaceId AND c.FlowId = f.Id) AS Comments
FROM flow.Flows f
WHERE f.WorkspaceId = @WorkspaceId";

    public async Task<IReadOnlyList<FlowPlacementRow>> PlacementsAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowPlacementRow>(PlacementSql + ";", new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<FlowPlacementRow?> PlacementAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<FlowPlacementRow>(PlacementSql + " AND f.Id = @FlowId;", new { WorkspaceId = workspaceId, FlowId = flowId });
    }

    public async Task<IReadOnlyList<FlowTagRow>> FlowTagsAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<FlowTagRow>("SELECT FlowId, TagId FROM flow.FlowTags WHERE WorkspaceId = @WorkspaceId;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task SetFolderAsync(Guid workspaceId, string flowId, string? folderId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Flows SET FolderId = @FolderId WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = flowId, FolderId = folderId });
    }

    public async Task SetOwnerAsync(Guid workspaceId, string flowId, string ownerId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Flows SET OwnerId = @OwnerId WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = flowId, OwnerId = ownerId });
    }

    public async Task SetTagsAsync(Guid workspaceId, string flowId, IReadOnlyList<string> tagIds)
    {
        using var conn = factory.Flow();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("DELETE FROM flow.FlowTags WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId;",
            new { WorkspaceId = workspaceId, FlowId = flowId }, tx);
        foreach (var tag in tagIds)
            await conn.ExecuteAsync(@"
INSERT INTO flow.FlowTags (WorkspaceId, FlowId, TagId)
SELECT @WorkspaceId, @FlowId, t.Id FROM flow.Tags t WHERE t.WorkspaceId = @WorkspaceId AND t.Id = @TagId;",
                new { WorkspaceId = workspaceId, FlowId = flowId, TagId = tag }, tx);
        tx.Commit();
    }

    public async Task ForgetFlowAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
DELETE FROM flow.FlowTags WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId;
DELETE FROM flow.FlowComments WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId;
DELETE FROM flow.MentionNotices WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId;", new { WorkspaceId = workspaceId, FlowId = flowId });
    }

    // ── Comments ──

    private const string CommentCols = "Id, WorkspaceId, FlowId, AuthorId, AuthorName, Body, CreatedAt, EditedAt";

    public async Task<IReadOnlyList<CommentRow>> CommentsAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<CommentRow>($@"
SELECT TOP (500) {CommentCols} FROM flow.FlowComments WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId ORDER BY CreatedAt, Id;",
            new { WorkspaceId = workspaceId, FlowId = flowId })).ToList();
    }

    public async Task<CommentRow?> CommentAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<CommentRow>($"SELECT {CommentCols} FROM flow.FlowComments WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id });
    }

    public async Task InsertCommentAsync(CommentRow c)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
INSERT INTO flow.FlowComments (Id, WorkspaceId, FlowId, AuthorId, AuthorName, Body, CreatedAt)
VALUES (@Id, @WorkspaceId, @FlowId, @AuthorId, @AuthorName, @Body, @CreatedAt);",
            new { c.Id, c.WorkspaceId, c.FlowId, c.AuthorId, c.AuthorName, c.Body, c.CreatedAt });
    }

    public async Task UpdateCommentAsync(Guid workspaceId, string id, string body, DateTime editedAt)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.FlowComments SET Body = @Body, EditedAt = @EditedAt WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id, Body = body, EditedAt = editedAt });
    }

    public async Task DeleteCommentAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("DELETE FROM flow.FlowComments WHERE WorkspaceId = @WorkspaceId AND Id = @Id;", new { WorkspaceId = workspaceId, Id = id });
    }

    public async Task<bool> ClaimMentionAsync(Guid workspaceId, string flowId, string vantraId, DateTime now)
    {
        using var conn = factory.Flow();
        try
        {
            return await conn.ExecuteScalarAsync<bool>(@"
SET NOCOUNT ON;
DECLARE @claimed BIT = 0;
UPDATE flow.MentionNotices SET SentAt = @Now
WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId AND VantraId = @VantraId AND SentAt <= @Cutoff;
IF @@ROWCOUNT > 0 SET @claimed = 1;
ELSE IF NOT EXISTS (SELECT 1 FROM flow.MentionNotices WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId AND VantraId = @VantraId)
BEGIN
    INSERT INTO flow.MentionNotices (WorkspaceId, FlowId, VantraId, SentAt) VALUES (@WorkspaceId, @FlowId, @VantraId, @Now);
    SET @claimed = 1;
END;
SELECT @claimed;", new { WorkspaceId = workspaceId, FlowId = flowId, VantraId = vantraId, Now = now, Cutoff = now - Services.Team.TeamRules.MentionEvery });
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (IsDuplicate(e)) { return false; }
    }

    // ── The activity log ──

    public async Task InsertActivityAsync(ActivityRow a)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
INSERT INTO flow.Activity (WorkspaceId, At, ActorId, ActorName, Kind, FlowId, FlowName, Version, Detail)
VALUES (@WorkspaceId, @At, @ActorId, @ActorName, @Kind, @FlowId, @FlowName, @Version, @Detail);",
            new { a.WorkspaceId, a.At, a.ActorId, a.ActorName, a.Kind, a.FlowId, a.FlowName, a.Version, a.Detail });
    }

    public async Task<IReadOnlyList<ActivityRow>> ActivityAsync(Guid workspaceId, ActivityQuery q)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<ActivityRow>(@"
SELECT TOP (@Limit) Id, WorkspaceId, At, ActorId, ActorName, Kind, FlowId, FlowName, Version, Detail
FROM flow.Activity
WHERE WorkspaceId = @WorkspaceId
  AND (@ActorId IS NULL OR ActorId = @ActorId)
  AND (@FlowId IS NULL OR FlowId = @FlowId)
  AND (@Kind IS NULL OR Kind LIKE @Kind + N'%')
  AND (@Before IS NULL OR Id < @Before)
ORDER BY Id DESC;",
            new { WorkspaceId = workspaceId, q.ActorId, q.FlowId, Kind = q.KindPrefix, q.Before, Limit = Math.Clamp(q.Limit, 1, 101) })).ToList();
    }

    // ── What an import or a copy needs ──

    public async Task<IReadOnlyList<SecretNameRow>> SecretNamesAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<SecretNameRow>(@"
IF OBJECT_ID(N'flow.Secrets', N'U') IS NOT NULL
    SELECT Id, Name FROM flow.Secrets WHERE WorkspaceId = @WorkspaceId ORDER BY Name;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<IReadOnlyList<ConnectedAppRow>> ConnectedAppsAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        var apps = (await conn.QueryAsync<ConnectedAppRow>(@"
SELECT App, RemoteWorkspaceName AS Name FROM flow.Connections WHERE WorkspaceId = @WorkspaceId AND RevokedAt IS NULL;",
            new { WorkspaceId = workspaceId })).ToList();
        apps.AddRange(await conn.QueryAsync<ConnectedAppRow>(@"
IF OBJECT_ID(N'flow.OAuthConnections', N'U') IS NOT NULL
    SELECT Provider AS App, AccountName AS Name FROM flow.OAuthConnections WHERE WorkspaceId = @WorkspaceId AND RevokedAt IS NULL;", new { WorkspaceId = workspaceId }));
        return apps;
    }
}

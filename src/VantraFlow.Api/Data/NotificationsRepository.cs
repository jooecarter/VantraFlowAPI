using Dapper;
using VantraFlow.Api.Services.Notifications;

namespace VantraFlow.Api.Data;

/// <summary>
/// Notifications and per-person settings (db/016). <see cref="ReadyAsync"/> first: nothing here runs against a database
/// without the tables. A grouped note (<see cref="NotificationDraft.Group"/>) is counted up under a range lock, so two
/// failures at once still make one note.
/// </summary>
public sealed class NotificationsRepository(SqlConnectionFactory factory, FlowSchema schema) : INotificationStore, Controllers.IJobTitleStore
{
    public Task<bool> ReadyAsync() => schema.NotificationsReadyAsync();

    public const string AddSql = @"
SET XACT_ABORT ON;
BEGIN TRAN;
DECLARE @Id BIGINT = NULL;
IF @Since IS NOT NULL
    SELECT TOP (1) @Id = Id FROM flow.Notifications WITH (UPDLOCK, HOLDLOCK)
    WHERE Recipient = @Recipient AND WorkspaceId = @WorkspaceId AND Kind = @Kind AND RefKey = @RefKey AND GroupedAt >= @Since
    ORDER BY GroupedAt DESC;
IF @Id IS NOT NULL
    UPDATE flow.Notifications
    SET Times = Times + 1, Lead = @Lead, Title = @Title,
        Body = CASE WHEN @BodyMany IS NULL THEN @Body ELSE REPLACE(@BodyMany, N'{n}', CAST(Times + 1 AS NVARCHAR(12))) END,
        Link = @Link, CreatedAt = @Now, ReadAt = NULL
    WHERE Id = @Id;
ELSE
    INSERT INTO flow.Notifications (WorkspaceId, Recipient, Kind, RefKey, Lead, Title, Body, Link, Times, GroupedAt, CreatedAt)
    VALUES (@WorkspaceId, @Recipient, @Kind, @RefKey, @Lead, @Title, @Body, @Link, 1, @Now, @Now);
COMMIT;
SELECT CASE WHEN @Id IS NULL THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    public async Task<bool> AddAsync(NotificationDraft d, string recipient, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<bool>(AddSql, new
        {
            d.WorkspaceId, Recipient = recipient, d.Kind, d.RefKey, d.Lead, d.Title, d.Body, d.BodyMany, d.Link, Now = now,
            Since = d.Group is { } g && d.RefKey is not null ? now - g : (DateTime?)null,
        });
    }

    private const string Columns = "Id, WorkspaceId, Recipient, Kind, RefKey, Lead, Title, Body, Link, Times, GroupedAt, CreatedAt, ReadAt";

    public async Task<IReadOnlyList<NotificationRow>> ListAsync(Guid workspaceId, string recipient, long? before, int take, bool unreadOnly, IReadOnlyCollection<string> hidden)
    {
        using var conn = factory.Flow();
        // Paged by id (CreatedAt moves when a group counts up, so the cursor is the id, newest first by time).
        return (await conn.QueryAsync<NotificationRow>($@"
SELECT TOP (@Take) {Columns} FROM flow.Notifications
WHERE Recipient = @Recipient AND WorkspaceId = @WorkspaceId
  AND (@Unread = 0 OR ReadAt IS NULL)
  AND Kind NOT IN @Hidden
  AND (@Before IS NULL OR CreatedAt < (SELECT b.CreatedAt FROM flow.Notifications b WHERE b.Id = @Before)
       OR (CreatedAt = (SELECT b.CreatedAt FROM flow.Notifications b WHERE b.Id = @Before) AND Id < @Before))
ORDER BY CreatedAt DESC, Id DESC;",
            new { WorkspaceId = workspaceId, Recipient = recipient, Take = take, Unread = unreadOnly, Before = before, Hidden = Hide(hidden) })).ToList();
    }

    public async Task<int> UnreadAsync(Guid workspaceId, string recipient, IReadOnlyCollection<string> hidden)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM flow.Notifications
WHERE Recipient = @Recipient AND WorkspaceId = @WorkspaceId AND ReadAt IS NULL AND Kind NOT IN @Hidden;",
            new { WorkspaceId = workspaceId, Recipient = recipient, Hidden = Hide(hidden) });
    }

    public async Task<bool> MarkAsync(Guid workspaceId, string recipient, long id, bool read, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.Notifications SET ReadAt = CASE WHEN @Read = 1 THEN ISNULL(ReadAt, @Now) ELSE NULL END
WHERE Id = @Id AND Recipient = @Recipient AND WorkspaceId = @WorkspaceId;",
            new { WorkspaceId = workspaceId, Recipient = recipient, Id = id, Read = read, Now = now }) == 1;
    }

    public async Task<int> MarkAllReadAsync(Guid workspaceId, string recipient, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.Notifications SET ReadAt = @Now WHERE Recipient = @Recipient AND WorkspaceId = @WorkspaceId AND ReadAt IS NULL;",
            new { WorkspaceId = workspaceId, Recipient = recipient, Now = now });
    }

    public async Task ResolveAsync(string kind, string refKey, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.Notifications SET ReadAt = @Now WHERE Kind = @Kind AND RefKey = @RefKey AND ReadAt IS NULL;",
            new { Kind = kind, RefKey = refKey, Now = now });
    }

    public async Task<IReadOnlyDictionary<string, string?>> PrefsAsync(IReadOnlyCollection<string> vantraIds)
    {
        if (vantraIds.Count == 0) return new Dictionary<string, string?>();
        using var conn = factory.Flow();
        var rows = await conn.QueryAsync<PrefsRow>(
            "SELECT VantraId, NotificationPrefs FROM flow.UserProfiles WHERE VantraId IN @Ids;", new { Ids = vantraIds.Take(1000).ToList() });
        return rows.ToDictionary(r => r.VantraId, r => r.NotificationPrefs, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SavePrefsAsync(string vantraId, string json)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.UserProfiles SET NotificationPrefs = @Json, UpdatedAt = SYSUTCDATETIME() WHERE VantraId = @VantraId;
IF @@ROWCOUNT = 0
    INSERT INTO flow.UserProfiles (VantraId, NotificationPrefs) VALUES (@VantraId, @Json);", new { VantraId = vantraId, Json = json });
    }

    /// <summary>The job title kept in Flow (Settings → Profile), or null.</summary>
    public async Task<string?> JobTitleAsync(string vantraId)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<string?>("SELECT JobTitle FROM flow.UserProfiles WHERE VantraId = @VantraId;", new { VantraId = vantraId });
    }

    public async Task SaveJobTitleAsync(string vantraId, string? title)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.UserProfiles SET JobTitle = @Title, UpdatedAt = SYSUTCDATETIME() WHERE VantraId = @VantraId;
IF @@ROWCOUNT = 0 AND @Title IS NOT NULL
    INSERT INTO flow.UserProfiles (VantraId, JobTitle) VALUES (@VantraId, @Title);", new { VantraId = vantraId, Title = title });
    }

    public async Task<(string Name, string Owner)?> FlowOwnerAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        var row = await conn.QuerySingleOrDefaultAsync<OwnerRow>(@"
SELECT Name, ISNULL(OwnerId, CreatedBy) AS Owner FROM flow.Flows WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = flowId });
        return row is null ? null : (row.Name, row.Owner);
    }

    private sealed record OwnerRow(string Name, string Owner);
    private sealed record PrefsRow(string VantraId, string? NotificationPrefs);

    public async Task PruneAsync(DateTime before)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("DELETE TOP (5000) FROM flow.Notifications WHERE CreatedAt < @Before;", new { Before = before });
    }

    /// <summary>Dapper can't expand an empty list in <c>NOT IN</c>: a kind that never exists stands in.</summary>
    private static List<string> Hide(IReadOnlyCollection<string> hidden) => hidden.Count == 0 ? ["-"] : hidden.ToList();
}

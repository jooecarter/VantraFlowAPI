using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>A workspace's app connections (one live one per app) and the webhooks Flow registered through them.</summary>
public sealed class ConnectionsRepository(SqlConnectionFactory factory)
{
    private const string ConnectionCols =
        "Id, WorkspaceId, App, RemoteWorkspaceId, RemoteWorkspaceName, KeyId, TokenCipher, Status, LastError, ConnectedBy, ConnectedByName, ConnectedAt, LastUsedAt";

    private const string SubscriptionCols = "Id, WorkspaceId, ConnectionId, App, Scope, RemoteId, SecretCipher, Events, CreatedAt, LastEventAt";

    public async Task<IReadOnlyList<ConnectionRow>> LiveAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<ConnectionRow>(
            $"SELECT {ConnectionCols} FROM flow.Connections WHERE WorkspaceId = @WorkspaceId AND RevokedAt IS NULL;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<ConnectionRow?> LiveAsync(Guid workspaceId, string app)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<ConnectionRow>(
            $"SELECT {ConnectionCols} FROM flow.Connections WHERE WorkspaceId = @WorkspaceId AND App = @App AND RevokedAt IS NULL;",
            new { WorkspaceId = workspaceId, App = app });
    }

    public async Task<ConnectionRow?> GetAsync(Guid id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<ConnectionRow>($"SELECT {ConnectionCols} FROM flow.Connections WHERE Id = @Id;", new { Id = id });
    }

    /// <summary>Saves a new live connection, revoking any earlier one for the app in the same batch.</summary>
    public async Task InsertAsync(ConnectionRow row)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.Connections SET Status = N'revoked', RevokedAt = SYSUTCDATETIME()
WHERE WorkspaceId = @WorkspaceId AND App = @App AND RevokedAt IS NULL;
INSERT INTO flow.Connections (Id, WorkspaceId, App, RemoteWorkspaceId, RemoteWorkspaceName, KeyId, TokenCipher, Status, ConnectedBy, ConnectedByName, ConnectedAt)
VALUES (@Id, @WorkspaceId, @App, @RemoteWorkspaceId, @RemoteWorkspaceName, @KeyId, @TokenCipher, N'active', @ConnectedBy, @ConnectedByName, @ConnectedAt);",
            new { row.Id, row.WorkspaceId, row.App, row.RemoteWorkspaceId, row.RemoteWorkspaceName, row.KeyId, row.TokenCipher, row.ConnectedBy, row.ConnectedByName, row.ConnectedAt });
    }

    /// <summary>Gone: revoked here (Disconnect) or found revoked in the app (a 401). Its subscriptions go too.</summary>
    public async Task RevokeAsync(Guid id, string? error)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.Connections SET Status = N'revoked', RevokedAt = SYSUTCDATETIME(), LastError = @Error WHERE Id = @Id AND RevokedAt IS NULL;
DELETE FROM flow.Subscriptions WHERE ConnectionId = @Id;", new { Id = id, Error = error });
    }

    public async Task TouchAsync(Guid id, string? error)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.Connections
SET LastUsedAt = SYSUTCDATETIME(), LastError = @Error, Status = CASE WHEN @Error IS NULL THEN N'active' ELSE N'error' END
WHERE Id = @Id AND RevokedAt IS NULL;", new { Id = id, Error = error });
    }

    // ── Subscriptions ──

    public async Task<IReadOnlyList<SubscriptionRow>> SubscriptionsAsync(Guid connectionId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<SubscriptionRow>($"SELECT {SubscriptionCols} FROM flow.Subscriptions WHERE ConnectionId = @Id;", new { Id = connectionId })).ToList();
    }

    public async Task<SubscriptionRow?> SubscriptionAsync(string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<SubscriptionRow>($"SELECT {SubscriptionCols} FROM flow.Subscriptions WHERE Id = @Id;", new { Id = id });
    }

    public async Task InsertSubscriptionAsync(SubscriptionRow s)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
INSERT INTO flow.Subscriptions (Id, WorkspaceId, ConnectionId, App, Scope, RemoteId, SecretCipher, Events)
VALUES (@Id, @WorkspaceId, @ConnectionId, @App, @Scope, @RemoteId, @SecretCipher, @Events);",
            new { s.Id, s.WorkspaceId, s.ConnectionId, s.App, s.Scope, s.RemoteId, s.SecretCipher, s.Events });
    }

    public async Task DeleteSubscriptionAsync(string id)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("DELETE FROM flow.Subscriptions WHERE Id = @Id;", new { Id = id });
    }

    public async Task SubscriptionSeenAsync(string id)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Subscriptions SET LastEventAt = SYSUTCDATETIME() WHERE Id = @Id;", new { Id = id });
    }
}

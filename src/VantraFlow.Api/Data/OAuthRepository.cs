using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>
/// Outside apps (db/004): each workspace's OAuth connections (one live one per provider), consents waiting to be
/// claimed, and where each polling trigger is up to.
/// </summary>
public sealed class OAuthRepository(SqlConnectionFactory factory)
{
    private const string Cols =
        "Id, WorkspaceId, Provider, AccountId, AccountName, AccountEmail, TeamId, BotUserId, Scopes, AccessCipher, RefreshCipher, ExpiresAt, Status, " +
        "LastError, ConnectedBy, ConnectedByName, ConnectedAt, LastUsedAt";

    public async Task<IReadOnlyList<OAuthConnectionRow>> LiveAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<OAuthConnectionRow>($"SELECT {Cols} FROM flow.OAuthConnections WHERE WorkspaceId = @WorkspaceId AND RevokedAt IS NULL;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<OAuthConnectionRow?> LiveAsync(Guid workspaceId, string provider)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<OAuthConnectionRow>(
            $"SELECT {Cols} FROM flow.OAuthConnections WHERE WorkspaceId = @WorkspaceId AND Provider = @Provider AND RevokedAt IS NULL;",
            new { WorkspaceId = workspaceId, Provider = provider });
    }

    /// <summary>The live Slack connections to one Slack workspace (its events go to each).</summary>
    public async Task<IReadOnlyList<OAuthConnectionRow>> ByTeamAsync(string teamId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<OAuthConnectionRow>(
            $"SELECT {Cols} FROM flow.OAuthConnections WHERE TeamId = @TeamId AND Provider = N'slack' AND RevokedAt IS NULL;",
            new { TeamId = teamId })).ToList();
    }

    /// <summary>Saves a new live connection, revoking any earlier one for the provider in the same batch.</summary>
    public async Task InsertAsync(OAuthConnectionRow r)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.OAuthConnections SET Status = N'revoked', RevokedAt = SYSUTCDATETIME()
WHERE WorkspaceId = @WorkspaceId AND Provider = @Provider AND RevokedAt IS NULL;
INSERT INTO flow.OAuthConnections (Id, WorkspaceId, Provider, AccountId, AccountName, AccountEmail, TeamId, BotUserId, Scopes, AccessCipher, RefreshCipher,
    ExpiresAt, Status, ConnectedBy, ConnectedByName, ConnectedAt)
VALUES (@Id, @WorkspaceId, @Provider, @AccountId, @AccountName, @AccountEmail, @TeamId, @BotUserId, @Scopes, @AccessCipher, @RefreshCipher,
    @ExpiresAt, N'active', @ConnectedBy, @ConnectedByName, @ConnectedAt);",
            new
            {
                r.Id, r.WorkspaceId, r.Provider, r.AccountId, r.AccountName, r.AccountEmail, r.TeamId, r.BotUserId, r.Scopes, r.AccessCipher, r.RefreshCipher,
                r.ExpiresAt, r.ConnectedBy, r.ConnectedByName, r.ConnectedAt,
            });
    }

    /// <summary>Fresh tokens after a refresh (the refresh token kept when the provider sent none).</summary>
    public async Task UpdateTokensAsync(Guid id, byte[] access, byte[]? refresh, DateTime? expiresAt)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.OAuthConnections
SET AccessCipher = @Access, RefreshCipher = ISNULL(@Refresh, RefreshCipher), ExpiresAt = @ExpiresAt, Status = N'active', LastError = NULL
WHERE Id = @Id AND RevokedAt IS NULL;", new { Id = id, Access = access, Refresh = refresh, ExpiresAt = expiresAt });
    }

    public async Task TouchAsync(Guid id, string? error)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.OAuthConnections
SET LastUsedAt = SYSUTCDATETIME(), LastError = @Error, Status = CASE WHEN @Error IS NULL THEN N'active' ELSE N'error' END
WHERE Id = @Id AND RevokedAt IS NULL;", new { Id = id, Error = error is { Length: > 400 } ? error[..400] : error });
    }

    public async Task RevokeAsync(Guid id, string? error)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.OAuthConnections SET Status = N'revoked', RevokedAt = SYSUTCDATETIME(), LastError = @Error WHERE Id = @Id AND RevokedAt IS NULL;",
            new { Id = id, Error = error is { Length: > 400 } ? error[..400] : error });
    }

    // ── Consents waiting to be claimed ──

    public async Task InsertPendingAsync(string id, Guid workspaceId, string provider, string vantraId, byte[] payload, DateTime expiresAt)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
INSERT INTO flow.OAuthPending (Id, WorkspaceId, Provider, VantraId, Payload, ExpiresAt) VALUES (@Id, @WorkspaceId, @Provider, @VantraId, @Payload, @ExpiresAt);",
            new { Id = id, WorkspaceId = workspaceId, Provider = provider, VantraId = vantraId, Payload = payload, ExpiresAt = expiresAt });
    }

    /// <summary>Takes a waiting consent, once: only the person who started it, in that workspace, before it expires.</summary>
    public async Task<byte[]?> TakePendingAsync(string id, Guid workspaceId, string provider, string vantraId, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<byte[]?>(@"
DELETE FROM flow.OAuthPending
OUTPUT DELETED.Payload
WHERE Id = @Id AND WorkspaceId = @WorkspaceId AND Provider = @Provider AND VantraId = @VantraId AND ExpiresAt > @Now;",
            new { Id = id, WorkspaceId = workspaceId, Provider = provider, VantraId = vantraId, Now = now });
    }

    // ── Polling triggers ──

    /// <summary>Enabled flows with a polling trigger whose next poll is due (or that have never polled).</summary>
    public async Task<IReadOnlyList<PollDueRow>> DuePollsAsync(DateTime now, int take)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<PollDueRow>(@"
SELECT TOP (@Take) f.Id AS FlowId, f.WorkspaceId, f.TriggerApp AS Provider, f.TriggerEvent, f.Definition, f.UpdatedAt,
       c.Fingerprint, c.Bookmark, c.NextPollAt, c.Failures
FROM flow.Flows f
LEFT JOIN flow.PollCursors c ON c.FlowId = f.Id
WHERE f.Enabled = 1 AND f.TriggerApp IN (N'microsoft', N'google', N'stripe', N'hubspot', N'xero') AND (c.FlowId IS NULL OR c.NextPollAt <= @Now)
ORDER BY c.NextPollAt;", new { Take = take, Now = now })).ToList();
    }

    /// <summary>One instance's claim on a poll (its lease): true for exactly one caller.</summary>
    public async Task<bool> ClaimPollAsync(PollDueRow p, DateTime now, DateTime leaseUntil)
    {
        using var conn = factory.Flow();
        if (p.NextPollAt is not null)
            return await conn.ExecuteAsync("UPDATE flow.PollCursors SET NextPollAt = @Lease WHERE FlowId = @FlowId AND NextPollAt <= @Now;",
                new { p.FlowId, Lease = leaseUntil, Now = now }) == 1;
        try
        {
            return await conn.ExecuteAsync(@"
INSERT INTO flow.PollCursors (FlowId, WorkspaceId, Provider, Fingerprint, NextPollAt) VALUES (@FlowId, @WorkspaceId, @Provider, N'', @Lease);",
                new { p.FlowId, p.WorkspaceId, p.Provider, Lease = leaseUntil }) == 1;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { return false; }
    }

    public async Task SavePollAsync(string flowId, string fingerprint, string? bookmark, DateTime nextPollAt, int failures, string? error, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.PollCursors
SET Fingerprint = @Fingerprint, Bookmark = @Bookmark, NextPollAt = @Next, Failures = @Failures, LastError = @Error, LastPolledAt = @Now
WHERE FlowId = @FlowId;",
            new { FlowId = flowId, Fingerprint = fingerprint, Bookmark = bookmark, Next = nextPollAt, Failures = failures,
                  Error = error is { Length: > 400 } ? error[..400] : error, Now = now });
    }

    /// <summary>Housekeeping: cursors of flows that are off or gone, and consents nobody claimed.</summary>
    public async Task PruneAsync(DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
DELETE c FROM flow.PollCursors c WHERE NOT EXISTS (SELECT 1 FROM flow.Flows f WHERE f.Id = c.FlowId AND f.Enabled = 1);
DELETE TOP (5000) FROM flow.OAuthPending WHERE ExpiresAt < @Now;", new { Now = now });
    }
}

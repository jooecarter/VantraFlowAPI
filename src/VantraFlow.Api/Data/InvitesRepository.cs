using System.Data;
using Dapper;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Workspaces;

namespace VantraFlow.Api.Data;

/// <summary>What the invitation endpoints need of the database (<see cref="InvitesRepository"/>; a fake in tests).</summary>
public interface IInviteStore
{
    /// <summary>The workspace's open invitations (expired ones too: they're listed as such), newest first.</summary>
    Task<IReadOnlyList<InviteRow>> ListOpenAsync(Guid workspaceId);
    Task<InviteRow?> GetAsync(Guid workspaceId, Guid id);
    /// <summary>Invitations issued or sent since then (the daily cap).</summary>
    Task<int> CountSentSinceAsync(Guid workspaceId, DateTime sinceUtc);
    /// <summary>
    /// Issue an invitation to <paramref name="email"/>: a new row, or the address's earlier one (in any state)
    /// re-opened with this token's hash, a fresh 14 days, the new role and this inviter.
    /// </summary>
    Task<InviteRow> IssueAsync(Guid workspaceId, string email, string role, byte[] tokenHash, string by, string? byName);
    /// <summary>A resend: a new token's hash and another 14 days, if it's still open. Null when it isn't.</summary>
    Task<InviteRow?> RenewAsync(Guid workspaceId, Guid id, byte[] tokenHash);
    /// <summary>The email went out: <c>sent</c>, stamped now (the resend cooldown counts from here).</summary>
    Task<InviteRow?> MarkSentAsync(Guid id);
    /// <summary>Withdraw it: <c>revoked</c>. True when it was open (false: already revoked or used, or no such invitation).</summary>
    Task<bool> RevokeAsync(Guid workspaceId, Guid id);
    /// <summary>The invitation behind a token's hash, in any state, with its workspace's name and look. Null when there's none.</summary>
    Task<InviteRow?> FindByTokenAsync(byte[] tokenHash);
    /// <summary>
    /// Open the invitation as <paramref name="vantraId"/> (whose account email is <paramref name="accountEmail"/>),
    /// deciding with <see cref="InviteRules.Evaluate"/> on the rows as locked, so two clicks can't both join or race a
    /// revoke. Accept adds them with the invitation's role and marks it accepted. Null when there's no such token.
    /// </summary>
    Task<(InviteOutcome Outcome, InviteRow Invite)?> AcceptAsync(byte[] tokenHash, string vantraId, string? accountEmail, string? name);
}

/// <summary><c>flow.Invites</c> (db/005): see <see cref="InviteRules"/>. Only called once db/005 has run.</summary>
public sealed class InvitesRepository(SqlConnectionFactory factory) : IInviteStore
{
    private const string Cols =
        "i.Id, i.WorkspaceId, i.Email, i.Role, i.Status, i.InvitedBy, i.InvitedByName, i.CreatedAt, i.SentAt, i.ExpiresAt, i.AcceptedBy, i.AcceptedAt";
    private const string WsCols = "w.Name AS WorkspaceName, w.Color AS WorkspaceColor, w.Logo AS WorkspaceLogo";

    public async Task<IReadOnlyList<InviteRow>> ListOpenAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<InviteRow>($@"
SELECT {Cols} FROM flow.Invites i
WHERE i.WorkspaceId = @WorkspaceId AND i.Status IN (N'pending', N'sent')
ORDER BY i.CreatedAt DESC, i.Email;", new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<InviteRow?> GetAsync(Guid workspaceId, Guid id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<InviteRow>($"SELECT {Cols} FROM flow.Invites i WHERE i.WorkspaceId = @WorkspaceId AND i.Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id });
    }

    public async Task<int> CountSentSinceAsync(Guid workspaceId, DateTime sinceUtc)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM flow.Invites WHERE WorkspaceId = @WorkspaceId AND (CreatedAt >= @Since OR SentAt >= @Since);",
            new { WorkspaceId = workspaceId, Since = sinceUtc });
    }

    public async Task<InviteRow> IssueAsync(Guid workspaceId, string email, string role, byte[] tokenHash, string by, string? byName)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleAsync<InviteRow>($@"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRAN;
UPDATE flow.Invites WITH (UPDLOCK, SERIALIZABLE)
   SET Role = @Role, TokenHash = @TokenHash, Status = N'pending', InvitedBy = @By, InvitedByName = @ByName,
       CreatedAt = SYSUTCDATETIME(), SentAt = NULL, ExpiresAt = DATEADD(DAY, @Days, SYSUTCDATETIME()),
       AcceptedBy = NULL, AcceptedAt = NULL, RevokedAt = NULL
 WHERE WorkspaceId = @WorkspaceId AND Email = @Email;
IF @@ROWCOUNT = 0
    INSERT INTO flow.Invites (Id, WorkspaceId, Email, Role, TokenHash, InvitedBy, InvitedByName, ExpiresAt)
    VALUES (NEWID(), @WorkspaceId, @Email, @Role, @TokenHash, @By, @ByName, DATEADD(DAY, @Days, SYSUTCDATETIME()));
COMMIT;
SELECT {Cols} FROM flow.Invites i WHERE i.WorkspaceId = @WorkspaceId AND i.Email = @Email;",
            new { WorkspaceId = workspaceId, Email = email, Role = role, TokenHash = tokenHash, By = by, ByName = byName, Days = InviteRules.ValidDays });
    }

    public async Task<InviteRow?> RenewAsync(Guid workspaceId, Guid id, byte[] tokenHash)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<InviteRow>($@"
SET NOCOUNT ON;
UPDATE flow.Invites SET TokenHash = @TokenHash, ExpiresAt = DATEADD(DAY, @Days, SYSUTCDATETIME())
 WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND Status IN (N'pending', N'sent');
SELECT {Cols} FROM flow.Invites i WHERE i.WorkspaceId = @WorkspaceId AND i.Id = @Id AND i.Status IN (N'pending', N'sent');",
            new { WorkspaceId = workspaceId, Id = id, TokenHash = tokenHash, Days = InviteRules.ValidDays });
    }

    public async Task<InviteRow?> MarkSentAsync(Guid id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<InviteRow>($@"
SET NOCOUNT ON;
UPDATE flow.Invites SET Status = N'sent', SentAt = SYSUTCDATETIME() WHERE Id = @Id AND Status IN (N'pending', N'sent');
SELECT {Cols} FROM flow.Invites i WHERE i.Id = @Id;", new { Id = id });
    }

    public async Task<bool> RevokeAsync(Guid workspaceId, Guid id)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.Invites SET Status = N'revoked', RevokedAt = SYSUTCDATETIME()
 WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND Status IN (N'pending', N'sent');",
            new { WorkspaceId = workspaceId, Id = id }) > 0;
    }

    public async Task<InviteRow?> FindByTokenAsync(byte[] tokenHash)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<InviteRow>($@"
SELECT {Cols}, {WsCols}
FROM flow.Invites i
JOIN flow.Workspaces w ON w.Id = i.WorkspaceId
WHERE i.TokenHash = @TokenHash;", new { TokenHash = tokenHash });
    }

    public async Task<(InviteOutcome Outcome, InviteRow Invite)?> AcceptAsync(byte[] tokenHash, string vantraId, string? accountEmail, string? name)
    {
        using var conn = factory.Flow();
        await conn.OpenAsync();
        var workspaceId = await conn.ExecuteScalarAsync<Guid?>("SELECT WorkspaceId FROM flow.Invites WHERE TokenHash = @TokenHash;", new { TokenHash = tokenHash });
        if (workspaceId is null) return null;

        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        // The workspace row first, as every membership change, then the invitation.
        await conn.ExecuteScalarAsync<Guid?>("SELECT Id FROM flow.Workspaces WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", new { Id = workspaceId }, tx);
        var invite = await conn.QuerySingleOrDefaultAsync<InviteRow>($@"
SELECT {Cols}, {WsCols}
FROM flow.Invites i WITH (UPDLOCK, HOLDLOCK)
JOIN flow.Workspaces w ON w.Id = i.WorkspaceId
WHERE i.TokenHash = @TokenHash;", new { TokenHash = tokenHash }, tx);
        if (invite is null) { tx.Rollback(); return null; } // a new link, or the workspace deleted, meanwhile

        var isMember = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM flow.WorkspaceMembers WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @WorkspaceId AND VantraId = @VantraId;",
            new { invite.WorkspaceId, VantraId = vantraId }, tx) > 0;
        var outcome = InviteRules.Evaluate(invite.Status, invite.ExpiresAt, DateTime.UtcNow, invite.Email, accountEmail, isMember);
        if (outcome == InviteOutcome.Accept)
        {
            await conn.ExecuteAsync(@"
INSERT INTO flow.WorkspaceMembers (WorkspaceId, VantraId, Role, Email, Name, AddedBy)
VALUES (@WorkspaceId, @VantraId, @Role, @Email, @Name, @InvitedBy);
UPDATE flow.Invites SET Status = N'accepted', AcceptedBy = @VantraId, AcceptedAt = SYSUTCDATETIME() WHERE Id = @Id;",
                new { invite.WorkspaceId, VantraId = vantraId, invite.Role, invite.Email, Name = name, invite.InvitedBy, invite.Id }, tx);
            tx.Commit();
        }
        else if (outcome == InviteOutcome.AlreadyMember && InviteRules.IsOpen(invite.Status))
        {
            await conn.ExecuteAsync("UPDATE flow.Invites SET Status = N'accepted', AcceptedBy = @VantraId, AcceptedAt = SYSUTCDATETIME() WHERE Id = @Id;",
                new { VantraId = vantraId, invite.Id }, tx);
            tx.Commit();
        }
        else tx.Rollback();
        return (outcome, invite);
    }
}

using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using VantraFlow.Api.Data;

namespace VantraFlow.Api.Shared;

/// <summary>The shared identity database couldn't be read or written: the request fails, nothing diverges.</summary>
public sealed class SharedUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Members, app access and invitations in <c>vantra.*</c> (db/shared/001 and 002, in VantraProjectsAPI).</summary>
public interface ISharedStore
{
    /// <summary>Stage 2's tables are there: 001's, 002's, and the index each script makes last (never a definition).</summary>
    Task<bool> ProbeAsync();

    /// <summary>The shared workspace this app's local workspace is linked to, while the link is on.</summary>
    Task<Guid?> SharedIdAsync(string app, Guid localId);

    /// <summary>The local ids among these that are linked (and on) for this app.</summary>
    Task<IReadOnlyList<Guid>> LinkedAsync(string app, IReadOnlyCollection<Guid> localIds);

    /// <summary>This app's local ids of the shared workspaces the person is a member of (links that are on).</summary>
    Task<IReadOnlyList<Guid>> UserLinksAsync(string app, string vantraId);

    Task<SharedState?> StateAsync(Guid sharedId);

    /// <summary>
    /// Decide and apply one change atomically: lock the workspace, hand its state to <paramref name="decide"/> (one of
    /// the <see cref="SharedRules"/>), and apply the plan. Null when the workspace doesn't exist. The state after is
    /// returned with an applied plan.
    /// </summary>
    Task<(SharedPlan Plan, SharedState After)?> ApplyAsync(Guid sharedId, string by, string addedVia, Func<SharedState, SharedPlan> decide);

    Task<IReadOnlyList<SharedInviteRow>> OpenInvitesAsync(Guid sharedId);

    /// <summary>Every invitation id of the workspace, open or not (Projects tells the rows holding its seats from its own by these).</summary>
    Task<IReadOnlyList<Guid>> InviteIdsAsync(Guid sharedId);
    Task<SharedInviteRow?> InviteAsync(Guid sharedId, Guid inviteId);
    Task<SharedInviteRow?> InviteByTokenAsync(byte[] tokenHash);
    Task<int> InvitesIssuedSinceAsync(Guid sharedId, DateTime sinceUtc);

    /// <summary>Issue (or re-issue, with a new token and 14 more days) the invitation to an address.</summary>
    Task<SharedInviteRow> IssueInviteAsync(Guid sharedId, string email, string role, string? guestType, IReadOnlyList<string> apps,
        byte[] tokenHash, string by, string fromApp, DateTime expiresAt);

    Task<SharedInviteRow?> RenewInviteAsync(Guid sharedId, Guid inviteId, byte[] tokenHash, DateTime expiresAt);
    Task<SharedInviteRow?> MarkInviteSentAsync(Guid inviteId);
    Task<bool> RevokeInviteAsync(Guid sharedId, Guid inviteId);

    /// <summary>
    /// Open an invitation as the signed-in account: in one transaction, match it, and when it's to be accepted add them
    /// (<see cref="SharedRules.Join"/>) and mark it accepted. Null when there's no such token.
    /// </summary>
    Task<(SharedInviteOutcome Outcome, SharedInviteRow Invite, SharedState? After)?> AcceptInviteAsync(byte[] tokenHash, string vantraId, string? accountEmail, DateTime nowUtc);
}

public sealed class SqlSharedStore(SqlConnectionFactory factory) : ISharedStore
{
    public const string ProbeSql = @"
SELECT CASE WHEN OBJECT_ID(N'vantra.Workspaces', N'U') IS NOT NULL
             AND OBJECT_ID(N'vantra.WorkspaceApps', N'U') IS NOT NULL
             AND OBJECT_ID(N'vantra.WorkspaceMembers', N'U') IS NOT NULL
             AND OBJECT_ID(N'vantra.WorkspaceInvites', N'U') IS NOT NULL
             AND OBJECT_ID(N'vantra.WorkspaceMemberApps', N'U') IS NOT NULL
             AND OBJECT_ID(N'vantra.WorkspaceInviteApps', N'U') IS NOT NULL
             AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_WorkspaceApps_Local' AND object_id = OBJECT_ID(N'vantra.WorkspaceApps'))
             AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WorkspaceMemberApps_User' AND object_id = OBJECT_ID(N'vantra.WorkspaceMemberApps'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    public const int ChunkSize = 500;

    private const string InviteCols = @"i.Id, i.WorkspaceId, i.Email, i.Role, i.GuestType, i.Status, i.InvitedByVantraId, i.InvitedFromApp,
       i.CreatedAt, i.SentAt, i.ExpiresAt, i.AcceptedByVantraId, w.Name AS WorkspaceName, w.Color AS WorkspaceColor, w.Logo AS WorkspaceLogo";

    public const string StateSql = @"
SELECT w.Id, w.Name, w.OwnerVantraId FROM vantra.Workspaces w WITH (UPDLOCK, HOLDLOCK) WHERE w.Id = @Id;
SELECT a.App, a.LocalWorkspaceId, a.Enabled FROM vantra.WorkspaceApps a WHERE a.WorkspaceId = @Id;
SELECT m.VantraId, m.Role, m.GuestType, m.JoinedAt FROM vantra.WorkspaceMembers m WHERE m.WorkspaceId = @Id ORDER BY m.JoinedAt, m.VantraId;
SELECT ma.VantraId, ma.App FROM vantra.WorkspaceMemberApps ma WHERE ma.WorkspaceId = @Id;";

    public async Task<bool> ProbeAsync()
    {
        using var conn = factory.Vantra();
        return await conn.ExecuteScalarAsync<bool>(ProbeSql);
    }

    public async Task<Guid?> SharedIdAsync(string app, Guid localId)
    {
        using var conn = factory.Vantra();
        return await conn.ExecuteScalarAsync<Guid?>(
            "SELECT a.WorkspaceId FROM vantra.WorkspaceApps a WHERE a.App = @App AND a.LocalWorkspaceId = @Local AND a.Enabled = 1;",
            new { App = app, Local = localId });
    }

    public async Task<IReadOnlyList<Guid>> LinkedAsync(string app, IReadOnlyCollection<Guid> localIds)
    {
        if (localIds.Count == 0) return [];
        var rows = new List<Guid>();
        using var conn = factory.Vantra();
        foreach (var chunk in localIds.Distinct().Chunk(ChunkSize))
            rows.AddRange(await conn.QueryAsync<Guid>(
                "SELECT a.LocalWorkspaceId FROM vantra.WorkspaceApps a WHERE a.App = @App AND a.Enabled = 1 AND a.LocalWorkspaceId IN @Ids;",
                new { App = app, Ids = chunk }));
        return rows;
    }

    public async Task<IReadOnlyList<Guid>> UserLinksAsync(string app, string vantraId)
    {
        using var conn = factory.Vantra();
        return (await conn.QueryAsync<Guid>(@"
SELECT a.LocalWorkspaceId
FROM vantra.WorkspaceMembers m
JOIN vantra.WorkspaceApps a ON a.WorkspaceId = m.WorkspaceId AND a.App = @App AND a.Enabled = 1
WHERE m.VantraId = @VantraId;", new { App = app, VantraId = vantraId })).ToList();
    }

    public async Task<SharedState?> StateAsync(Guid sharedId)
    {
        using var conn = factory.Vantra();
        await conn.OpenAsync();
        return await ReadStateAsync(conn, null, sharedId);
    }

    private static async Task<SharedState?> ReadStateAsync(SqlConnection conn, IDbTransaction? tx, Guid sharedId)
    {
        using var grid = await conn.QueryMultipleAsync(StateSql, new { Id = sharedId }, tx);
        var ws = await grid.ReadSingleOrDefaultAsync<WorkspaceRow>();
        var links = (await grid.ReadAsync<LinkRow>()).ToList();
        var members = (await grid.ReadAsync<MemberRow>()).ToList();
        var access = (await grid.ReadAsync<AccessRow>()).ToList();
        if (ws is null) return null;
        var apps = access.GroupBy(a => a.VantraId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => SharedApps.Order(g.Select(a => a.App)), StringComparer.OrdinalIgnoreCase);
        return new SharedState(ws.Id, ws.Name, ws.OwnerVantraId,
            links.Where(l => SharedApps.IsValid(l.App)).Select(l => new SharedLink(l.App, l.LocalWorkspaceId, l.Enabled)).ToList(),
            members.Select(m => new SharedMember(m.VantraId, m.Role, m.GuestType, m.JoinedAt, apps.GetValueOrDefault(m.VantraId) ?? [])).ToList());
    }

    public async Task<(SharedPlan Plan, SharedState After)?> ApplyAsync(Guid sharedId, string by, string addedVia, Func<SharedState, SharedPlan> decide)
    {
        using var conn = factory.Vantra();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        var state = await ReadStateAsync(conn, tx, sharedId);
        if (state is null) { tx.Rollback(); return null; }
        var plan = decide(state);
        if (!plan.Ok || (plan.Empty && plan.PrimaryOwner == state.PrimaryOwner)) { tx.Rollback(); return (plan, state); }
        await WriteAsync(conn, tx, sharedId, state, plan, by, addedVia);
        var after = (await ReadStateAsync(conn, tx, sharedId))!;
        tx.Commit();
        return (plan, after);
    }

    private static async Task WriteAsync(SqlConnection conn, IDbTransaction tx, Guid sharedId, SharedState state, SharedPlan plan, string by, string addedVia)
    {
        foreach (var a in plan.Adds)
            await conn.ExecuteAsync(@"
INSERT INTO vantra.WorkspaceMembers (WorkspaceId, VantraId, Role, GuestType, AddedVia, UpdatedByVantraId)
SELECT @Id, @VantraId, @Role, @GuestType, @Via, @By
WHERE NOT EXISTS (SELECT 1 FROM vantra.WorkspaceMembers WHERE WorkspaceId = @Id AND VantraId = @VantraId);",
                new { Id = sharedId, a.VantraId, a.Role, a.GuestType, Via = addedVia, By = Clip(by) }, tx);
        foreach (var c in plan.Changes)
            await conn.ExecuteAsync(@"
UPDATE vantra.WorkspaceMembers
   SET Role = @Role, GuestType = @GuestType, UpdatedAt = SYSUTCDATETIME(), UpdatedByVantraId = @By
 WHERE WorkspaceId = @Id AND VantraId = @VantraId;",
                new { Id = sharedId, c.VantraId, c.Role, c.GuestType, By = Clip(by) }, tx);
        foreach (var g in plan.Grants)
            await conn.ExecuteAsync(@"
INSERT INTO vantra.WorkspaceMemberApps (WorkspaceId, VantraId, App, GrantedBy)
SELECT @Id, m.VantraId, @App, @By
FROM vantra.WorkspaceMembers m
WHERE m.WorkspaceId = @Id AND m.VantraId = @VantraId
  AND NOT EXISTS (SELECT 1 FROM vantra.WorkspaceMemberApps x WHERE x.WorkspaceId = @Id AND x.VantraId = @VantraId AND x.App = @App);",
                new { Id = sharedId, g.VantraId, g.App, By = Clip(by) }, tx);
        foreach (var r in plan.Revokes)
            await conn.ExecuteAsync("DELETE FROM vantra.WorkspaceMemberApps WHERE WorkspaceId = @Id AND VantraId = @VantraId AND App = @App;",
                new { Id = sharedId, r.VantraId, r.App }, tx);
        if (plan.Removals.Count > 0)
            await conn.ExecuteAsync("DELETE FROM vantra.WorkspaceMembers WHERE WorkspaceId = @Id AND VantraId IN @Ids;",
                new { Id = sharedId, Ids = plan.Removals }, tx);
        if (!string.Equals(plan.PrimaryOwner, state.PrimaryOwner, StringComparison.Ordinal))
            await conn.ExecuteAsync("UPDATE vantra.Workspaces SET OwnerVantraId = @Owner, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;",
                new { Id = sharedId, Owner = plan.PrimaryOwner }, tx);
    }

    // ── Invitations ──

    public async Task<IReadOnlyList<SharedInviteRow>> OpenInvitesAsync(Guid sharedId)
    {
        using var conn = factory.Vantra();
        var rows = (await conn.QueryAsync<SharedInviteRow>($@"
SELECT {InviteCols}
FROM vantra.WorkspaceInvites i JOIN vantra.Workspaces w ON w.Id = i.WorkspaceId
WHERE i.WorkspaceId = @Id AND i.Status IN (N'pending', N'sent')
ORDER BY i.CreatedAt DESC;", new { Id = sharedId })).ToList();
        await AppsAsync(conn, null, rows);
        return rows;
    }

    public async Task<IReadOnlyList<Guid>> InviteIdsAsync(Guid sharedId)
    {
        using var conn = factory.Vantra();
        return (await conn.QueryAsync<Guid>("SELECT Id FROM vantra.WorkspaceInvites WHERE WorkspaceId = @Id;", new { Id = sharedId })).ToList();
    }

    public async Task<SharedInviteRow?> InviteAsync(Guid sharedId, Guid inviteId)
    {
        using var conn = factory.Vantra();
        var row = await conn.QuerySingleOrDefaultAsync<SharedInviteRow>($@"
SELECT {InviteCols}
FROM vantra.WorkspaceInvites i JOIN vantra.Workspaces w ON w.Id = i.WorkspaceId
WHERE i.WorkspaceId = @Id AND i.Id = @InviteId;", new { Id = sharedId, InviteId = inviteId });
        if (row is not null) await AppsAsync(conn, null, [row]);
        return row;
    }

    public async Task<SharedInviteRow?> InviteByTokenAsync(byte[] tokenHash)
    {
        using var conn = factory.Vantra();
        var row = await conn.QuerySingleOrDefaultAsync<SharedInviteRow>($@"
SELECT {InviteCols}
FROM vantra.WorkspaceInvites i JOIN vantra.Workspaces w ON w.Id = i.WorkspaceId
WHERE i.TokenHash = @Hash;", new { Hash = tokenHash });
        if (row is not null) await AppsAsync(conn, null, [row]);
        return row;
    }

    public async Task<int> InvitesIssuedSinceAsync(Guid sharedId, DateTime sinceUtc)
    {
        using var conn = factory.Vantra();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM vantra.WorkspaceInvites WHERE WorkspaceId = @Id AND CreatedAt >= @Since;",
            new { Id = sharedId, Since = sinceUtc });
    }

    public async Task<SharedInviteRow> IssueInviteAsync(Guid sharedId, string email, string role, string? guestType, IReadOnlyList<string> apps,
        byte[] tokenHash, string by, string fromApp, DateTime expiresAt)
    {
        using var conn = factory.Vantra();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        // One row per (workspace, email): an address invited before gets its row back, with a new link.
        var id = await conn.ExecuteScalarAsync<Guid?>(@"
SELECT Id FROM vantra.WorkspaceInvites WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @Id AND Email = @Email;",
            new { Id = sharedId, Email = email }, tx);
        if (id is { } existing)
            await conn.ExecuteAsync(@"
UPDATE vantra.WorkspaceInvites
   SET Role = @Role, GuestType = @GuestType, TokenHash = @Hash, Status = N'pending', InvitedByVantraId = @By,
       InvitedFromApp = @FromApp, CreatedAt = SYSUTCDATETIME(), SentAt = NULL, ExpiresAt = @ExpiresAt,
       AcceptedByVantraId = NULL, AcceptedAt = NULL, RevokedAt = NULL
 WHERE Id = @InviteId;",
                new { InviteId = existing, Role = role, GuestType = guestType, Hash = tokenHash, By = Clip(by), FromApp = fromApp, ExpiresAt = expiresAt }, tx);
        else
        {
            id = Guid.NewGuid();
            await conn.ExecuteAsync(@"
INSERT INTO vantra.WorkspaceInvites (Id, WorkspaceId, Email, Role, GuestType, TokenHash, Status, InvitedByVantraId, InvitedFromApp, ExpiresAt)
VALUES (@InviteId, @Id, @Email, @Role, @GuestType, @Hash, N'pending', @By, @FromApp, @ExpiresAt);",
                new { InviteId = id, Id = sharedId, Email = email, Role = role, GuestType = guestType, Hash = tokenHash, By = Clip(by), FromApp = fromApp, ExpiresAt = expiresAt }, tx);
        }
        await conn.ExecuteAsync("DELETE FROM vantra.WorkspaceInviteApps WHERE InviteId = @InviteId;", new { InviteId = id }, tx);
        foreach (var app in SharedApps.Order(apps))
            await conn.ExecuteAsync("INSERT INTO vantra.WorkspaceInviteApps (InviteId, App) VALUES (@InviteId, @App);", new { InviteId = id, App = app }, tx);
        var row = await conn.QuerySingleAsync<SharedInviteRow>($@"
SELECT {InviteCols}
FROM vantra.WorkspaceInvites i JOIN vantra.Workspaces w ON w.Id = i.WorkspaceId
WHERE i.Id = @InviteId;", new { InviteId = id }, tx);
        await AppsAsync(conn, tx, [row]);
        tx.Commit();
        return row;
    }

    public async Task<SharedInviteRow?> RenewInviteAsync(Guid sharedId, Guid inviteId, byte[] tokenHash, DateTime expiresAt)
    {
        using (var conn = factory.Vantra())
        {
            var n = await conn.ExecuteAsync(@"
UPDATE vantra.WorkspaceInvites SET TokenHash = @Hash, ExpiresAt = @ExpiresAt, Status = N'pending', SentAt = NULL
 WHERE WorkspaceId = @Id AND Id = @InviteId AND Status IN (N'pending', N'sent');",
                new { Id = sharedId, InviteId = inviteId, Hash = tokenHash, ExpiresAt = expiresAt });
            if (n == 0) return null;
        }
        return await InviteAsync(sharedId, inviteId);
    }

    public async Task<SharedInviteRow?> MarkInviteSentAsync(Guid inviteId)
    {
        Guid? ws;
        using (var conn = factory.Vantra())
            ws = await conn.ExecuteScalarAsync<Guid?>(@"
UPDATE vantra.WorkspaceInvites SET Status = N'sent', SentAt = SYSUTCDATETIME()
OUTPUT inserted.WorkspaceId
 WHERE Id = @InviteId AND Status IN (N'pending', N'sent');", new { InviteId = inviteId });
        return ws is { } id ? await InviteAsync(id, inviteId) : null;
    }

    public async Task<bool> RevokeInviteAsync(Guid sharedId, Guid inviteId)
    {
        using var conn = factory.Vantra();
        return await conn.ExecuteAsync(@"
UPDATE vantra.WorkspaceInvites SET Status = N'revoked', RevokedAt = SYSUTCDATETIME()
 WHERE WorkspaceId = @Id AND Id = @InviteId AND Status IN (N'pending', N'sent');",
            new { Id = sharedId, InviteId = inviteId }) > 0;
    }

    public async Task<(SharedInviteOutcome Outcome, SharedInviteRow Invite, SharedState? After)?> AcceptInviteAsync(
        byte[] tokenHash, string vantraId, string? accountEmail, DateTime nowUtc)
    {
        using var conn = factory.Vantra();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        var row = await conn.QuerySingleOrDefaultAsync<SharedInviteRow>($@"
SELECT {InviteCols}
FROM vantra.WorkspaceInvites i WITH (UPDLOCK, HOLDLOCK) JOIN vantra.Workspaces w ON w.Id = i.WorkspaceId
WHERE i.TokenHash = @Hash;", new { Hash = tokenHash }, tx);
        if (row is null) { tx.Rollback(); return null; }
        await AppsAsync(conn, tx, [row]);
        var state = await ReadStateAsync(conn, tx, row.WorkspaceId);
        if (state is null) { tx.Rollback(); return (SharedInviteOutcome.Invalid, row, null); }
        var outcome = SharedInviteRules.Evaluate(row.Status, row.ExpiresAt, nowUtc, row.Email, accountEmail, state.Find(vantraId) is not null);
        if (outcome != SharedInviteOutcome.Accept) { tx.Rollback(); return (outcome, row, state); }
        var plan = SharedRules.Join(state, vantraId, row.Role, row.GuestType, row.Apps);
        await WriteAsync(conn, tx, row.WorkspaceId, state, plan, vantraId, "invite");
        await conn.ExecuteAsync(@"
UPDATE vantra.WorkspaceInvites SET Status = N'accepted', AcceptedByVantraId = @VantraId, AcceptedAt = SYSUTCDATETIME()
 WHERE Id = @InviteId;", new { InviteId = row.Id, VantraId = Clip(vantraId) }, tx);
        var after = await ReadStateAsync(conn, tx, row.WorkspaceId);
        tx.Commit();
        row.Status = SharedInviteRules.Accepted;
        return (outcome, row, after);
    }

    private static async Task AppsAsync(SqlConnection conn, IDbTransaction? tx, IReadOnlyList<SharedInviteRow> rows)
    {
        if (rows.Count == 0) return;
        var all = new List<(Guid InviteId, string App)>();
        foreach (var chunk in rows.Select(r => r.Id).Chunk(ChunkSize))
            all.AddRange(await conn.QueryAsync<(Guid InviteId, string App)>(
                "SELECT ia.InviteId, ia.App FROM vantra.WorkspaceInviteApps ia WHERE ia.InviteId IN @Ids;", new { Ids = chunk }, tx));
        var byInvite = all.GroupBy(a => a.InviteId).ToDictionary(g => g.Key, g => SharedApps.Order(g.Select(a => a.App)));
        foreach (var r in rows) r.Apps = byInvite.GetValueOrDefault(r.Id) ?? [];
    }

    private static string Clip(string s) => s.Length <= 50 ? s : s[..50];

    private sealed class WorkspaceRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string OwnerVantraId { get; set; } = "";
    }

    private sealed class LinkRow
    {
        public string App { get; set; } = "";
        public Guid LocalWorkspaceId { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class MemberRow
    {
        public string VantraId { get; set; } = "";
        public string Role { get; set; } = "";
        public string? GuestType { get; set; }
        public DateTime JoinedAt { get; set; }
    }

    private sealed class AccessRow
    {
        public string VantraId { get; set; } = "";
        public string App { get; set; } = "";
    }
}

/// <summary>
/// Whether stage 2 applies, and the shared state when it does. The probe is cached like stage 1's: a yes for good, a
/// no for <see cref="NegativeTtl"/>. Reads fall back to "not shared" on any error, so a page never breaks because of
/// the identity database; writes ask strictly and fail instead (503), so an app never writes its own list for a
/// workspace whose members are shared.
/// </summary>
public sealed class SharedMembers(ISharedStore store, ILogger<SharedMembers> log, TimeProvider? clock = null)
{
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private volatile bool ready;
    private long notReadyUntilTicks;

    public ISharedStore Store => store;

    public async Task<bool> ReadyAsync(bool strict = false)
    {
        if (ready) return true;
        if (!strict && clock.GetUtcNow().UtcTicks < Interlocked.Read(ref notReadyUntilTicks)) return false;
        bool ok;
        try
        {
            ok = await store.ProbeAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (strict) throw new SharedUnavailableException("The shared members couldn't be checked.", ex);
            log.LogWarning(ex, "Couldn't probe for the shared members tables; answering without them");
            ok = false;
        }
        if (ok) ready = true;
        else Interlocked.Exchange(ref notReadyUntilTicks, clock.GetUtcNow().Add(NegativeTtl).UtcTicks);
        return ok;
    }

    /// <summary>
    /// The shared workspace of this app's local workspace when its members are shared for this app (linked, the
    /// workspace has the app, and someone has access to it); null otherwise. With <paramref name="strict"/>, an error
    /// throws <see cref="SharedUnavailableException"/> instead of answering null.
    /// </summary>
    public async Task<SharedState?> ForLocalAsync(string app, Guid localId, bool strict = false)
    {
        if (localId == Guid.Empty || !await ReadyAsync(strict)) return null;
        try
        {
            var id = await store.SharedIdAsync(app, localId);
            if (id is null) return null;
            var state = await store.StateAsync(id.Value);
            return state is not null && state.MembersShared(app) ? state : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SharedUnavailableException)
        {
            if (strict) throw new SharedUnavailableException("The shared members couldn't be read.", ex);
            log.LogWarning(ex, "Couldn't read the shared members for a {App} workspace; answering with its own list", app);
            return null;
        }
    }

    /// <summary>The shared state by its own id, strictly (for writes and the other apps' calls).</summary>
    public async Task<SharedState?> StateAsync(Guid sharedId)
    {
        if (!await ReadyAsync(strict: true)) return null;
        try { return await store.StateAsync(sharedId); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SharedUnavailableException("The shared members couldn't be read.", ex);
        }
    }
}

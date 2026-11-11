using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using VantraFlow.Api.Data;

namespace VantraFlow.Api.Shared;

// Shared Vantra workspaces, stage 3: one workspace everywhere. Every workspace is a shared one (created shared, or
// linked on first sight with the same id), its name, logo, colour and time zone are the shared record's, and each app
// is switched on or off per workspace by that app's own server. The same file is in Vantra Projects', Orbit's and
// Flow's APIs (only the namespace differs). Design: VantraProjectsAPI docs/shared-workspaces.md, section 8.

/// <summary>What every app shows of a workspace: the shared name, colour (<c>#rrggbb</c>), logo (a data URL) and time zone.</summary>
public sealed record SharedLook(string Name, string? Color, string? Logo, string? TimeZone);

/// <summary>A change to the look: null leaves a field as it is; <c>""</c> clears the colour, logo or time zone.</summary>
public sealed record SharedLookChange(string? Name = null, string? Color = null, string? Logo = null, string? TimeZone = null)
{
    public bool Empty => Name is null && Color is null && Logo is null && TimeZone is null;
}

/// <summary>
/// An app's own workspace becoming shared, with the same id: its look, its owner (the main owner), and its members with
/// their shared roles. Each of them gets access to that app, as they have it today.
/// </summary>
public sealed record SharedSeed(Guid LocalId, SharedLook Look, string Owner, IReadOnlyList<SharedChange> Members);

public enum SharedLinkResult
{
    /// <summary>The shared record was made and the app's workspace linked to it.</summary>
    Linked,
    /// <summary>The app's workspace was already linked (on or off).</summary>
    AlreadyLinked,
    /// <summary>A shared workspace with that id exists and this app isn't in it: left alone, for a person to decide.</summary>
    Taken,
}

/// <summary>One of this app's links, on or off.</summary>
public sealed record SharedLinkInfo(Guid SharedId, Guid LocalId, bool Enabled);

/// <summary>A shared workspace a person is in: its look, their role, its apps (on and off) and the apps they may use.</summary>
public sealed record SharedPlace(Guid Id, SharedLook Look, string PrimaryOwner, string Role, IReadOnlyList<SharedLink> Links, IReadOnlyList<string> MyApps)
{
    public bool HasApp(string app) => Links.Any(l => l.Enabled && l.App == app);
    public bool Has(string app) => MyApps.Contains(app);
    public SharedLink? Link(string app) => Links.FirstOrDefault(l => l.App == app);
    public IReadOnlyList<string> Apps => SharedApps.Order(Links.Where(l => l.Enabled).Select(l => l.App));
}

/// <summary>The codes stage 3 adds, the status each is sent as, and what it says.</summary>
public static class SharedSpaceCodes
{
    public const string AppOn = "app_on";
    public const string OnlyApp = "only_app";
    public const string Taken = "taken";
    public const string NotOwner = "owner_only";

    public static int StatusFor(string code) => code switch
    {
        NotOwner => StatusCodes.Status403Forbidden,
        _ => SharedCodes.StatusFor(code),
    };

    public static string MessageFor(string code, string app) => code switch
    {
        AppOn => $"This workspace already has {SharedApps.Name(app)}.",
        OnlyApp => "It's this workspace's only app. Delete the workspace instead.",
        Taken => "This workspace is being set up. Try again in a minute.",
        NotOwner => "Only an owner can do that.",
        SharedCodes.NotManager => $"Only owners and admins can add {SharedApps.Name(app)}.",
        SharedCodes.GuestFlow => SharedCodes.MessageFor(SharedCodes.GuestFlow),
        _ => SharedCodes.MessageFor(code),
    };

    public static SharedResult Error(string code, string app) =>
        new(StatusFor(code), new { error = MessageFor(code, app), code });
}

/// <summary>Who may switch an app on or off for a shared workspace. Pure, as <see cref="SharedRules"/>.</summary>
public static class SharedAppRules
{
    /// <summary>Adding an app (it starts that app's own trial or plan, as creating a workspace there does): owners and admins.</summary>
    public static string? CanAdd(SharedPlace? p, string app)
    {
        if (p is null) return SharedCodes.NotMember;
        if (!SharedApps.IsValid(app)) return SharedCodes.InvalidApp;
        if (!SharedRoles.CanManage(p.Role)) return SharedCodes.NotManager;
        if (p.HasApp(app)) return SharedSpaceCodes.AppOn;
        return null;
    }

    /// <summary>Switching an app off (its data is kept, and it can be switched on again): owners only, never the last app on.</summary>
    public static string? CanTurnOff(SharedPlace? p, string app)
    {
        if (p is null) return SharedCodes.NotMember;
        if (!SharedApps.IsValid(app)) return SharedCodes.InvalidApp;
        if (p.Role != SharedRoles.Owner) return SharedSpaceCodes.NotOwner;
        if (!p.HasApp(app)) return SharedCodes.AppOff;
        if (p.Apps.Count <= 1) return SharedSpaceCodes.OnlyApp;
        return null;
    }

    /// <summary>
    /// Why this person sees a shared workspace under "In other apps" instead of in this app's list (null: it's here):
    /// <c>app_off</c> (the workspace hasn't got the app), <c>guest_flow</c>, or <c>no_access</c> (ask an admin).
    /// </summary>
    public static string? Elsewhere(SharedPlace p, string app)
    {
        if (!p.HasApp(app)) return SharedCodes.AppOff;
        if (app == SharedApps.Flow && p.Role == SharedRoles.Guest) return SharedCodes.GuestFlow;
        return p.Has(app) ? null : "no_access";
    }

    /// <summary>The apps people get when an app is added: the person adding it, and the main owner (who keeps every app).</summary>
    public static IReadOnlyList<string> AddGrants(SharedPlace p, string by) =>
        new[] { by, p.PrimaryOwner }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The look as it may be stored: a name of 1–200, a <c>#rrggbb</c> colour, a small image data URL, a time zone id.</summary>
    public static SharedLook Clean(SharedLook look) => new(
        Clip(look.Name.Trim(), 200),
        CleanColor(look.Color),
        CleanLogo(look.Logo),
        string.IsNullOrWhiteSpace(look.TimeZone) || look.TimeZone.Trim().Length > 64 ? null : look.TimeZone.Trim());

    public static string? CleanColor(string? c)
    {
        var s = c?.Trim().ToLowerInvariant();
        return s is not null && Hex.IsMatch(s) ? s : null;
    }

    public static string? CleanLogo(string? l) =>
        l is { Length: > 0 and <= 100_000 } && l.StartsWith("data:image/", StringComparison.Ordinal) && !IsNoLogo(l) ? l : null;

    /// <summary>
    /// The shared logo once someone has removed it. A null logo means "never set" (links made before stage 3 left
    /// Projects' blob logos out), and then each app keeps its own and the first to have one fills it in; this one means
    /// "removed", and every app drops its copy. It passes the column's check (a data:image/ value).
    /// </summary>
    public const string NoLogo = "data:image/none,";

    public static bool IsNoLogo(string? l) => string.Equals(l, NoLogo, StringComparison.Ordinal);

    /// <summary>The logo to show: none for a removed one.</summary>
    public static string? Shown(string? l) => string.IsNullOrWhiteSpace(l) || IsNoLogo(l) ? null : l;

    private static readonly Regex Hex = new("^#[0-9a-f]{6}$", RegexOptions.Compiled);

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>Wire shapes for stage 3.</summary>
public sealed record SharedProductDto(string App, string Name, bool On, bool Here, bool Access);

/// <summary>
/// A workspace's products, for its settings: which apps it has, which the caller may use, and what they may do. With
/// <c>Shared</c> false (not linked yet, or the shared tables aren't there) only this app is listed, on.
/// </summary>
public sealed record SharedProductsDto(bool Shared, string? SharedId, string? Role, bool CanAdd, bool CanTurnOff, IReadOnlyList<SharedProductDto> Products,
    string? TimeZone = null);

/// <summary>A shared workspace the caller is in that isn't in this app's list, and why.</summary>
public sealed record SharedElsewhereDto(string Id, string Name, string? Color, string? Logo, string Role, IReadOnlyList<string> Apps, string Reason, bool CanAdd);

public sealed record SharedAddBody(string? SharedId);
public sealed record SharedProductBody(bool On);

/// <summary>The shared record itself, in <c>vantra.*</c> (db/shared/001 and 002, in VantraProjectsAPI).</summary>
public interface ISharedDirectory
{
    /// <summary>
    /// Make <paramref name="seed"/> shared with its own id, atomically and once: linked already (on or off) answers
    /// <see cref="SharedLinkResult.AlreadyLinked"/>, a shared workspace with that id <see cref="SharedLinkResult.Taken"/>.
    /// Two at once: the unique <c>UX_WorkspaceApps_Local</c> lets one through.
    /// </summary>
    Task<SharedLinkResult> LinkAsync(string app, SharedSeed seed, string linkReason, string addedVia);

    /// <summary>This app's links (on and off) for these local ids.</summary>
    Task<IReadOnlyList<SharedLinkInfo>> LinksAsync(string app, IReadOnlyCollection<Guid> localIds);

    /// <summary>Every shared workspace the person is in.</summary>
    Task<IReadOnlyList<SharedPlace>> PlacesAsync(string vantraId);

    Task<SharedPlace?> PlaceAsync(Guid sharedId, string vantraId);

    /// <summary>Change the look; null when there's no such workspace. A logo of <c>""</c> is stored as <see cref="SharedAppRules.NoLogo"/>.</summary>
    Task<SharedLook?> SetLookAsync(Guid sharedId, SharedLookChange change);

    /// <summary>Fill in a logo that was never set (from an app's own), unless someone set or removed one meanwhile.</summary>
    Task<bool> FillLogoAsync(Guid sharedId, string logo);

    /// <summary>
    /// Switch an app on (adding its link with <paramref name="localId"/>, or turning it back on) and give these people
    /// access to it; or off (the link stays, so its data is kept and it can come back).
    /// </summary>
    Task SetAppAsync(Guid sharedId, string app, Guid localId, bool on, string by, IReadOnlyList<string> grantTo);

    /// <summary>
    /// An app's workspace was deleted: its link, everyone's access to it and invitations' offers of it go. With
    /// <paramref name="deleteWhenEmpty"/>, the shared workspace goes too once no app is linked. True when it went.
    /// </summary>
    Task<bool> UnlinkAsync(Guid sharedId, string app, bool deleteWhenEmpty);
}

public sealed class SqlSharedDirectory(SqlConnectionFactory factory) : ISharedDirectory
{
    public async Task<SharedLinkResult> LinkAsync(string app, SharedSeed seed, string linkReason, string addedVia)
    {
        var look = SharedAppRules.Clean(seed.Look);
        if (look.Name.Length == 0 || string.IsNullOrWhiteSpace(seed.Owner)) return SharedLinkResult.Taken;
        using var conn = factory.Vantra();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            var linked = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM vantra.WorkspaceApps WITH (UPDLOCK, HOLDLOCK) WHERE App = @App AND LocalWorkspaceId = @Local;",
                new { App = app, Local = seed.LocalId }, tx);
            if (linked > 0) { tx.Rollback(); return SharedLinkResult.AlreadyLinked; }
            var taken = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM vantra.Workspaces WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", new { Id = seed.LocalId }, tx);
            if (taken > 0) { tx.Rollback(); return SharedLinkResult.Taken; }

            var owner = Clip(seed.Owner.Trim());
            await conn.ExecuteAsync(@"
INSERT INTO vantra.Workspaces (Id, Name, Color, Logo, TimeZone, OwnerVantraId, OriginApp)
VALUES (@Id, @Name, @Color, @Logo, @TimeZone, @Owner, @App);",
                new { Id = seed.LocalId, look.Name, look.Color, look.Logo, look.TimeZone, Owner = owner, App = app }, tx);
            var members = seed.Members
                .Where(m => !string.IsNullOrWhiteSpace(m.VantraId) && m.VantraId.Trim().Length <= 50 && SharedRoles.IsValid(m.Role))
                .GroupBy(m => m.VantraId.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (!members.Any(m => string.Equals(m.VantraId.Trim(), owner, StringComparison.OrdinalIgnoreCase)))
                members.Insert(0, new SharedChange(owner, SharedRoles.Owner, null));
            foreach (var m in members)
            {
                var id = m.VantraId.Trim();
                var primary = string.Equals(id, owner, StringComparison.OrdinalIgnoreCase);
                var role = primary ? SharedRoles.Owner : m.Role;
                var type = role == SharedRoles.Guest ? SharedRoles.CleanGuestType(m.GuestType) : null;
                await conn.ExecuteAsync(@"
INSERT INTO vantra.WorkspaceMembers (WorkspaceId, VantraId, Role, GuestType, AddedVia, UpdatedByVantraId)
VALUES (@Id, @VantraId, @Role, @GuestType, @Via, @Owner);",
                    new { Id = seed.LocalId, VantraId = id, Role = role, GuestType = type, Via = addedVia, Owner = owner }, tx);
                if (app == SharedApps.Flow && role == SharedRoles.Guest) continue;
                await conn.ExecuteAsync(@"
INSERT INTO vantra.WorkspaceMemberApps (WorkspaceId, VantraId, App, GrantedBy) VALUES (@Id, @VantraId, @App, @Owner);",
                    new { Id = seed.LocalId, VantraId = id, App = app, Owner = owner }, tx);
            }
            // Last: the link. The unique (App, LocalWorkspaceId) index lets only one of two at once through.
            await conn.ExecuteAsync(@"
INSERT INTO vantra.WorkspaceApps (WorkspaceId, App, LocalWorkspaceId, Enabled, LinkReason, LinkedByVantraId)
VALUES (@Id, @App, @Id, 1, @Reason, @Owner);",
                new { Id = seed.LocalId, App = app, Reason = linkReason, Owner = owner }, tx);
            tx.Commit();
            return SharedLinkResult.Linked;
        }
        catch (SqlException e) when (e.Number is 2601 or 2627)
        {
            try { tx.Rollback(); } catch (InvalidOperationException) { /* already rolled back */ }
            return SharedLinkResult.AlreadyLinked;
        }
    }

    public async Task<IReadOnlyList<SharedLinkInfo>> LinksAsync(string app, IReadOnlyCollection<Guid> localIds)
    {
        if (localIds.Count == 0) return [];
        var rows = new List<SharedLinkInfo>();
        using var conn = factory.Vantra();
        foreach (var chunk in localIds.Distinct().Chunk(SqlSharedStore.ChunkSize))
            rows.AddRange((await conn.QueryAsync<(Guid WorkspaceId, Guid LocalWorkspaceId, bool Enabled)>(
                    "SELECT WorkspaceId, LocalWorkspaceId, Enabled FROM vantra.WorkspaceApps WHERE App = @App AND LocalWorkspaceId IN @Ids;",
                    new { App = app, Ids = chunk }))
                .Select(r => new SharedLinkInfo(r.WorkspaceId, r.LocalWorkspaceId, r.Enabled)));
        return rows;
    }

    private const string PlacesSql = @"
SELECT w.Id, w.Name, w.Color, w.Logo, w.TimeZone, w.OwnerVantraId, m.Role
FROM vantra.WorkspaceMembers m JOIN vantra.Workspaces w ON w.Id = m.WorkspaceId
WHERE m.VantraId = @VantraId {0}
ORDER BY w.CreatedAt;
SELECT a.WorkspaceId, a.App, a.LocalWorkspaceId, a.Enabled
FROM vantra.WorkspaceApps a JOIN vantra.WorkspaceMembers m ON m.WorkspaceId = a.WorkspaceId AND m.VantraId = @VantraId
WHERE 1 = 1 {1};
SELECT ma.WorkspaceId, ma.App FROM vantra.WorkspaceMemberApps ma WHERE ma.VantraId = @VantraId {2};";

    public Task<IReadOnlyList<SharedPlace>> PlacesAsync(string vantraId) => ReadPlacesAsync(vantraId, null);

    public async Task<SharedPlace?> PlaceAsync(Guid sharedId, string vantraId) => (await ReadPlacesAsync(vantraId, sharedId)).FirstOrDefault();

    private async Task<IReadOnlyList<SharedPlace>> ReadPlacesAsync(string vantraId, Guid? only)
    {
        var sql = only is null ? string.Format(PlacesSql, "", "", "")
            : string.Format(PlacesSql, "AND w.Id = @Only", "AND a.WorkspaceId = @Only", "AND ma.WorkspaceId = @Only");
        using var conn = factory.Vantra();
        using var grid = await conn.QueryMultipleAsync(sql, new { VantraId = vantraId, Only = only });
        var ws = (await grid.ReadAsync<(Guid Id, string Name, string? Color, string? Logo, string? TimeZone, string OwnerVantraId, string Role)>()).ToList();
        var links = (await grid.ReadAsync<(Guid WorkspaceId, string App, Guid LocalWorkspaceId, bool Enabled)>()).ToList();
        var access = (await grid.ReadAsync<(Guid WorkspaceId, string App)>()).ToList();
        return ws.Select(w => new SharedPlace(w.Id, new SharedLook(w.Name, Blank(w.Color), SharedAppRules.Shown(w.Logo), Blank(w.TimeZone)), w.OwnerVantraId, w.Role,
                links.Where(l => l.WorkspaceId == w.Id && SharedApps.IsValid(l.App)).Select(l => new SharedLink(l.App, l.LocalWorkspaceId, l.Enabled)).ToList(),
                SharedApps.Order(access.Where(a => a.WorkspaceId == w.Id).Select(a => a.App))))
            .ToList();
    }

    public async Task<SharedLook?> SetLookAsync(Guid sharedId, SharedLookChange change)
    {
        using var conn = factory.Vantra();
        var row = await conn.QuerySingleOrDefaultAsync<(string Name, string? Color, string? Logo, string? TimeZone)>(@"
UPDATE vantra.Workspaces SET
    Name     = COALESCE(@Name, Name),
    Color    = CASE WHEN @Color IS NULL THEN Color ELSE NULLIF(@Color, N'') END,
    Logo     = CASE WHEN @Logo IS NULL THEN Logo ELSE NULLIF(@Logo, N'') END,
    TimeZone = CASE WHEN @TimeZone IS NULL THEN TimeZone ELSE NULLIF(@TimeZone, N'') END,
    UpdatedAt = SYSUTCDATETIME()
OUTPUT inserted.Name, inserted.Color, inserted.Logo, inserted.TimeZone
WHERE Id = @Id;",
            new { Id = sharedId, change.Name, change.Color, Logo = change.Logo == "" ? SharedAppRules.NoLogo : change.Logo, change.TimeZone });
        return row.Name is null ? null : new SharedLook(row.Name, Blank(row.Color), Blank(row.Logo), Blank(row.TimeZone));
    }

    public async Task<bool> FillLogoAsync(Guid sharedId, string logo)
    {
        if (SharedAppRules.CleanLogo(logo) is null) return false;
        using var conn = factory.Vantra();
        return await conn.ExecuteAsync(
            "UPDATE vantra.Workspaces SET Logo = @Logo, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id AND Logo IS NULL;",
            new { Id = sharedId, Logo = logo }) > 0;
    }

    public async Task SetAppAsync(Guid sharedId, string app, Guid localId, bool on, string by, IReadOnlyList<string> grantTo)
    {
        using var conn = factory.Vantra();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        await conn.ExecuteAsync("SELECT Id FROM vantra.Workspaces WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", new { Id = sharedId }, tx);
        var n = await conn.ExecuteAsync(@"
UPDATE vantra.WorkspaceApps SET Enabled = @On, UpdatedAt = SYSUTCDATETIME(), LinkedByVantraId = @By
 WHERE WorkspaceId = @Id AND App = @App;", new { Id = sharedId, App = app, On = on, By = Clip(by) }, tx);
        if (n == 0 && on)
            await conn.ExecuteAsync(@"
INSERT INTO vantra.WorkspaceApps (WorkspaceId, App, LocalWorkspaceId, Enabled, LinkReason, LinkedByVantraId)
VALUES (@Id, @App, @Local, 1, @Reason, @By);",
                new { Id = sharedId, App = app, Local = localId, Reason = localId == sharedId ? "same_id" : "manual", By = Clip(by) }, tx);
        if (on)
            foreach (var who in grantTo)
                await conn.ExecuteAsync(@"
INSERT INTO vantra.WorkspaceMemberApps (WorkspaceId, VantraId, App, GrantedBy)
SELECT @Id, m.VantraId, @App, @By FROM vantra.WorkspaceMembers m
WHERE m.WorkspaceId = @Id AND m.VantraId = @VantraId AND NOT (@App = N'flow' AND m.Role = N'guest')
  AND NOT EXISTS (SELECT 1 FROM vantra.WorkspaceMemberApps x WHERE x.WorkspaceId = @Id AND x.VantraId = m.VantraId AND x.App = @App);",
                    new { Id = sharedId, App = app, VantraId = who, By = Clip(by) }, tx);
        tx.Commit();
    }

    public async Task<bool> UnlinkAsync(Guid sharedId, string app, bool deleteWhenEmpty)
    {
        using var conn = factory.Vantra();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        await conn.ExecuteAsync("SELECT Id FROM vantra.Workspaces WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", new { Id = sharedId }, tx);
        await conn.ExecuteAsync(@"
DELETE FROM vantra.WorkspaceMemberApps WHERE WorkspaceId = @Id AND App = @App;
DELETE ia FROM vantra.WorkspaceInviteApps ia JOIN vantra.WorkspaceInvites i ON i.Id = ia.InviteId WHERE i.WorkspaceId = @Id AND ia.App = @App;
DELETE FROM vantra.WorkspaceApps WHERE WorkspaceId = @Id AND App = @App;", new { Id = sharedId, App = app }, tx);
        var deleted = deleteWhenEmpty && await conn.ExecuteAsync(@"
DELETE FROM vantra.Workspaces WHERE Id = @Id AND NOT EXISTS (SELECT 1 FROM vantra.WorkspaceApps WHERE WorkspaceId = @Id);",
            new { Id = sharedId }, tx) > 0;
        tx.Commit();
        return deleted;
    }

    private static string Clip(string s) => s.Length <= 50 ? s : s[..50];
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>
/// Stage 3 in one place for an app: whether the shared tables are there (stage 2's probe), linking the app's own
/// workspaces on first sight, which of them are switched off, "In other apps", and the products of a workspace.
/// Reads never fail a request (an error answers "not shared", as before); writes ask strictly and fail instead.
/// </summary>
public sealed class SharedSpaces(ISharedDirectory directory, SharedMembers members, ILogger<SharedSpaces> log, TimeProvider? clock = null)
{
    /// <summary>How long "switched off" (or "not off") is remembered for a local workspace.</summary>
    public static readonly TimeSpan OffTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long a workspace that couldn't be linked (its id taken, or an error) is left before trying again.</summary>
    public static readonly TimeSpan RetryLinkAfter = TimeSpan.FromMinutes(10);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, (bool Off, long Until)> off = new();
    private readonly ConcurrentDictionary<Guid, long> linkRetry = new();

    public ISharedDirectory Directory => directory;

    public Task<bool> ReadyAsync(bool strict = false) => members.ReadyAsync(strict);

    private long Now => clock.GetUtcNow().UtcTicks;

    /// <summary>Forget what's remembered about a local workspace (after it's switched on or off here).</summary>
    public void Forget(Guid localId) => off.TryRemove(localId, out _);

    /// <summary>The local workspaces among these whose link for <paramref name="app"/> is switched off. Never throws.</summary>
    public async Task<IReadOnlySet<Guid>> OffAsync(string app, IReadOnlyCollection<Guid> localIds)
    {
        var result = new HashSet<Guid>();
        var ask = new List<Guid>();
        var now = Now;
        foreach (var id in localIds.Distinct())
        {
            if (off.TryGetValue(id, out var c) && c.Until > now) { if (c.Off) result.Add(id); }
            else ask.Add(id);
        }
        if (ask.Count == 0) return result;
        try
        {
            if (!await members.ReadyAsync()) return result;
            var links = (await directory.LinksAsync(app, ask)).ToDictionary(l => l.LocalId);
            var until = clock.GetUtcNow().Add(OffTtl).UtcTicks;
            foreach (var id in ask)
            {
                var isOff = links.TryGetValue(id, out var l) && !l.Enabled;
                off[id] = (isOff, until);
                if (isOff) result.Add(id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't check which {App} workspaces are switched off; answering as before", app);
        }
        return result;
    }

    public async Task<bool> IsOffAsync(string app, Guid localId) =>
        localId != Guid.Empty && (await OffAsync(app, [localId])).Contains(localId);

    /// <summary>
    /// Every workspace becomes shared: each of these local workspaces that isn't linked yet gets a shared record with
    /// its own id (<paramref name="seed"/> says what it holds; null skips one). Idempotent and race-safe; never throws.
    /// Returns the ones it linked.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> EnsureLinkedAsync(string app, IReadOnlyCollection<Guid> localIds, Func<Guid, Task<SharedSeed?>> seed)
    {
        var linkedNow = new List<Guid>();
        if (localIds.Count == 0) return linkedNow;
        try
        {
            if (!await members.ReadyAsync()) return linkedNow;
            var now = Now;
            var candidates = localIds.Distinct().Where(id => id != Guid.Empty && !(linkRetry.TryGetValue(id, out var at) && at > now)).ToList();
            if (candidates.Count == 0) return linkedNow;
            var known = (await directory.LinksAsync(app, candidates)).Select(l => l.LocalId).ToHashSet();
            foreach (var id in candidates.Where(c => !known.Contains(c)))
            {
                try
                {
                    var s = await seed(id);
                    if (s is null) { linkRetry[id] = clock.GetUtcNow().Add(RetryLinkAfter).UtcTicks; continue; }
                    var r = await directory.LinkAsync(app, s, "origin", "app");
                    if (r == SharedLinkResult.Linked) linkedNow.Add(id);
                    else if (r == SharedLinkResult.Taken)
                    {
                        log.LogWarning("A {App} workspace's id is already a shared workspace's; it stays as it is until it's linked by hand", app);
                        linkRetry[id] = clock.GetUtcNow().Add(RetryLinkAfter).UtcTicks;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Couldn't make a {App} workspace shared; trying again later", app);
                    linkRetry[id] = clock.GetUtcNow().Add(RetryLinkAfter).UtcTicks;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't check {App}'s workspaces against the shared ones; trying again later", app);
        }
        return linkedNow;
    }

    /// <summary>The caller's shared workspaces, or none (an error, or no shared tables yet). Never throws.</summary>
    public async Task<IReadOnlyList<SharedPlace>> PlacesAsync(string vantraId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vantraId) || !await members.ReadyAsync()) return [];
            return await directory.PlacesAsync(vantraId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't read a person's shared workspaces; answering without them");
            return [];
        }
    }

    /// <summary>
    /// Fill in a never-set shared logo from an app's own, best effort (the first app to have one wins). Null logos and
    /// errors are ignored.
    /// </summary>
    public async Task FillLogoAsync(Guid sharedId, string? logo)
    {
        if (SharedAppRules.CleanLogo(logo) is not { } l) return;
        try { await directory.FillLogoAsync(sharedId, l); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Couldn't fill in a shared workspace's logo"); }
    }

    /// <summary>"In other apps": the caller's shared workspaces that aren't in this app's list, and why.</summary>
    public async Task<IReadOnlyList<SharedElsewhereDto>> ElsewhereAsync(string app, string vantraId)
    {
        var list = new List<SharedElsewhereDto>();
        foreach (var p in await PlacesAsync(vantraId))
        {
            if (SharedAppRules.Elsewhere(p, app) is not { } reason) continue;
            var canAdd = reason == SharedCodes.AppOff && SharedAppRules.CanAdd(p, app) is null;
            list.Add(new SharedElsewhereDto(p.Id.ToString(), p.Look.Name, p.Look.Color, p.Look.Logo, p.Role, p.Apps, reason, canAdd));
        }
        return list;
    }

    /// <summary>
    /// A workspace's products for its settings (this app's local id). Not linked, or no shared tables: just this app,
    /// on. Never throws.
    /// </summary>
    public async Task<SharedProductsDto> ProductsAsync(string app, Guid localId, string vantraId)
    {
        var alone = new SharedProductsDto(false, null, null, false, false, [new SharedProductDto(app, SharedApps.Name(app), true, true, true)]);
        try
        {
            if (!await members.ReadyAsync()) return alone;
            var link = (await directory.LinksAsync(app, [localId])).FirstOrDefault();
            if (link is null) return alone;
            var p = await directory.PlaceAsync(link.SharedId, vantraId);
            if (p is null) return alone;
            return new SharedProductsDto(true, p.Id.ToString(), p.Role, SharedRoles.CanManage(p.Role), SharedAppRules.CanTurnOff(p, app) is null,
                SharedApps.All.Select(a => new SharedProductDto(a, SharedApps.Name(a), p.HasApp(a), a == app, p.Has(a))).ToList(), p.Look.TimeZone);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't read a {App} workspace's products; answering with this app alone", app);
            return alone;
        }
    }

    /// <summary>The shared workspace of this app's local workspace (on or off), strictly: null when it isn't linked.</summary>
    public async Task<SharedLinkInfo?> LinkAsync(string app, Guid localId, bool strict = true)
    {
        try
        {
            if (!await members.ReadyAsync(strict)) return null;
            return (await directory.LinksAsync(app, [localId])).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SharedUnavailableException)
        {
            if (strict) throw new SharedUnavailableException("The shared workspace couldn't be read.", ex);
            log.LogWarning(ex, "Couldn't read a {App} workspace's link; answering as not shared", app);
            return null;
        }
    }

    /// <summary>
    /// After this app's workspace was deleted: remove its link (and the shared workspace when it was the last app and
    /// the caller is an owner of it). Best effort, logged; a link to a workspace that's gone is ignored by every app.
    /// </summary>
    public async Task AfterDeleteAsync(string app, Guid localId, string vantraId)
    {
        try
        {
            if (!await members.ReadyAsync()) return;
            var link = (await directory.LinksAsync(app, [localId])).FirstOrDefault();
            if (link is null) return;
            var p = await directory.PlaceAsync(link.SharedId, vantraId);
            await directory.UnlinkAsync(link.SharedId, app, deleteWhenEmpty: p?.Role == SharedRoles.Owner);
            Forget(localId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "A deleted {App} workspace's shared link couldn't be removed", app);
        }
    }
}

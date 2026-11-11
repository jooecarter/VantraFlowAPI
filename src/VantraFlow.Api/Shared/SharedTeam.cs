using System.Collections.Concurrent;
using System.Globalization;

namespace VantraFlow.Api.Shared;

/// <summary>
/// One app's own member list, kept in line with the shared one (the source of truth) so the app's existing permission
/// checks, queries and seat counts keep working unchanged. Each app maps the shared role to its own roles.
/// </summary>
public interface ISharedMirror
{
    string App { get; }

    /// <summary>
    /// Make this app's member list for the local workspace match the shared state: add who has access to this app,
    /// change roles, remove who doesn't, and set the owner. Returns anyone it couldn't give access to (Projects: no
    /// seat), whose access is then taken back in the shared record.
    /// </summary>
    Task<IReadOnlyList<string>> ApplyAsync(Guid localId, SharedState state, string? by);

    /// <summary>The local workspaces this person has a row in.</summary>
    Task<IReadOnlyList<Guid>> MembershipsAsync(string vantraId);
}

/// <summary>What a mirror has to do to one app's member list: who to add, whose role to change, who to remove.</summary>
public sealed record MirrorDiff(
    IReadOnlyList<(string VantraId, string Role)> Adds,
    IReadOnlyList<(string VantraId, string Role)> Changes,
    IReadOnlyList<string> Removals)
{
    public bool Empty => Adds.Count == 0 && Changes.Count == 0 && Removals.Count == 0;

    /// <summary>The difference between the app's list and the one it should have (VantraIds compared ignoring case).</summary>
    public static MirrorDiff Compute(IEnumerable<(string VantraId, string Role)> local, IEnumerable<(string VantraId, string Role)> desired)
    {
        var have = new Dictionary<string, (string VantraId, string Role)>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in local) have.TryAdd(l.VantraId, l);
        var want = new Dictionary<string, (string VantraId, string Role)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in desired) want.TryAdd(d.VantraId, d);
        var adds = want.Values.Where(d => !have.ContainsKey(d.VantraId)).ToList();
        var changes = want.Values.Where(d => have.TryGetValue(d.VantraId, out var h) && !string.Equals(h.Role, d.Role, StringComparison.Ordinal))
            .Select(d => (have[d.VantraId].VantraId, d.Role)).ToList();
        var removals = have.Values.Where(h => !want.ContainsKey(h.VantraId)).Select(h => h.VantraId).ToList();
        return new MirrorDiff(adds, changes, removals);
    }
}

/// <summary>
/// Keeps this app's own member lists in line with the shared record: right after every change made here, and — for
/// changes made in another app — when a workspace or a person is next seen (at most every <see cref="Every"/>).
/// </summary>
public sealed class SharedSync(SharedMembers shared, ISharedMirror mirror, ILogger<SharedSync> log, TimeProvider? clock = null)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, long> workspaceDue = new();
    private readonly ConcurrentDictionary<string, long> userDue = new(StringComparer.OrdinalIgnoreCase);

    public string App => mirror.App;

    public SharedMembers Shared => shared;

    /// <summary>The shared state of a local workspace (null: its members aren't shared), its own list brought in line first when due.</summary>
    public async Task<SharedState?> WorkspaceAsync(Guid localId, bool force = false, bool strict = false)
    {
        var state = await shared.ForLocalAsync(App, localId, strict);
        if (state is null) return null;
        if (force || Claim(workspaceDue, localId)) state = await MirrorAsync(state, null, strict);
        return state;
    }

    /// <summary>
    /// On a request in a workspace: bring its list in line when due (a change made in another app), never failing the
    /// request. Costs nothing between checks.
    /// </summary>
    public async Task TouchAsync(Guid localId)
    {
        if (localId == Guid.Empty || !Claim(workspaceDue, localId)) return;
        try
        {
            if (await shared.ForLocalAsync(App, localId) is { } state) await MirrorAsync(state, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't check a {App} workspace against the shared one; trying again later", App);
        }
    }

    /// <summary>
    /// Bring this app's own list in line with <paramref name="state"/>. Nothing happens unless the workspace's members
    /// are shared for this app and its main owner has access to it (so the app always keeps its owner).
    /// </summary>
    public async Task<SharedState> MirrorAsync(SharedState state, string? by, bool strict = false)
    {
        var local = state.LocalId(App);
        if (local is null || !state.MembersShared(App)) return state;
        if (state.Find(state.PrimaryOwner)?.Has(App) != true)
        {
            log.LogWarning("Shared workspace {Workspace}'s main owner has no {App} access; {App} keeps its own member list", state.WorkspaceId, App, App);
            return state;
        }
        try
        {
            var refused = await mirror.ApplyAsync(local.Value, state, by);
            workspaceDue[local.Value] = clock.GetUtcNow().Add(Every).UtcTicks;
            if (refused.Count == 0) return state;
            log.LogWarning("{App} couldn't give {Count} people the access they were given elsewhere; it was taken back", App, refused.Count);
            var withdrawn = await shared.Store.ApplyAsync(state.WorkspaceId, by ?? "", "app", s => SharedRules.Withdraw(s, refused, App));
            return withdrawn?.After ?? state;
        }
        catch (Exception ex) when (!strict && ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't bring {App}'s own member list in line with the shared one; trying again later", App);
            return state;
        }
    }

    /// <summary>A person's linked workspaces in this app, each brought in line when due (they joined, left or changed elsewhere).</summary>
    public async Task UserAsync(string vantraId)
    {
        if (string.IsNullOrWhiteSpace(vantraId) || !Claim(userDue, vantraId)) return;
        try
        {
            if (!await shared.ReadyAsync()) return;
            var links = await shared.Store.UserLinksAsync(App, vantraId);
            var mine = await mirror.MembershipsAsync(vantraId);
            var linkedMine = await shared.Store.LinkedAsync(App, mine);
            foreach (var local in links.Concat(linkedMine).Distinct())
                await TouchAsync(local);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't check a person's shared workspaces in {App}; trying again later", App);
        }
    }

    private bool Claim<TKey>(ConcurrentDictionary<TKey, long> due, TKey key) where TKey : notnull
    {
        var now = clock.GetUtcNow().UtcTicks;
        var next = clock.GetUtcNow().Add(Every).UtcTicks;
        while (true)
        {
            if (!due.TryGetValue(key, out var at))
            {
                if (due.TryAdd(key, next)) return true;
                continue;
            }
            if (at > now) return false;
            if (due.TryUpdate(key, next, at)) return true;
        }
    }
}

/// <summary>What only the app itself knows: names, its email, its link, and its seat checks.</summary>
public interface ISharedHost
{
    string App { get; }

    Task<IReadOnlyDictionary<string, SharedPerson>> PeopleAsync(IReadOnlyCollection<string> vantraIds);

    /// <summary>The VantraIds with this email in the identity database.</summary>
    Task<IReadOnlyList<string>> AccountsForEmailAsync(string email);

    /// <summary>
    /// This app's own seat check, exactly as its billing has it, for giving one more person of this kind access to
    /// it (only the app that counts seats checks anything). Null when there's room.
    /// </summary>
    Task<SharedResult?> SeatAsync(SharedState s, string role, string? guestType);

    /// <summary>Giving or taking away Vantra Projects from another app: done by Projects, which checks its seats.</summary>
    Task<SharedResult> RemoteAccessAsync(SharedState s, SharedActor actor, string target, bool grant);

    /// <summary>Projects' seat check for an invitation that gives Projects, asked from another app.</summary>
    Task<SharedResult?> RemoteSeatAsync(SharedState s, SharedActor actor, string role, string? guestType);

    string InviteLink(string? origin, string token);

    Task<bool> SendInviteAsync(SharedState s, SharedInviteRow invite, string link, SharedActor actor);
}

/// <summary>
/// The shared Members page's actions, the same in every app: the rules (<see cref="SharedRules"/>) decide, the shared
/// record is written first (a failure fails the request), then this app's own list is brought in line. Access to an
/// app that counts seats goes through that app's own seat check.
/// </summary>
public sealed class SharedTeam(SharedSync sync, ISharedHost host, ILogger<SharedTeam> log, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private ISharedStore Store => sync.Shared.Store;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public string App => host.App;

    public async Task<SharedTeamDto> TeamAsync(SharedState s, string me)
    {
        var mine = s.Find(me);
        var manage = SharedRoles.CanManage(mine?.Role);
        var invites = manage ? await Store.OpenInvitesAsync(s.WorkspaceId) : [];
        var people = await host.PeopleAsync(s.Members.Select(m => m.VantraId).Concat(invites.Select(i => i.InvitedByVantraId)).ToList());
        var members = s.Members
            .Select(m =>
            {
                var p = people.GetValueOrDefault(m.VantraId);
                return new SharedMemberDto(m.VantraId, p?.Name ?? m.VantraId, p?.Email, m.Role, m.GuestType, s.IsPrimary(m.VantraId),
                    SharedApps.Order(m.Apps.Where(s.HasApp)), Iso(m.JoinedAt));
            })
            .OrderBy(m => m.Primary ? 0 : RoleOrder(m.Role)).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new SharedTeamDto(s.WorkspaceId.ToString(), s.Name, App, s.Apps.Select(AppDto).ToList(),
            new SharedMeDto(mine?.VantraId ?? me, mine?.Role ?? SharedRoles.Guest, s.IsPrimary(me), manage),
            members, invites.Select(i => InviteDto(i, people, null)).ToList());
    }

    public async Task<SharedResult> InviteAsync(SharedState s, SharedActor actor, SharedInviteBody? body, string? origin)
    {
        var email = SharedInviteRules.NormalizeEmail(body?.Email);
        if (email is null) return SharedResult.Error(SharedCodes.InvalidEmail);
        var role = SharedRoles.Clean(body?.Role) ?? (string.IsNullOrWhiteSpace(body?.Role) ? SharedRoles.Member : "?");
        var guestType = role == SharedRoles.Guest ? SharedRoles.CleanGuestType(body?.GuestType) ?? SharedRoles.Client : null;
        var requested = (body?.Apps ?? [App]).ToList();
        if (requested.Any(a => SharedApps.Clean(a) is null)) return SharedResult.Error(SharedCodes.InvalidApp);
        var apps = SharedApps.Order(requested);
        if (SharedRules.CheckInvite(s, actor.VantraId, role, guestType, apps) is { } refused) return SharedResult.Error(refused);

        var accounts = await host.AccountsForEmailAsync(email);
        if (accounts.Any(a => s.Find(a) is not null))
            return SharedResult.Error(SharedCodes.AlreadyMember, $"{email} is already in {s.Name}.");
        if (await Store.InvitesIssuedSinceAsync(s.WorkspaceId, Now.Date) >= SharedInviteRules.DailyLimitPerWorkspace)
            return SharedResult.Error(SharedCodes.InviteLimit);

        // Projects counts seats: an invitation that gives it holds one, so its own check runs first.
        if (apps.Any(SharedApps.CountsSeats) && await SeatForAsync(s, actor, role, guestType) is { } full) return full;

        var token = SharedInviteRules.NewToken();
        var row = await Store.IssueInviteAsync(s.WorkspaceId, email, role, guestType, apps, SharedInviteRules.Hash(token), actor.VantraId, App,
            Now.AddDays(SharedInviteRules.ValidDays));
        var link = host.InviteLink(origin, token);
        row = await SendAsync(s, row, link, actor);
        await sync.MirrorAsync(s, actor.VantraId); // Projects holds the seat for it
        log.LogInformation("Shared workspace {Workspace} invited someone ({Role}; apps {Apps})", s.WorkspaceId, role, string.Join(",", apps));
        return new SharedResult(StatusCodes.Status201Created, InviteDto(row, new Dictionary<string, SharedPerson>(), link, actor.Name));
    }

    public async Task<SharedResult> ResendAsync(SharedState s, SharedActor actor, Guid inviteId, string? origin)
    {
        if (!SharedRoles.CanManage(s.Find(actor.VantraId)?.Role)) return SharedResult.Error(SharedCodes.NotManager);
        var row = await Store.InviteAsync(s.WorkspaceId, inviteId);
        if (row is null || !SharedInviteRules.IsOpen(row.Status)) return SharedResult.Error(SharedCodes.InviteInvalid);
        if (!SharedInviteRules.CanResend(row.SentAt, Now))
            return new SharedResult(StatusCodes.Status429TooManyRequests, new
            {
                error = "That invitation was just sent. Give it a minute.", code = SharedCodes.InviteLimit,
                retryAfter = SharedInviteRules.RetryAfterSeconds(row.SentAt, Now),
            });
        var token = SharedInviteRules.NewToken();
        row = await Store.RenewInviteAsync(s.WorkspaceId, inviteId, SharedInviteRules.Hash(token), Now.AddDays(SharedInviteRules.ValidDays));
        if (row is null) return SharedResult.Error(SharedCodes.InviteInvalid);
        var link = host.InviteLink(origin, token);
        row = await SendAsync(s, row, link, actor);
        var people = await host.PeopleAsync([row.InvitedByVantraId]);
        return SharedResult.Okay(InviteDto(row, people, link));
    }

    public async Task<SharedResult> RevokeInviteAsync(SharedState s, SharedActor actor, Guid inviteId)
    {
        if (!SharedRoles.CanManage(s.Find(actor.VantraId)?.Role)) return SharedResult.Error(SharedCodes.NotManager);
        if (!await Store.RevokeInviteAsync(s.WorkspaceId, inviteId) && await Store.InviteAsync(s.WorkspaceId, inviteId) is null)
            return SharedResult.Error(SharedCodes.InviteInvalid);
        await sync.MirrorAsync(s, actor.VantraId); // Projects lets its seat go
        return SharedResult.NoContent;
    }

    public Task<SharedResult> SetRoleAsync(SharedState s, SharedActor actor, string target, SharedRoleBody? body) =>
        ApplyAsync(s, actor, st => SharedRules.SetRole(st, actor.VantraId, target, SharedRoles.Clean(body?.Role), SharedRoles.CleanGuestType(body?.GuestType)));

    public Task<SharedResult> RemoveAsync(SharedState s, SharedActor actor, string target) =>
        string.Equals(target, actor.VantraId, StringComparison.OrdinalIgnoreCase)
            ? LeaveAsync(s, actor)
            : ApplyAsync(s, actor, st => SharedRules.Remove(st, actor.VantraId, target));

    public Task<SharedResult> LeaveAsync(SharedState s, SharedActor actor) =>
        ApplyAsync(s, actor, st => SharedRules.Leave(st, actor.VantraId));

    public Task<SharedResult> TransferAsync(SharedState s, SharedActor actor, string? target) =>
        ApplyAsync(s, actor, st => SharedRules.Transfer(st, actor.VantraId, target));

    public async Task<SharedResult> GrantAsync(SharedState s, SharedActor actor, string target, string? app)
    {
        var a = SharedApps.Clean(app);
        var check = SharedRules.Grant(s, actor.VantraId, target, a);
        if (!check.Ok) return SharedResult.Error(check.Error!);
        if (check.Grants.Count == 0) return SharedResult.NoContent;
        if (SharedApps.CountsSeats(a!))
        {
            if (a != App) return await host.RemoteAccessAsync(s, actor, check.Target!, grant: true);
            var member = s.Find(check.Target)!;
            if (await host.SeatAsync(s, member.Role, member.GuestType) is { } full) return full;
        }
        return await ApplyAsync(s, actor, st => SharedRules.Grant(st, actor.VantraId, target, a));
    }

    public async Task<SharedResult> RevokeAsync(SharedState s, SharedActor actor, string target, string? app)
    {
        var a = SharedApps.Clean(app);
        var check = SharedRules.Revoke(s, actor.VantraId, target, a);
        if (!check.Ok) return SharedResult.Error(check.Error!);
        if (check.Revokes.Count == 0) return SharedResult.NoContent;
        if (SharedApps.CountsSeats(a!) && a != App) return await host.RemoteAccessAsync(s, actor, check.Target!, grant: false);
        return await ApplyAsync(s, actor, st => SharedRules.Revoke(st, actor.VantraId, target, a));
    }

    /// <summary>Apply a change to the shared record (failing the request when it can't), then bring this app's list in line.</summary>
    public async Task<SharedResult> ApplyAsync(SharedState s, SharedActor actor, Func<SharedState, SharedPlan> decide)
    {
        var done = await Store.ApplyAsync(s.WorkspaceId, actor.VantraId, "app", decide);
        if (done is null) return SharedResult.Error(SharedCodes.NotShared);
        var (plan, after) = done.Value;
        if (!plan.Ok) return SharedResult.Error(plan.Error!);
        await sync.MirrorAsync(after, actor.VantraId);
        return SharedResult.NoContent;
    }

    // ── Opening an invitation ──

    public async Task<SharedResult> PreviewAsync(string? token)
    {
        if (!SharedInviteRules.IsTokenShape(token) || !await sync.Shared.ReadyAsync()) return Invalid();
        var r = await Store.InviteByTokenAsync(SharedInviteRules.Hash(token!));
        if (r is null) return Invalid();
        var outcome = SharedInviteRules.Preview(r.Status, r.ExpiresAt, Now);
        if (outcome != SharedInviteOutcome.Accept) return Closed(outcome, r);
        var inviter = (await host.PeopleAsync([r.InvitedByVantraId])).GetValueOrDefault(r.InvitedByVantraId)?.Name;
        return SharedResult.Okay(new SharedInvitePreviewDto(r.WorkspaceName ?? "", r.WorkspaceColor, r.WorkspaceLogo, r.Role, r.GuestType,
            r.Apps.Select(AppDto).ToList(), inviter, SharedInviteRules.EmailHint(r.Email), Iso(r.ExpiresAt)));
    }

    /// <summary>
    /// Join by an invitation from any app: added to the shared workspace with the apps it gives, and this app's own
    /// list brought in line now (the other apps catch up when they next see the workspace or the person).
    /// </summary>
    public async Task<(SharedResult Result, SharedState? After)> AcceptAsync(string? token, string vantraId, string? accountEmail)
    {
        if (!SharedInviteRules.IsTokenShape(token) || !await sync.Shared.ReadyAsync()) return (Invalid(), null);
        var r = await Store.AcceptInviteAsync(SharedInviteRules.Hash(token!), vantraId, accountEmail, Now);
        if (r is not { } res) return (Invalid(), null);
        var ws = res.Invite.WorkspaceName ?? "";
        switch (res.Outcome)
        {
            case SharedInviteOutcome.Accept:
            case SharedInviteOutcome.AlreadyMember:
                var after = res.After;
                if (after is not null) after = await sync.MirrorAsync(after, vantraId);
                var me = after?.Find(vantraId);
                if (res.Outcome == SharedInviteOutcome.Accept)
                    log.LogInformation("Someone joined shared workspace {Workspace} by invitation", res.Invite.WorkspaceId);
                return (SharedResult.Okay(new SharedAcceptDto(res.Invite.WorkspaceId.ToString(), ws, after?.LocalId(App),
                    res.Outcome == SharedInviteOutcome.AlreadyMember, SharedApps.Order((me?.Apps ?? []).Where(a => after!.HasApp(a))), res.Invite.Apps)), after);
            case SharedInviteOutcome.WrongAccount:
                return (new SharedResult(StatusCodes.Status409Conflict, new
                {
                    error = $"This invitation was sent to {res.Invite.Email}. Sign in as that address to join {ws}.",
                    code = "wrong_account", email = res.Invite.Email, workspace = ws,
                }), null);
            default:
                return (Closed(res.Outcome, res.Invite), null);
        }
    }

    // ── Helpers ──

    private async Task<SharedResult?> SeatForAsync(SharedState s, SharedActor actor, string role, string? guestType) =>
        App == SharedApps.Projects ? await host.SeatAsync(s, role, guestType) : await host.RemoteSeatAsync(s, actor, role, guestType);

    private async Task<SharedInviteRow> SendAsync(SharedState s, SharedInviteRow row, string link, SharedActor actor)
    {
        try
        {
            if (await host.SendInviteAsync(s, row, link, actor)) return await Store.MarkInviteSentAsync(row.Id) ?? row;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't email a shared invitation; it stays pending with its link to copy");
        }
        return row;
    }

    private static SharedResult Invalid() => SharedResult.Error(SharedCodes.InviteInvalid, "This invitation link isn't valid.");

    private static SharedResult Closed(SharedInviteOutcome outcome, SharedInviteRow r)
    {
        var ws = r.WorkspaceName ?? "the workspace";
        return outcome switch
        {
            SharedInviteOutcome.Expired => new(StatusCodes.Status410Gone, new { error = $"This invitation to {ws} has expired. Ask for a new one.", code = "invite_expired", workspace = ws }),
            SharedInviteOutcome.Revoked => new(StatusCodes.Status410Gone, new { error = $"This invitation to {ws} was withdrawn.", code = "invite_revoked", workspace = ws }),
            SharedInviteOutcome.Used => new(StatusCodes.Status410Gone, new { error = $"This invitation to {ws} has already been used.", code = "invite_used", workspace = ws }),
            _ => Invalid(),
        };
    }

    private SharedInviteDto InviteDto(SharedInviteRow r, IReadOnlyDictionary<string, SharedPerson> people, string? link, string? byName = null) => new(
        r.Id.ToString(), r.Email, r.Role, r.GuestType, r.Apps, SharedInviteRules.ListStatus(r.Status, r.ExpiresAt, Now),
        Iso(r.CreatedAt), Iso(r.ExpiresAt), r.SentAt is { } sent ? Iso(sent) : null,
        byName ?? people.GetValueOrDefault(r.InvitedByVantraId)?.Name, link);

    public static SharedAppDto AppDto(string app) => new(app, SharedApps.Name(app));

    private static int RoleOrder(string role) => role switch
    {
        SharedRoles.Owner => 1, SharedRoles.Admin => 2, SharedRoles.Member => 3, _ => 4,
    };

    public static string Iso(DateTime d) =>
        DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

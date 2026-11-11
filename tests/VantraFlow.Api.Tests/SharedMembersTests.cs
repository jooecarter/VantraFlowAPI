using Microsoft.Extensions.Logging.Abstractions;
using VantraFlow.Api.Shared;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>
/// Shared Vantra workspaces, stage 2: one role per person, per-app access (each app sold separately), and the rules
/// every app enforces on the server. The same tests are in each API.
/// </summary>
public sealed class SharedMembersTests
{
    private static readonly Guid Ws = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProjectsWs = Ws;
    private static readonly Guid OrbitWs = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid FlowWs = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static SharedMember M(string id, string role, params string[] apps) =>
        new(id, role, role == SharedRoles.Guest ? SharedRoles.Client : null, new DateTime(2026, 1, 1), apps);

    /// <summary>Ann owns it (main owner, every app); Bob is a second owner; Ada an admin; Max a member with Projects only; Gus a client.</summary>
    private static SharedState State(params SharedMember[] extra)
    {
        var members = new List<SharedMember>
        {
            M("ann", SharedRoles.Owner, SharedApps.Projects, SharedApps.Orbit, SharedApps.Flow),
            M("bob", SharedRoles.Owner, SharedApps.Projects, SharedApps.Orbit),
            M("ada", SharedRoles.Admin, SharedApps.Projects, SharedApps.Orbit, SharedApps.Flow),
            M("max", SharedRoles.Member, SharedApps.Projects),
            M("gus", SharedRoles.Guest, SharedApps.Projects),
        };
        members.AddRange(extra);
        return new SharedState(Ws, "Example Co", "ann",
            [new(SharedApps.Projects, ProjectsWs, true), new(SharedApps.Orbit, OrbitWs, true), new(SharedApps.Flow, FlowWs, true)], members);
    }

    // ── Rules ──

    [Fact]
    public void Admins_cant_touch_owners()
    {
        var s = State();
        Assert.Equal(SharedCodes.OwnerOnly, SharedRules.SetRole(s, "ada", "bob", SharedRoles.Member, null).Error);
        Assert.Equal(SharedCodes.OwnerOnly, SharedRules.SetRole(s, "ada", "max", SharedRoles.Owner, null).Error);
        Assert.Equal(SharedCodes.OwnerOnly, SharedRules.Remove(s, "ada", "bob").Error);
        Assert.Equal(SharedCodes.OwnerOnly, SharedRules.Grant(s, "ada", "bob", SharedApps.Flow).Error);
        Assert.Equal(SharedCodes.OwnerOnly, SharedRules.Revoke(s, "ada", "bob", SharedApps.Orbit).Error);
        Assert.True(SharedRules.SetRole(s, "ann", "bob", SharedRoles.Admin, null).Ok);
    }

    [Fact]
    public void Members_and_guests_cant_manage()
    {
        var s = State();
        Assert.Equal(SharedCodes.NotManager, SharedRules.SetRole(s, "max", "gus", SharedRoles.Guest, SharedRoles.Vendor).Error);
        Assert.Equal(SharedCodes.NotManager, SharedRules.Grant(s, "gus", "max", SharedApps.Orbit).Error);
        Assert.Equal(SharedCodes.NotManager, SharedRules.CheckInvite(s, "max", SharedRoles.Member, null, [SharedApps.Projects]));
        Assert.Equal(SharedCodes.NotMember, SharedRules.Remove(s, "nobody", "max").Error);
    }

    [Fact]
    public void Guests_stay_guests_either_way()
    {
        var s = State();
        Assert.Equal(SharedCodes.GuestChange, SharedRules.SetRole(s, "ann", "gus", SharedRoles.Member, null).Error);
        Assert.Equal(SharedCodes.GuestChange, SharedRules.SetRole(s, "ann", "max", SharedRoles.Guest, null).Error);
        var vendor = SharedRules.SetRole(s, "ada", "gus", SharedRoles.Guest, SharedRoles.Vendor);
        Assert.True(vendor.Ok);
        Assert.Equal(SharedRoles.Vendor, Assert.Single(vendor.Changes).GuestType);
    }

    [Fact]
    public void Guests_get_no_flow()
    {
        var s = State();
        Assert.Equal(SharedCodes.GuestFlow, SharedRules.Grant(s, "ann", "gus", SharedApps.Flow).Error);
        Assert.Equal(SharedCodes.GuestFlow, SharedRules.CheckInvite(s, "ada", SharedRoles.Guest, SharedRoles.Client, [SharedApps.Projects, SharedApps.Flow]));
        var joined = SharedRules.Join(s, "new", SharedRoles.Guest, SharedRoles.Vendor, [SharedApps.Orbit, SharedApps.Flow]);
        Assert.Equal([SharedApps.Orbit], joined.Grants.Select(g => g.App));
    }

    [Fact]
    public void The_main_owner_keeps_every_app_and_cant_go()
    {
        var s = State();
        Assert.Equal(SharedCodes.PrimaryOwner, SharedRules.Revoke(s, "bob", "ann", SharedApps.Orbit).Error);
        Assert.Equal(SharedCodes.PrimaryOwner, SharedRules.Remove(s, "bob", "ann").Error);
        Assert.Equal(SharedCodes.PrimaryOwner, SharedRules.Leave(s, "ann").Error);
        Assert.Equal(SharedCodes.PrimaryOwner, SharedRules.SetRole(s, "bob", "ann", SharedRoles.Admin, null).Error);
        Assert.True(SharedRules.Leave(s, "bob").Ok);
    }

    [Fact]
    public void There_is_always_an_owner()
    {
        var alone = new SharedState(Ws, "Example Co", "ann", [new(SharedApps.Projects, ProjectsWs, true)],
            [M("ann", SharedRoles.Owner, SharedApps.Projects), M("ada", SharedRoles.Admin, SharedApps.Projects)]);
        Assert.False(SharedRules.Leave(alone, "ann").Ok);
        Assert.False(SharedRules.SetRole(alone, "ann", "ann", SharedRoles.Admin, null).Ok);
        Assert.Equal(SharedCodes.TargetApps, SharedRules.Transfer(State(), "ann", "bob").Error);
        var handed = SharedRules.Transfer(alone, "ann", "ada");
        Assert.True(handed.Ok);
        Assert.Equal("ada", handed.PrimaryOwner);
        Assert.Contains(handed.Changes, c => c.VantraId == "ada" && c.Role == SharedRoles.Owner);
        Assert.Contains(handed.Changes, c => c.VantraId == "ann" && c.Role == SharedRoles.Admin);
    }

    [Fact]
    public void Handing_over_takes_the_main_owner_and_a_teammate_with_every_app()
    {
        var s = State();
        Assert.Equal(SharedCodes.NotPrimary, SharedRules.Transfer(s, "bob", "ada").Error);
        Assert.Equal(SharedCodes.TargetGuest, SharedRules.Transfer(s, "ann", "gus").Error);
        Assert.Equal(SharedCodes.TargetApps, SharedRules.Transfer(s, "ann", "max").Error);
        Assert.True(SharedRules.Transfer(s, "ann", "ada").Ok);
    }

    [Fact]
    public void Access_is_only_to_apps_the_workspace_has()
    {
        var s = State() with { Links = [new(SharedApps.Projects, ProjectsWs, true), new(SharedApps.Orbit, OrbitWs, false)] };
        Assert.Equal(SharedCodes.AppOff, SharedRules.Grant(s, "ann", "max", SharedApps.Orbit).Error);
        Assert.Equal(SharedCodes.AppOff, SharedRules.CheckInvite(s, "ann", SharedRoles.Member, null, [SharedApps.Flow]));
        Assert.Equal(SharedCodes.InvalidApp, SharedRules.Grant(s, "ann", "max", "notes").Error);
        Assert.Equal(SharedCodes.NoApps, SharedRules.CheckInvite(s, "ann", SharedRoles.Member, null, []));
        Assert.Equal([SharedApps.Projects], s.Apps);
    }

    [Fact]
    public void Nobody_removes_their_own_access_or_role()
    {
        var s = State();
        Assert.Equal(SharedCodes.SelfChange, SharedRules.Revoke(s, "ada", "ada", SharedApps.Orbit).Error);
        Assert.Equal(SharedCodes.SelfChange, SharedRules.SetRole(s, "ada", "ada", SharedRoles.Member, null).Error);
        Assert.Equal(SharedCodes.Self, SharedRules.Remove(s, "ada", "ada").Error);
        Assert.True(SharedRules.Grant(s, "bob", "bob", SharedApps.Flow).Ok);
    }

    [Fact]
    public void Joining_keeps_an_existing_role_and_only_adds_apps()
    {
        var s = State();
        var again = SharedRules.Join(s, "MAX", SharedRoles.Admin, null, [SharedApps.Projects, SharedApps.Orbit]);
        Assert.Empty(again.Adds);
        Assert.Equal([new SharedAccess("max", SharedApps.Orbit)], again.Grants);
        var fresh = SharedRules.Join(s, "new", SharedRoles.Member, SharedRoles.Client, [SharedApps.Orbit]);
        Assert.Equal(new SharedChange("new", SharedRoles.Member, null), Assert.Single(fresh.Adds));
    }

    [Fact]
    public void Stage_two_applies_to_an_app_once_someone_has_access_to_it()
    {
        var s = State();
        Assert.True(s.MembersShared(SharedApps.Flow));
        var none = s with { Members = s.Members.Select(m => m with { Apps = m.Apps.Where(a => a != SharedApps.Flow).ToList() }).ToList() };
        Assert.False(none.MembersShared(SharedApps.Flow));
        Assert.True(none.MembersShared(SharedApps.Projects));
    }

    // ── Mirror ──

    [Fact]
    public void The_mirror_diff_ignores_case_and_finds_adds_changes_and_removals()
    {
        var d = MirrorDiff.Compute([("ANN", "owner"), ("bob", "member"), ("old", "member")], [("ann", "owner"), ("bob", "admin"), ("new", "member")]);
        Assert.Equal([("new", "member")], d.Adds);
        Assert.Equal([("bob", "admin")], d.Changes);
        Assert.Equal(["old"], d.Removals);
        Assert.True(MirrorDiff.Compute([("a", "x")], [("A", "x")]).Empty);
    }

    // ── Invitations ──

    [Fact]
    public void Invitation_tokens_are_random_and_only_their_hash_is_kept()
    {
        var a = SharedInviteRules.NewToken();
        Assert.True(SharedInviteRules.IsTokenShape(a));
        Assert.NotEqual(a, SharedInviteRules.NewToken());
        Assert.Equal(32, SharedInviteRules.Hash(a).Length);
        Assert.False(SharedInviteRules.IsTokenShape(Guid.NewGuid().ToString()));
        Assert.Equal(SharedInviteOutcome.WrongAccount,
            SharedInviteRules.Evaluate("sent", DateTime.UtcNow.AddDays(1), DateTime.UtcNow, "a@example.com", "b@example.com", false));
        Assert.Equal(SharedInviteOutcome.Expired,
            SharedInviteRules.Evaluate("sent", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, "a@example.com", "A@example.com", false));
    }

    // ── Probe, sync and the team service, against fakes ──

    private sealed class FakeStore : ISharedStore
    {
        public bool Ready = true;
        public Exception? ProbeThrows;
        public SharedState? State;
        public readonly List<SharedInviteRow> Invites = [];
        public int Applies;

        public Task<bool> ProbeAsync() => ProbeThrows is { } e ? Task.FromException<bool>(e) : Task.FromResult(Ready);

        public Task<Guid?> SharedIdAsync(string app, Guid localId) =>
            Task.FromResult(State?.LocalId(app) == localId ? State.WorkspaceId : (Guid?)null);

        public Task<IReadOnlyList<Guid>> LinkedAsync(string app, IReadOnlyCollection<Guid> localIds) =>
            Task.FromResult<IReadOnlyList<Guid>>(localIds.Where(id => State?.LocalId(app) == id).ToList());

        public Task<IReadOnlyList<Guid>> UserLinksAsync(string app, string vantraId) =>
            Task.FromResult<IReadOnlyList<Guid>>(State?.Find(vantraId) is not null && State.LocalId(app) is { } l ? [l] : []);

        public Task<SharedState?> StateAsync(Guid sharedId) => Task.FromResult(State);

        public Task<(SharedPlan Plan, SharedState After)?> ApplyAsync(Guid sharedId, string by, string addedVia, Func<SharedState, SharedPlan> decide)
        {
            if (State is null) return Task.FromResult<(SharedPlan, SharedState)?>(null);
            var plan = decide(State);
            if (plan.Ok)
            {
                Applies++;
                var members = State.Members.ToList();
                foreach (var a in plan.Adds) members.Add(new SharedMember(a.VantraId, a.Role, a.GuestType, DateTime.UtcNow, []));
                members = members.Where(m => !plan.Removals.Contains(m.VantraId, StringComparer.OrdinalIgnoreCase))
                    .Select(m =>
                    {
                        var c = plan.Changes.FirstOrDefault(x => string.Equals(x.VantraId, m.VantraId, StringComparison.OrdinalIgnoreCase));
                        var apps = m.Apps.Concat(plan.Grants.Where(g => g.VantraId == m.VantraId).Select(g => g.App))
                            .Except(plan.Revokes.Where(r => r.VantraId == m.VantraId).Select(r => r.App)).Distinct().ToList();
                        return m with { Role = c?.Role ?? m.Role, GuestType = c is null ? m.GuestType : c.GuestType, Apps = apps };
                    }).ToList();
                State = State with { Members = members, PrimaryOwner = plan.PrimaryOwner };
            }
            return Task.FromResult<(SharedPlan, SharedState)?>((plan, State));
        }

        public Task<IReadOnlyList<SharedInviteRow>> OpenInvitesAsync(Guid sharedId) =>
            Task.FromResult<IReadOnlyList<SharedInviteRow>>(Invites.Where(i => SharedInviteRules.IsOpen(i.Status)).ToList());
        public Task<SharedInviteRow?> InviteAsync(Guid sharedId, Guid inviteId) => Task.FromResult(Invites.FirstOrDefault(i => i.Id == inviteId));
        public Task<IReadOnlyList<Guid>> InviteIdsAsync(Guid sharedId) => Task.FromResult<IReadOnlyList<Guid>>(Invites.Select(i => i.Id).ToList());
        public Task<SharedInviteRow?> InviteByTokenAsync(byte[] tokenHash) => Task.FromResult<SharedInviteRow?>(null);
        public Task<int> InvitesIssuedSinceAsync(Guid sharedId, DateTime sinceUtc) => Task.FromResult(Invites.Count);

        public Task<SharedInviteRow> IssueInviteAsync(Guid sharedId, string email, string role, string? guestType, IReadOnlyList<string> apps,
            byte[] tokenHash, string by, string fromApp, DateTime expiresAt)
        {
            var row = new SharedInviteRow
            {
                Id = Guid.NewGuid(), WorkspaceId = sharedId, Email = email, Role = role, GuestType = guestType, Apps = apps, Status = "pending",
                InvitedByVantraId = by, InvitedFromApp = fromApp, CreatedAt = DateTime.UtcNow, ExpiresAt = expiresAt,
            };
            Invites.Add(row);
            return Task.FromResult(row);
        }

        public Task<SharedInviteRow?> RenewInviteAsync(Guid sharedId, Guid inviteId, byte[] tokenHash, DateTime expiresAt) => InviteAsync(sharedId, inviteId);
        public Task<SharedInviteRow?> MarkInviteSentAsync(Guid inviteId) => InviteAsync(Ws, inviteId);
        public Task<bool> RevokeInviteAsync(Guid sharedId, Guid inviteId) => Task.FromResult(Invites.RemoveAll(i => i.Id == inviteId) > 0);

        public Task<(SharedInviteOutcome Outcome, SharedInviteRow Invite, SharedState? After)?> AcceptInviteAsync(byte[] tokenHash, string vantraId,
            string? accountEmail, DateTime nowUtc) => Task.FromResult<(SharedInviteOutcome, SharedInviteRow, SharedState?)?>(null);
    }

    private sealed class FakeMirror(string app) : ISharedMirror
    {
        public string App => app;
        public List<IReadOnlyList<string>> Applied = [];
        public List<string> Refuse = [];

        public Task<IReadOnlyList<string>> ApplyAsync(Guid localId, SharedState state, string? by)
        {
            Applied.Add(state.Members.Where(m => m.Has(app)).Select(m => m.VantraId).ToList());
            return Task.FromResult<IReadOnlyList<string>>(Refuse.ToList());
        }

        public Task<IReadOnlyList<Guid>> MembershipsAsync(string vantraId) => Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class FakeHost(string app) : ISharedHost
    {
        public string App => app;
        public SharedResult? Seat;
        public SharedResult Remote = SharedResult.NoContent;
        public readonly List<string> Calls = [];

        public Task<IReadOnlyDictionary<string, SharedPerson>> PeopleAsync(IReadOnlyCollection<string> vantraIds) =>
            Task.FromResult<IReadOnlyDictionary<string, SharedPerson>>(vantraIds.Distinct().ToDictionary(id => id, id => new SharedPerson(id, $"{id}@example.com")));
        public Task<IReadOnlyList<string>> AccountsForEmailAsync(string email) =>
            Task.FromResult<IReadOnlyList<string>>(email == "max@example.com" ? ["max"] : []);
        public Task<SharedResult?> SeatAsync(SharedState s, string role, string? guestType) { Calls.Add("seat"); return Task.FromResult(Seat); }
        public Task<SharedResult> RemoteAccessAsync(SharedState s, SharedActor actor, string target, bool grant) { Calls.Add($"remote:{target}:{grant}"); return Task.FromResult(Remote); }
        public Task<SharedResult?> RemoteSeatAsync(SharedState s, SharedActor actor, string role, string? guestType) { Calls.Add("remote-seat"); return Task.FromResult(Seat); }
        public string InviteLink(string? origin, string token) => $"https://app.example.com/invite/{token}";
        public Task<bool> SendInviteAsync(SharedState s, SharedInviteRow invite, string link, SharedActor actor) { Calls.Add("email"); return Task.FromResult(true); }
    }

    private static (FakeStore Store, FakeMirror Mirror, SharedSync Sync) Wire(string app, SharedState? state)
    {
        var store = new FakeStore { State = state };
        var mirror = new FakeMirror(app);
        var sync = new SharedSync(new SharedMembers(store, NullLogger<SharedMembers>.Instance), mirror, NullLogger<SharedSync>.Instance);
        return (store, mirror, sync);
    }

    private static readonly SharedActor Ann = new("ann", "Ann", "ann@example.com");
    private static readonly SharedActor Ada = new("ada", "Ada", "ada@example.com");

    [Fact]
    public async Task Without_the_tables_or_any_access_a_workspace_isnt_shared()
    {
        var (store, _, sync) = Wire(SharedApps.Flow, State());
        store.Ready = false;
        Assert.Null(await sync.WorkspaceAsync(FlowWs));

        var (store2, _, sync2) = Wire(SharedApps.Flow, State());
        store2.ProbeThrows = new InvalidOperationException("down");
        Assert.Null(await sync2.WorkspaceAsync(FlowWs));
        await Assert.ThrowsAsync<SharedUnavailableException>(() => sync2.WorkspaceAsync(FlowWs, strict: true));

        var noFlow = State();
        noFlow = noFlow with { Members = noFlow.Members.Select(m => m with { Apps = m.Apps.Where(a => a != SharedApps.Flow).ToList() }).ToList() };
        var (_, mirror3, sync3) = Wire(SharedApps.Flow, noFlow);
        Assert.Null(await sync3.WorkspaceAsync(FlowWs, force: true));
        Assert.Empty(mirror3.Applied);
        Assert.Null(await sync3.WorkspaceAsync(Guid.NewGuid(), force: true));
    }

    [Fact]
    public async Task The_mirror_waits_while_the_main_owner_lacks_the_app()
    {
        var s = State();
        s = s with { Members = s.Members.Select(m => m.VantraId == "ann" ? m with { Apps = [SharedApps.Projects] } : m).ToList() };
        var (_, mirror, sync) = Wire(SharedApps.Orbit, s);
        await sync.MirrorAsync(s, "ann");
        Assert.Empty(mirror.Applied);
    }

    [Fact]
    public async Task Access_an_app_couldnt_give_is_taken_back()
    {
        var (store, mirror, sync) = Wire(SharedApps.Projects, State());
        mirror.Refuse.Add("max");
        var after = await sync.MirrorAsync(store.State!, "ann");
        Assert.False(after.Find("max")!.Has(SharedApps.Projects));
        Assert.True(after.Find("ann")!.Has(SharedApps.Projects));
    }

    [Fact]
    public async Task Changes_write_the_shared_record_then_mirror_this_app()
    {
        var (store, mirror, sync) = Wire(SharedApps.Flow, State());
        var team = new SharedTeam(sync, new FakeHost(SharedApps.Flow), NullLogger<SharedTeam>.Instance);
        var r = await team.GrantAsync(store.State!, Ann, "bob", SharedApps.Flow);
        Assert.Equal(204, r.Status);
        Assert.True(store.State!.Find("bob")!.Has(SharedApps.Flow));
        Assert.Contains("bob", mirror.Applied.Last());

        var refused = await team.RemoveAsync(store.State!, Ada, "bob");
        Assert.Equal(403, refused.Status);
        Assert.NotNull(store.State!.Find("bob"));
    }

    [Fact]
    public async Task Projects_access_is_given_by_projects_with_its_seat_check()
    {
        // From another app: Projects is asked, and its answer (no seat) is passed on as it is.
        var (store, _, sync) = Wire(SharedApps.Flow, State());
        var host = new FakeHost(SharedApps.Flow) { Remote = new SharedResult(402, new { error = "All 3 of your seats are taken.", code = "seats_full" }) };
        var team = new SharedTeam(sync, host, NullLogger<SharedTeam>.Instance);
        var r = await team.GrantAsync(store.State!, Ann, "bob", SharedApps.Orbit);
        Assert.Equal(204, r.Status);
        var add = State(M("eve", SharedRoles.Member, SharedApps.Orbit));
        store.State = add;
        r = await team.GrantAsync(add, Ann, "eve", SharedApps.Projects);
        Assert.Equal(402, r.Status);
        Assert.Contains("remote:eve:True", host.Calls);
        Assert.False(store.State!.Find("eve")!.Has(SharedApps.Projects));

        // In Projects itself: its own check runs before anything is written.
        var (pStore, _, pSync) = Wire(SharedApps.Projects, State(M("eve", SharedRoles.Member, SharedApps.Orbit)));
        var pHost = new FakeHost(SharedApps.Projects) { Seat = SharedResult.Error(SharedCodes.SeatsFull) };
        var pTeam = new SharedTeam(pSync, pHost, NullLogger<SharedTeam>.Instance);
        Assert.Equal(402, (await pTeam.GrantAsync(pStore.State!, Ann, "eve", SharedApps.Projects)).Status);
        Assert.Equal(0, pStore.Applies);
        pHost.Seat = null;
        Assert.Equal(204, (await pTeam.GrantAsync(pStore.State!, Ann, "eve", SharedApps.Projects)).Status);
        Assert.True(pStore.State!.Find("eve")!.Has(SharedApps.Projects));
    }

    [Fact]
    public async Task Invitations_choose_apps_and_hold_a_projects_seat_first()
    {
        var (store, _, sync) = Wire(SharedApps.Flow, State());
        var host = new FakeHost(SharedApps.Flow);
        var team = new SharedTeam(sync, host, NullLogger<SharedTeam>.Instance);

        var r = await team.InviteAsync(store.State!, Ada, new SharedInviteBody("new@example.com", null, null, null), null);
        Assert.Equal(201, r.Status);
        Assert.Equal([SharedApps.Flow], store.Invites.Single().Apps); // the default: the app it's sent from
        Assert.DoesNotContain("remote-seat", host.Calls);

        host.Seat = new SharedResult(402, new { error = "No seats.", code = "seats_full" });
        r = await team.InviteAsync(store.State!, Ada, new SharedInviteBody("pat@example.com", "member", null, [SharedApps.Projects, SharedApps.Flow]), null);
        Assert.Equal(402, r.Status);
        Assert.Single(store.Invites);

        r = await team.InviteAsync(store.State!, Ada, new SharedInviteBody("max@example.com", "member", null, [SharedApps.Orbit]), null);
        Assert.Equal(409, r.Status);
        r = await team.InviteAsync(store.State!, Ada, new SharedInviteBody("g@example.com", "guest", "vendor", [SharedApps.Flow]), null);
        Assert.Equal(409, r.Status);
        r = await team.InviteAsync(store.State!, Ada, new SharedInviteBody("g@example.com", "owner", null, [SharedApps.Orbit]), null);
        Assert.Equal(400, r.Status);
    }

    [Fact]
    public async Task The_team_lists_everyone_with_their_apps_and_invites_only_for_managers()
    {
        var (store, _, sync) = Wire(SharedApps.Flow, State());
        store.Invites.Add(new SharedInviteRow { Id = Guid.NewGuid(), Email = "x@example.com", Role = "member", Status = "sent", Apps = [SharedApps.Flow],
            InvitedByVantraId = "ann", ExpiresAt = DateTime.UtcNow.AddDays(3) });
        var team = new SharedTeam(sync, new FakeHost(SharedApps.Flow), NullLogger<SharedTeam>.Instance);
        var dto = await team.TeamAsync(store.State!, "ada");
        Assert.Equal(5, dto.Members.Count);
        Assert.True(dto.Members[0].Primary);
        Assert.Equal([SharedApps.Projects, SharedApps.Orbit, SharedApps.Flow], dto.Apps.Select(a => a.App));
        Assert.Single(dto.Invites);
        Assert.Empty((await team.TeamAsync(store.State!, "max")).Invites);
    }

    [Fact]
    public void Flow_shows_each_shared_role_as_its_own_and_never_a_guest()
    {
        var s = State(M("vic", SharedRoles.Guest, SharedApps.Flow)); // a guest's Flow row, if one got in, is left out
        var mine = FlowSharedMirror.Desired(s).ToDictionary(d => d.VantraId, d => d.Role);
        Assert.Equal(["ann", "ada"], mine.Keys);
        Assert.Equal("owner", mine["ann"]);
        Assert.Equal("admin", mine["ada"]);
        Assert.Null(FlowSharedMirror.LocalRole(SharedRoles.Guest));
        Assert.Equal("member", FlowSharedMirror.LocalRole(SharedRoles.Member));
    }
}

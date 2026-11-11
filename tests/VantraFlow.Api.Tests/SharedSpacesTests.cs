using Microsoft.Extensions.Logging.Abstractions;
using VantraFlow.Api.Shared;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>
/// Shared Vantra workspaces, stage 3: one workspace everywhere. Who may switch an app on or off, "In other apps", the
/// look as it's stored, linking every workspace once (race-safe, never one whose id is taken), and that nothing changes
/// before the shared tables exist or when they can't be read. The same tests are in each API.
/// </summary>
public sealed class SharedSpacesTests
{
    private static readonly Guid Ws = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static SharedPlace Place(string role, string[] on, string[] mine, string[]? off = null) =>
        new(Ws, new SharedLook("Example Co", "#12855d", null, "Europe/London"), "ann", role,
            on.Select(a => new SharedLink(a, Ws, true)).Concat((off ?? []).Select(a => new SharedLink(a, Ws, false))).ToList(), mine);

    // ── Rules ──

    [Fact]
    public void Owners_and_admins_add_an_app_members_and_guests_cant()
    {
        Assert.Null(SharedAppRules.CanAdd(Place(SharedRoles.Owner, [SharedApps.Projects], [SharedApps.Projects]), SharedApps.Flow));
        Assert.Null(SharedAppRules.CanAdd(Place(SharedRoles.Admin, [SharedApps.Projects], [SharedApps.Projects]), SharedApps.Orbit));
        Assert.Equal(SharedCodes.NotManager, SharedAppRules.CanAdd(Place(SharedRoles.Member, [SharedApps.Projects], [SharedApps.Projects]), SharedApps.Flow));
        Assert.Equal(SharedCodes.NotManager, SharedAppRules.CanAdd(Place(SharedRoles.Guest, [SharedApps.Projects], [SharedApps.Projects]), SharedApps.Orbit));
        Assert.Equal(SharedSpaceCodes.AppOn, SharedAppRules.CanAdd(Place(SharedRoles.Owner, [SharedApps.Flow], [SharedApps.Flow]), SharedApps.Flow));
        Assert.Equal(SharedCodes.NotMember, SharedAppRules.CanAdd(null, SharedApps.Flow));
        Assert.Equal(SharedCodes.InvalidApp, SharedAppRules.CanAdd(Place(SharedRoles.Owner, [], []), "folio"));
        // Switched off before: adding it again is allowed (its data comes back).
        Assert.Null(SharedAppRules.CanAdd(Place(SharedRoles.Owner, [SharedApps.Projects], [SharedApps.Projects], [SharedApps.Orbit]), SharedApps.Orbit));
    }

    [Fact]
    public void Only_owners_switch_an_app_off_and_never_the_last_one()
    {
        var both = new[] { SharedApps.Projects, SharedApps.Flow };
        Assert.Null(SharedAppRules.CanTurnOff(Place(SharedRoles.Owner, both, both), SharedApps.Flow));
        Assert.Equal(SharedSpaceCodes.NotOwner, SharedAppRules.CanTurnOff(Place(SharedRoles.Admin, both, both), SharedApps.Flow));
        Assert.Equal(SharedSpaceCodes.OnlyApp, SharedAppRules.CanTurnOff(Place(SharedRoles.Owner, [SharedApps.Flow], [SharedApps.Flow]), SharedApps.Flow));
        Assert.Equal(SharedCodes.AppOff, SharedAppRules.CanTurnOff(Place(SharedRoles.Owner, [SharedApps.Projects], [SharedApps.Projects], [SharedApps.Flow]), SharedApps.Flow));
    }

    [Fact]
    public void In_other_apps_says_why()
    {
        Assert.Equal(SharedCodes.AppOff, SharedAppRules.Elsewhere(Place(SharedRoles.Member, [SharedApps.Projects], [SharedApps.Projects]), SharedApps.Orbit));
        Assert.Equal("no_access", SharedAppRules.Elsewhere(Place(SharedRoles.Member, [SharedApps.Projects, SharedApps.Orbit], [SharedApps.Projects]), SharedApps.Orbit));
        Assert.Equal(SharedCodes.GuestFlow, SharedAppRules.Elsewhere(Place(SharedRoles.Guest, [SharedApps.Projects, SharedApps.Flow], [SharedApps.Projects]), SharedApps.Flow));
        Assert.Null(SharedAppRules.Elsewhere(Place(SharedRoles.Member, [SharedApps.Orbit], [SharedApps.Orbit]), SharedApps.Orbit));
    }

    [Fact]
    public void Adding_an_app_gives_it_to_the_caller_and_the_main_owner()
    {
        Assert.Equal(["ada", "ann"], SharedAppRules.AddGrants(Place(SharedRoles.Admin, [SharedApps.Projects], [SharedApps.Projects]), "ada"));
        Assert.Equal(["ann"], SharedAppRules.AddGrants(Place(SharedRoles.Owner, [SharedApps.Projects], [SharedApps.Projects]), "ann"));
    }

    [Fact]
    public void The_look_is_stored_clean()
    {
        var look = SharedAppRules.Clean(new SharedLook("  " + new string('x', 250) + " ", "#12855D", "https://example.com/logo.png", new string('z', 70)));
        Assert.Equal(200, look.Name.Length);
        Assert.Equal("#12855d", look.Color);
        Assert.Null(look.Logo);
        Assert.Null(look.TimeZone);
        Assert.Null(SharedAppRules.CleanColor("blue"));
        Assert.Equal("data:image/png;base64,AA==", SharedAppRules.CleanLogo("data:image/png;base64,AA=="));
        Assert.Null(SharedAppRules.CleanLogo("data:image/png;base64," + new string('A', 100_000)));
    }

    [Fact]
    public async Task A_removed_logo_is_marked_and_never_shown_and_a_never_set_one_is_filled_once()
    {
        Assert.Null(SharedAppRules.Shown(SharedAppRules.NoLogo));
        Assert.Null(SharedAppRules.CleanLogo(SharedAppRules.NoLogo));
        Assert.StartsWith("data:image/", SharedAppRules.NoLogo); // passes the column's check
        var (spaces, dir, _) = Make();
        await spaces.FillLogoAsync(Ws, "data:image/png;base64,AA==");
        await spaces.FillLogoAsync(Ws, "data:image/png;base64,BB==");
        await spaces.FillLogoAsync(Guid.NewGuid(), null);
        Assert.Equal("data:image/png;base64,AA==", Assert.Single(dir.Logos).Value);
    }

    // ── SharedSpaces against fakes ──

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ProbeStore(Func<bool> ready) : ISharedStore
    {
        public Task<bool> ProbeAsync() => Task.FromResult(ready());
        public Task<Guid?> SharedIdAsync(string app, Guid localId) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> LinkedAsync(string app, IReadOnlyCollection<Guid> localIds) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> UserLinksAsync(string app, string vantraId) => throw new NotSupportedException();
        public Task<SharedState?> StateAsync(Guid sharedId) => throw new NotSupportedException();
        public Task<(SharedPlan Plan, SharedState After)?> ApplyAsync(Guid sharedId, string by, string addedVia, Func<SharedState, SharedPlan> decide) => throw new NotSupportedException();
        public Task<IReadOnlyList<SharedInviteRow>> OpenInvitesAsync(Guid sharedId) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> InviteIdsAsync(Guid sharedId) => throw new NotSupportedException();
        public Task<SharedInviteRow?> InviteAsync(Guid sharedId, Guid inviteId) => throw new NotSupportedException();
        public Task<SharedInviteRow?> InviteByTokenAsync(byte[] tokenHash) => throw new NotSupportedException();
        public Task<int> InvitesIssuedSinceAsync(Guid sharedId, DateTime sinceUtc) => throw new NotSupportedException();
        public Task<SharedInviteRow> IssueInviteAsync(Guid sharedId, string email, string role, string? guestType, IReadOnlyList<string> apps,
            byte[] tokenHash, string by, string fromApp, DateTime expiresAt) => throw new NotSupportedException();
        public Task<SharedInviteRow?> RenewInviteAsync(Guid sharedId, Guid inviteId, byte[] tokenHash, DateTime expiresAt) => throw new NotSupportedException();
        public Task<SharedInviteRow?> MarkInviteSentAsync(Guid inviteId) => throw new NotSupportedException();
        public Task<bool> RevokeInviteAsync(Guid sharedId, Guid inviteId) => throw new NotSupportedException();
        public Task<(SharedInviteOutcome Outcome, SharedInviteRow Invite, SharedState? After)?> AcceptInviteAsync(byte[] tokenHash, string vantraId, string? accountEmail, DateTime nowUtc) =>
            throw new NotSupportedException();
    }

    /// <summary>An in-memory shared record: links by (app, local id), and which shared ids exist.</summary>
    private sealed class FakeDirectory : ISharedDirectory
    {
        public readonly Dictionary<(string App, Guid Local), SharedLinkInfo> Links = [];
        public readonly HashSet<Guid> Workspaces = [];
        public readonly List<SharedPlace> Places = [];
        public int LinkCalls, LinksCalls;
        public bool Throw;

        public Task<SharedLinkResult> LinkAsync(string app, SharedSeed seed, string linkReason, string addedVia)
        {
            LinkCalls++;
            if (Throw) throw new InvalidOperationException("down");
            if (Links.ContainsKey((app, seed.LocalId))) return Task.FromResult(SharedLinkResult.AlreadyLinked);
            if (!Workspaces.Add(seed.LocalId)) return Task.FromResult(SharedLinkResult.Taken);
            Links[(app, seed.LocalId)] = new SharedLinkInfo(seed.LocalId, seed.LocalId, true);
            return Task.FromResult(SharedLinkResult.Linked);
        }

        public Task<IReadOnlyList<SharedLinkInfo>> LinksAsync(string app, IReadOnlyCollection<Guid> localIds)
        {
            LinksCalls++;
            if (Throw) throw new InvalidOperationException("down");
            return Task.FromResult<IReadOnlyList<SharedLinkInfo>>(localIds.Where(id => Links.ContainsKey((app, id))).Select(id => Links[(app, id)]).ToList());
        }

        public Task<IReadOnlyList<SharedPlace>> PlacesAsync(string vantraId) =>
            Throw ? throw new InvalidOperationException("down") : Task.FromResult<IReadOnlyList<SharedPlace>>(Places);
        public Task<SharedPlace?> PlaceAsync(Guid sharedId, string vantraId) =>
            Throw ? throw new InvalidOperationException("down") : Task.FromResult(Places.FirstOrDefault(p => p.Id == sharedId));
        public Task<SharedLook?> SetLookAsync(Guid sharedId, SharedLookChange change) => throw new NotSupportedException();
        public readonly Dictionary<Guid, string> Logos = [];
        public Task<bool> FillLogoAsync(Guid sharedId, string logo) => Task.FromResult(Logos.TryAdd(sharedId, logo));
        public Task SetAppAsync(Guid sharedId, string app, Guid localId, bool on, string by, IReadOnlyList<string> grantTo) => throw new NotSupportedException();
        public Task<bool> UnlinkAsync(Guid sharedId, string app, bool deleteWhenEmpty) => throw new NotSupportedException();
    }

    private static (SharedSpaces Spaces, FakeDirectory Dir, Clock Clock) Make(bool ready = true)
    {
        var clock = new Clock();
        var dir = new FakeDirectory();
        var members = new SharedMembers(new ProbeStore(() => ready), NullLogger<SharedMembers>.Instance, clock);
        return (new SharedSpaces(dir, members, NullLogger<SharedSpaces>.Instance, clock), dir, clock);
    }

    private static Task<SharedSeed?> Seed(Guid id) =>
        Task.FromResult<SharedSeed?>(new SharedSeed(id, new SharedLook("Example Co", null, null, null), "ann", [new SharedChange("ann", SharedRoles.Owner, null)]));

    [Fact]
    public async Task Before_the_shared_tables_nothing_is_linked_switched_off_or_listed()
    {
        var (spaces, dir, _) = Make(ready: false);
        var id = Guid.NewGuid();
        Assert.Empty(await spaces.EnsureLinkedAsync(SharedApps.Flow, [id], Seed));
        Assert.Empty(await spaces.OffAsync(SharedApps.Flow, [id]));
        Assert.Empty(await spaces.ElsewhereAsync(SharedApps.Flow, "ann"));
        var products = await spaces.ProductsAsync(SharedApps.Flow, id, "ann");
        Assert.False(products.Shared);
        Assert.Equal([SharedApps.Flow], products.Products.Select(p => p.App));
        Assert.Equal(0, dir.LinkCalls + dir.LinksCalls);
    }

    [Fact]
    public async Task Every_workspace_is_linked_once_with_its_own_id()
    {
        var (spaces, dir, _) = Make();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal([a, b], await spaces.EnsureLinkedAsync(SharedApps.Flow, [a, b, a], Seed));
        Assert.Equal(a, dir.Links[(SharedApps.Flow, a)].SharedId);
        Assert.Empty(await spaces.EnsureLinkedAsync(SharedApps.Flow, [a, b], Seed));
        Assert.Equal(2, dir.LinkCalls);
    }

    [Fact]
    public async Task A_workspace_whose_id_is_taken_is_left_alone_and_tried_again_later()
    {
        var (spaces, dir, clock) = Make();
        var id = Guid.NewGuid();
        dir.Workspaces.Add(id); // e.g. an Orbit copy of a Projects workspace
        Assert.Empty(await spaces.EnsureLinkedAsync(SharedApps.Orbit, [id], Seed));
        Assert.Empty(await spaces.EnsureLinkedAsync(SharedApps.Orbit, [id], Seed));
        Assert.Equal(1, dir.LinkCalls);
        clock.Now = clock.Now.Add(SharedSpaces.RetryLinkAfter).AddSeconds(1);
        await spaces.EnsureLinkedAsync(SharedApps.Orbit, [id], Seed);
        Assert.Equal(2, dir.LinkCalls);
        Assert.False(dir.Links.ContainsKey((SharedApps.Orbit, id)));
    }

    [Fact]
    public async Task A_seed_of_null_is_skipped()
    {
        var (spaces, dir, _) = Make();
        Assert.Empty(await spaces.EnsureLinkedAsync(SharedApps.Flow, [Guid.NewGuid()], _ => Task.FromResult<SharedSeed?>(null)));
        Assert.Equal(0, dir.LinkCalls);
    }

    [Fact]
    public async Task Switched_off_is_remembered_for_thirty_seconds()
    {
        var (spaces, dir, clock) = Make();
        var id = Guid.NewGuid();
        dir.Links[(SharedApps.Flow, id)] = new SharedLinkInfo(Ws, id, false);
        Assert.True(await spaces.IsOffAsync(SharedApps.Flow, id));
        dir.Links[(SharedApps.Flow, id)] = new SharedLinkInfo(Ws, id, true);
        Assert.True(await spaces.IsOffAsync(SharedApps.Flow, id));
        clock.Now = clock.Now.Add(SharedSpaces.OffTtl).AddSeconds(1);
        Assert.False(await spaces.IsOffAsync(SharedApps.Flow, id));
        dir.Links[(SharedApps.Flow, id)] = new SharedLinkInfo(Ws, id, false);
        spaces.Forget(id);
        Assert.True(await spaces.IsOffAsync(SharedApps.Flow, id));
    }

    [Fact]
    public async Task An_unreadable_shared_record_never_fails_a_read()
    {
        var (spaces, dir, _) = Make();
        dir.Throw = true;
        var id = Guid.NewGuid();
        Assert.Empty(await spaces.EnsureLinkedAsync(SharedApps.Flow, [id], Seed));
        Assert.Empty(await spaces.OffAsync(SharedApps.Flow, [id]));
        Assert.Empty(await spaces.ElsewhereAsync(SharedApps.Flow, "ann"));
        Assert.False((await spaces.ProductsAsync(SharedApps.Flow, id, "ann")).Shared);
        await spaces.AfterDeleteAsync(SharedApps.Flow, id, "ann");
        await Assert.ThrowsAsync<SharedUnavailableException>(() => spaces.LinkAsync(SharedApps.Flow, id));
    }

    [Fact]
    public async Task In_other_apps_lists_what_isnt_here_and_who_can_add_it()
    {
        var (spaces, dir, _) = Make();
        var here = Place(SharedRoles.Member, [SharedApps.Flow], [SharedApps.Flow]) with { Id = Guid.NewGuid() };
        var noApp = Place(SharedRoles.Admin, [SharedApps.Projects], [SharedApps.Projects]) with { Id = Guid.NewGuid() };
        var noAccess = Place(SharedRoles.Member, [SharedApps.Projects, SharedApps.Flow], [SharedApps.Projects]) with { Id = Guid.NewGuid() };
        dir.Places.AddRange([here, noApp, noAccess]);
        var list = await spaces.ElsewhereAsync(SharedApps.Flow, "ada");
        Assert.Equal(2, list.Count);
        Assert.Equal((SharedCodes.AppOff, true), (list[0].Reason, list[0].CanAdd));
        Assert.Equal(("no_access", false), (list[1].Reason, list[1].CanAdd));
    }

    [Fact]
    public async Task A_workspaces_products_say_what_it_has_and_what_you_may_do()
    {
        var (spaces, dir, _) = Make();
        var id = Guid.NewGuid();
        dir.Links[(SharedApps.Flow, id)] = new SharedLinkInfo(Ws, id, true);
        dir.Places.Add(Place(SharedRoles.Owner, [SharedApps.Projects, SharedApps.Flow], [SharedApps.Projects, SharedApps.Flow]));
        var p = await spaces.ProductsAsync(SharedApps.Flow, id, "ann");
        Assert.True(p.Shared && p.CanAdd && p.CanTurnOff);
        Assert.Equal([true, false, true], p.Products.Select(x => x.On));
        Assert.Equal(SharedApps.Flow, Assert.Single(p.Products, x => x.Here).App);
    }
}

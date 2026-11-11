using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Workspaces;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>Who may do what to a workspace and its members: the rules on their own.</summary>
public sealed class WorkspaceRulesTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static WorkspaceState State(params (string Id, string Role)[] m) =>
        new(m.Select((x, i) => new MemberState(x.Id, x.Role, T0.AddDays(i))).ToList(), m.First(x => x.Role == FlowRoles.Owner).Id);

    private static readonly WorkspaceState Team = State(("olga", "owner"), ("adam", "admin"), ("mia", "member"), ("max", "member"));

    [Theory]
    [InlineData("mia", "max", "admin", "not_manager")]   // a member changes nothing
    [InlineData("adam", "olga", "member", "owner_only")]  // an admin can't change an owner
    [InlineData("adam", "mia", "owner", "owner_only")]    // nor make one
    [InlineData("olga", "olga", "admin", "last_owner")]   // the last owner stays
    [InlineData("adam", "nobody", "admin", "not_member")]
    [InlineData("stranger", "mia", "admin", "not_member")]
    public void Role_changes_are_refused_when_they_should_be(string caller, string target, string role, string code) =>
        Assert.Equal(code, WorkspaceRules.SetRole(Team, caller, target, role).Error);

    [Fact]
    public void Admins_change_members_and_owners_make_owners()
    {
        Assert.True(WorkspaceRules.SetRole(Team, "adam", "mia", "admin").Ok);
        var p = WorkspaceRules.SetRole(Team, "olga", "mia", "owner");
        Assert.True(p.Ok);
        Assert.Equal("owner", Assert.Single(p.Changes).Role);
        Assert.Equal("olga", p.PrimaryOwner);
        // With two owners, either may step down; the primary passes to the other.
        var two = State(("olga", "owner"), ("ola", "owner"));
        var down = WorkspaceRules.SetRole(two, "olga", "olga", "admin");
        Assert.True(down.Ok);
        Assert.Equal("ola", down.PrimaryOwner);
    }

    [Fact]
    public void Removing_follows_the_same_rules_and_never_yourself()
    {
        Assert.Equal("not_manager", WorkspaceRules.Remove(Team, "mia", "max").Error);
        Assert.Equal("owner_only", WorkspaceRules.Remove(Team, "adam", "olga").Error);
        Assert.Equal("self", WorkspaceRules.Remove(Team, "adam", "adam").Error);
        Assert.Equal(["max"], WorkspaceRules.Remove(Team, "adam", "max").Removals);
        var two = State(("olga", "owner"), ("ola", "owner"));
        Assert.True(WorkspaceRules.Remove(two, "ola", "olga").Ok);
    }

    [Fact]
    public void The_last_owner_can_not_leave_but_everyone_else_can()
    {
        Assert.Equal("last_owner", WorkspaceRules.Leave(Team, "olga").Error);
        Assert.True(WorkspaceRules.Leave(Team, "adam").Ok);
        Assert.True(WorkspaceRules.Leave(Team, "mia").Ok);
        Assert.Equal("not_member", WorkspaceRules.Leave(Team, "stranger").Error);
        var two = State(("olga", "owner"), ("ola", "owner"));
        var p = WorkspaceRules.Leave(two, "olga");
        Assert.True(p.Ok);
        Assert.Equal("ola", p.PrimaryOwner);
    }

    [Fact]
    public void Handing_over_makes_them_the_owner_and_you_an_admin()
    {
        Assert.Equal("owner_only", WorkspaceRules.Transfer(Team, "adam", "mia").Error);
        Assert.Equal("self", WorkspaceRules.Transfer(Team, "olga", "olga").Error);
        Assert.Equal("not_member", WorkspaceRules.Transfer(Team, "olga", "nobody").Error);
        var p = WorkspaceRules.Transfer(Team, "olga", "mia");
        Assert.True(p.Ok);
        Assert.Equal("mia", p.PrimaryOwner);
        Assert.Contains(p.Changes, c => c is { VantraId: "olga", Role: "admin" });
        Assert.Contains(p.Changes, c => c is { VantraId: "mia", Role: "owner" });
    }

    [Fact]
    public void Only_owners_delete_and_managers_invite()
    {
        Assert.True(WorkspaceRules.CanDelete("owner"));
        Assert.False(WorkspaceRules.CanDelete("admin"));
        Assert.False(WorkspaceRules.CanDelete("member"));
        Assert.True(WorkspaceRules.CanInvite("admin"));
        Assert.False(WorkspaceRules.CanInvite("member"));
        Assert.False(WorkspaceRules.IsInvitableRole("owner"));
        Assert.True(WorkspaceRules.IsInvitableRole("member"));
    }

    [Theory]
    [InlineData(" Studio North ", "Studio North")]
    [InlineData("A", null)]
    [InlineData("", null)]
    [InlineData("Line\nbreak", null)]
    public void Names_are_two_to_eighty_characters(string input, string? expected) => Assert.Equal(expected, WorkspaceRules.CleanName(input));

    [Fact]
    public void Colours_and_the_typed_confirmation()
    {
        Assert.Equal("#12855d", WorkspaceRules.CleanColor("#12855D"));
        Assert.Null(WorkspaceRules.CleanColor("green"));
        Assert.Null(WorkspaceRules.CleanColor("#12855"));
        Assert.True(WorkspaceRules.ConfirmMatches("Northwind", " Northwind "));
        Assert.False(WorkspaceRules.ConfirmMatches("Northwind", "northwind"));
        Assert.False(WorkspaceRules.ConfirmMatches("Northwind", null));
    }
}

/// <summary>Invitation tokens, addresses and the decision on opening one.</summary>
public sealed class InviteRulesTests
{
    [Fact]
    public void Tokens_are_random_url_safe_and_kept_only_as_a_hash()
    {
        var a = InviteRules.NewToken();
        var b = InviteRules.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.True(InviteRules.IsTokenShape(a));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        var h = InviteRules.Hash(a);
        Assert.Equal(32, h.Length);
        Assert.Equal(h, InviteRules.Hash(a));
        Assert.NotEqual(h, InviteRules.Hash(b));
        Assert.False(InviteRules.IsTokenShape("short"));
        Assert.False(InviteRules.IsTokenShape(new string('a', 42) + "/"));
        Assert.False(InviteRules.IsTokenShape(null));
    }

    [Theory]
    [InlineData("  Jane@Example.com ", "jane@example.com")]
    [InlineData("Jane Doe <jane@example.com>", null)]
    [InlineData("jane@", null)]
    [InlineData("jane@localhost", null)]
    [InlineData("", null)]
    public void Addresses_are_one_plain_email(string input, string? expected) => Assert.Equal(expected, InviteRules.NormalizeEmail(input));

    [Fact]
    public void Opening_one_is_decided_in_the_right_order()
    {
        var now = DateTime.UtcNow;
        var later = now.AddDays(3);
        Assert.Equal(InviteOutcome.WrongAccount, InviteRules.Evaluate("sent", later, now, "jane@example.com", "joe@example.com", false));
        Assert.Equal(InviteOutcome.AlreadyMember, InviteRules.Evaluate("accepted", later, now, "jane@example.com", "JANE@example.com", true));
        Assert.Equal(InviteOutcome.Used, InviteRules.Evaluate("accepted", later, now, "jane@example.com", "jane@example.com", false));
        Assert.Equal(InviteOutcome.Revoked, InviteRules.Evaluate("revoked", later, now, "jane@example.com", "jane@example.com", false));
        Assert.Equal(InviteOutcome.Expired, InviteRules.Evaluate("pending", now.AddSeconds(-1), now, "jane@example.com", "jane@example.com", false));
        Assert.Equal(InviteOutcome.Accept, InviteRules.Evaluate("pending", later, now, "jane@example.com", "jane@example.com", false));
        Assert.Equal(InviteOutcome.WrongAccount, InviteRules.Evaluate("pending", later, now, "jane@example.com", null, false));
        Assert.Equal("expired", InviteRules.ListStatus("sent", now.AddMinutes(-1), now));
        Assert.Equal("sent", InviteRules.ListStatus("sent", later, now));
    }

    [Fact]
    public void Resends_wait_a_minute_and_links_stay_on_the_studio()
    {
        var now = DateTime.UtcNow;
        Assert.False(InviteRules.CanResend(now.AddSeconds(-20), now));
        Assert.Equal(40, InviteRules.RetryAfterSeconds(now.AddSeconds(-20), now));
        Assert.True(InviteRules.CanResend(now.AddMinutes(-2), now));
        Assert.True(InviteRules.CanResend(null, now));
        Assert.Equal("j•••@example.com", InviteRules.EmailHint("jane@example.com"));
        Assert.Null(InviteRules.TrustedOrigin("https://evil.example.com", ["http://localhost:5195"], "https://flow.example.com"));
        Assert.Equal("http://localhost:5195", InviteRules.TrustedOrigin("http://localhost:5195/", ["http://localhost:5195"], null));
        Assert.Equal("https://flow.example.com/invite/abc", InviteRules.Link(null, "https://flow.example.com/", "abc"));
    }

    [Fact]
    public void Emails_encode_what_people_typed()
    {
        var (subject, html) = WorkspaceMail.ComposeInvite("<b>Acme</b>", "Eve \"x\"", "admin", "https://flow.example.com/invite/abc?x=<y>");
        Assert.DoesNotContain("<b>Acme</b>", html);
        Assert.Contains("&lt;b&gt;Acme&lt;/b&gt;", html);
        Assert.Contains("an admin", html);
        Assert.Contains("https://flow.example.com/invite/abc?x=&lt;y&gt;", html);
        Assert.DoesNotContain("\n", subject);
        var (added, body) = WorkspaceMail.ComposeAdded("Acme", "Eve", "member", "https://flow.example.com/flows?ws=1");
        Assert.Contains("added you to Acme", added);
        Assert.Contains("a member", body);
    }
}

/// <summary>
/// The workspace endpoints end to end over an in-memory database: every one checks the caller's role on the server
/// (members, admins, owners, strangers), invitations, accepting them, and deleting a workspace.
/// </summary>
public sealed class WorkspaceEndpointTests
{
    private readonly FakeWorkspaces db = new();
    private readonly FakeUsers users = new();
    private readonly FakeMail mail = new();
    private readonly FakeFeatures features = new();
    private readonly CapturingLogger<MembersController> membersLog = new();
    private readonly CapturingLogger<InviteAcceptController> acceptLog = new();
    private readonly Guid ws;

    public WorkspaceEndpointTests()
    {
        ws = db.Add("Northwind", ("olga", "owner"), ("adam", "admin"), ("mia", "member"));
        foreach (var id in new[] { "olga", "adam", "mia" }) users.Add(id, $"{id}@example.com");
    }

    private WorkspacesController Workspaces(string? sub, IConfiguration? config = null) =>
        new(db, config ?? new ConfigurationBuilder().Build(), new FakeAlerts(), features, new FakeTeardown(db), NullLogger<WorkspacesController>.Instance)
        { ControllerContext = Http.As(sub) };

    private MembersController Members(string? sub) => new(db, db, users, mail, features, membersLog) { ControllerContext = Http.As(sub) };

    private InviteAcceptController Accept(string? sub, string? email = null) =>
        new(db, users, features, acceptLog) { ControllerContext = Http.As(sub, email) };

    [Fact]
    public async Task Todays_studio_gets_no_automatic_workspace_an_older_one_still_does()
    {
        // Stage 3: someone with no workspace chooses (create one, or add Flow to a shared one).
        static IReadOnlyList<WorkspaceDto> Of(ActionResult<IReadOnlyList<WorkspaceDto>> r) =>
            Assert.IsAssignableFrom<IReadOnlyList<WorkspaceDto>>(((ObjectResult)r.Result!).Value);
        Assert.Empty(Of(await Workspaces("newcomer").List("0")));
        Assert.DoesNotContain(db.Log, l => l.StartsWith("create:"));
        var first = Assert.Single(Of(await Workspaces("newcomer").List()));
        Assert.Equal("My workspace", first.Name);
    }

    [Fact]
    public void The_two_retired_my_workspaces_are_never_made_shared()
    {
        var spaces = new VantraFlow.Api.Shared.FlowSpaces(db, features, null!);
        foreach (var id in VantraFlow.Api.Shared.FlowSpaces.Retired)
            Assert.Null(spaces.SeedAsync(id).GetAwaiter().GetResult());
        Assert.Contains(Guid.Parse("B25498CA-A9BB-4AD3-BD8C-559C639B82E8"), VantraFlow.Api.Shared.FlowSpaces.Retired);
        Assert.Contains(Guid.Parse("2CEB40CB-FB3F-4BFD-96AA-89AC2B3248A7"), VantraFlow.Api.Shared.FlowSpaces.Retired);
    }

    [Fact]
    public async Task A_flow_workspace_seeds_its_shared_record_as_it_is()
    {
        var seed = await new VantraFlow.Api.Shared.FlowSpaces(db, features, null!).SeedAsync(ws);
        Assert.NotNull(seed);
        Assert.Equal(("olga", "Northwind"), (seed.Owner, seed.Look.Name));
        Assert.Equal(["owner", "admin", "member"], seed.Members.Select(m => m.Role));
    }

    [Fact]
    public async Task Strangers_see_nothing_and_members_change_nothing()
    {
        Assert.Equal(404, Http.Status(await Members("eve").Members(ws)));
        Assert.Equal(404, Http.Status(await Members("eve").Invite(ws, new CreateInviteBody("x@example.com", "member"))));
        Assert.Equal(404, Http.Status(await Workspaces("eve").Delete(ws, new DeleteWorkspaceBody("Northwind"), default)));

        Assert.Equal(200, Http.Status(await Members("mia").Members(ws)));
        foreach (var r in new IActionResult[]
                 {
                     await Members("mia").Invite(ws, new CreateInviteBody("x@example.com", "member")),
                     await Members("mia").Invites(ws),
                     await Members("mia").Role(ws, "adam", new UpdateMemberBody("member")),
                     await Members("mia").Remove(ws, "adam"),
                     await Members("mia").Revoke(ws, Guid.NewGuid()),
                     await Members("mia").Resend(ws, Guid.NewGuid()),
                     (await Workspaces("mia").Update(ws, new UpdateWorkspaceBody("Renamed", null))).Result!,
                 })
        {
            Assert.Equal(403, Http.Status(r));
            Assert.Equal("not_manager", Http.Code(r));
        }
        Assert.Equal("Northwind", db.Rows[ws].Name);
        Assert.Equal("admin", db.RoleOf(ws, "adam"));
    }

    [Fact]
    public async Task Admins_manage_members_but_not_owners_and_only_owners_delete_or_hand_over()
    {
        Assert.Equal(204, Http.Status(await Members("adam").Role(ws, "mia", new UpdateMemberBody("admin"))));
        Assert.Equal("owner_only", Http.Code(await Members("adam").Role(ws, "olga", new UpdateMemberBody("member"))));
        Assert.Equal("owner_only", Http.Code(await Members("adam").Role(ws, "mia", new UpdateMemberBody("owner"))));
        Assert.Equal("owner_only", Http.Code(await Members("adam").Remove(ws, "olga")));
        Assert.Equal(400, Http.Status(await Members("adam").Role(ws, "mia", new UpdateMemberBody("boss"))));

        var del = await Workspaces("adam").Delete(ws, new DeleteWorkspaceBody("Northwind"), default);
        Assert.Equal(403, Http.Status(del));
        Assert.Equal("owner_only", Http.Code(del));
        Assert.Equal("owner_only", Http.Code(await Workspaces("adam").Transfer(ws, new TransferWorkspaceBody("mia"))));
        Assert.True(db.Rows.ContainsKey(ws));
    }

    [Fact]
    public async Task There_is_always_an_owner_and_ownership_can_be_handed_over()
    {
        Assert.Equal("last_owner", Http.Code(await Workspaces("olga").Leave(ws)));
        Assert.Equal("last_owner", Http.Code(await Members("olga").Role(ws, "olga", new UpdateMemberBody("admin"))));
        Assert.Equal("last_owner", Http.Code(await Members("olga").Remove(ws, "olga")));

        Assert.Equal(204, Http.Status(await Workspaces("olga").Transfer(ws, new TransferWorkspaceBody("mia"))));
        Assert.Equal("owner", db.RoleOf(ws, "mia"));
        Assert.Equal("admin", db.RoleOf(ws, "olga"));
        Assert.Equal("mia", db.Rows[ws].OwnerVantraId);

        // Now olga isn't an owner, she can leave; mia can't.
        Assert.Equal(204, Http.Status(await Workspaces("olga").Leave(ws)));
        Assert.Null(db.RoleOf(ws, "olga"));
        Assert.Equal("last_owner", Http.Code(await Workspaces("mia").Leave(ws)));
    }

    [Fact]
    public async Task Inviting_an_existing_account_adds_them_and_emails_a_note()
    {
        users.Add("sam", "sam@example.com");
        var r = await Members("adam").Invite(ws, new CreateInviteBody(" Sam@Example.com ", "admin"));
        Assert.Equal(201, Http.Status(r));
        var body = Http.Body<InviteResultDto>(r);
        Assert.Equal("added", body.Kind);
        Assert.True(body.Emailed);
        Assert.Equal("admin", db.RoleOf(ws, "sam"));
        Assert.Equal(("added", "sam@example.com"), (mail.Sent[0].Kind, mail.Sent[0].To));
        Assert.Empty(db.Invites);

        var again = await Members("adam").Invite(ws, new CreateInviteBody("sam@example.com", "member"));
        Assert.Equal(409, Http.Status(again));
        Assert.Equal("already_member", Http.Code(again));
        Assert.Equal("invalid_role", Http.Code(await Members("adam").Invite(ws, new CreateInviteBody("x@example.com", "owner"))));
        Assert.Equal("invalid_email", Http.Code(await Members("adam").Invite(ws, new CreateInviteBody("not an email", "member"))));
    }

    [Fact]
    public async Task Inviting_someone_new_sends_a_link_whose_token_is_stored_only_hashed_and_never_logged()
    {
        var r = await Members("adam").Invite(ws, new CreateInviteBody("new@example.com", "member"));
        Assert.Equal(201, Http.Status(r));
        var body = Http.Body<InviteResultDto>(r);
        Assert.Equal("invited", body.Kind);
        Assert.Equal("sent", body.Invite!.Status);
        var token = body.Invite.Link!.Split('/').Last();
        Assert.True(InviteRules.IsTokenShape(token));
        Assert.Equal(body.Invite.Link, mail.Sent.Single().Link);
        var (row, hash) = Assert.Single(db.Invites);
        Assert.Equal(InviteRules.Hash(token), hash);
        Assert.DoesNotContain(token, Convert.ToBase64String(hash));

        // The list never shows a link again.
        var list = Assert.IsAssignableFrom<IEnumerable<InviteDto>>(((ObjectResult)await Members("adam").Invites(ws)).Value);
        Assert.Null(Assert.Single(list).Link);

        // Accepting it, as the invited address, joins; the token is in no log line.
        users.Add("newbie", "new@example.com");
        var joined = await Accept("newbie").Accept(new InviteTokenBody(token));
        Assert.Equal(200, Http.Status(joined));
        Assert.Equal("member", db.RoleOf(ws, "newbie"));
        Assert.DoesNotContain(membersLog.Lines.Concat(acceptLog.Lines), l => l.Contains(token, StringComparison.Ordinal));
        Assert.NotEmpty(membersLog.Lines);
    }

    [Fact]
    public async Task Without_a_sender_the_invitation_waits_with_its_link_to_copy()
    {
        mail.CanSend = false;
        var body = Http.Body<InviteResultDto>(await Members("olga").Invite(ws, new CreateInviteBody("new@example.com", "admin")));
        Assert.False(body.Emailed);
        Assert.Equal("pending", body.Invite!.Status);
        Assert.NotNull(body.Invite.Link);
    }

    [Fact]
    public async Task Before_db_005_new_people_wait_but_existing_accounts_still_join()
    {
        features.Invites = false;
        var r = await Members("adam").Invite(ws, new CreateInviteBody("new@example.com", "member"));
        Assert.Equal(503, Http.Status(r));
        Assert.Equal("not_ready", Http.Code(r));
        Assert.Equal("not_ready", Http.Code(await Members("adam").Invites(ws)));
        Assert.Equal("not_ready", Http.Code(await Accept("mia").Accept(new InviteTokenBody(InviteRules.NewToken()))));
        users.Add("sam", "sam@example.com");
        Assert.Equal(201, Http.Status(await Members("adam").Invite(ws, new CreateInviteBody("sam@example.com", "member"))));
        Assert.Equal(503, Http.Status((await Workspaces("olga").Update(ws, new UpdateWorkspaceBody(null, null, "#ff0000"))).Result!));
        // Renaming keeps working.
        Assert.Equal(200, Http.Status(await Workspaces("olga").Update(ws, new UpdateWorkspaceBody("Northwind Trading", null))));
    }

    [Fact]
    public async Task Each_workspace_has_a_daily_cap_and_resends_wait_a_minute()
    {
        db.SentToday = InviteRules.DailyLimitPerWorkspace;
        var capped = await Members("adam").Invite(ws, new CreateInviteBody("new@example.com", "member"));
        Assert.Equal(429, Http.Status(capped));
        Assert.Equal("invite_limit", Http.Code(capped));
        db.SentToday = 0;

        var first = Http.Body<InviteResultDto>(await Members("adam").Invite(ws, new CreateInviteBody("new@example.com", "member"))).Invite!;
        var soon = await Members("adam").Resend(ws, Guid.Parse(first.Id));
        Assert.Equal(429, Http.Status(soon));

        db.Invites[0].Row.SentAt = DateTime.UtcNow.AddMinutes(-5);
        var oldHash = db.Invites[0].Hash;
        var resent = Http.Body<InviteDto>(await Members("adam").Resend(ws, Guid.Parse(first.Id)));
        Assert.NotEqual(first.Link, resent.Link);
        Assert.NotEqual(oldHash, db.Invites[0].Hash);
        // The old link stops working.
        users.Add("newbie", "new@example.com");
        Assert.Equal("invite_invalid", Http.Code(await Accept("newbie").Accept(new InviteTokenBody(first.Link!.Split('/').Last()))));
    }

    [Fact]
    public async Task Revoked_expired_used_and_wrong_account_links_each_say_so()
    {
        string Token(IActionResult r) => Http.Body<InviteResultDto>(r).Invite!.Link!.Split('/').Last();

        var revoked = Token(await Members("adam").Invite(ws, new CreateInviteBody("new@example.com", "member")));
        var id = db.Invites.Single().Row.Id;
        Assert.Equal(204, Http.Status(await Members("adam").Revoke(ws, id)));
        Assert.Equal(204, Http.Status(await Members("adam").Revoke(ws, id))); // again: still fine
        Assert.Equal(404, Http.Status(await Members("adam").Revoke(ws, Guid.NewGuid())));
        Assert.Equal("invite_revoked", Http.Code(await Accept(null).Preview(new InviteTokenBody(revoked))));
        Assert.Equal("invite_revoked", Http.Code(await Accept("newbie", "new@example.com").Accept(new InviteTokenBody(revoked))));

        var expired = Token(await Members("adam").Invite(ws, new CreateInviteBody("new@example.com", "member")));
        db.Invites.Single().Row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        Assert.Equal(410, Http.Status(await Accept(null).Preview(new InviteTokenBody(expired))));
        Assert.Equal("invite_expired", Http.Code(await Accept("newbie", "new@example.com").Accept(new InviteTokenBody(expired))));

        var good = Token(await Members("adam").Invite(ws, new CreateInviteBody("new@example.com", "admin")));
        var preview = Http.Body<InvitePreviewDto>(await Accept(null).Preview(new InviteTokenBody(good)));
        Assert.Equal("Northwind", preview.WorkspaceName);
        Assert.Equal("n•••@example.com", preview.EmailHint);

        var wrong = await Accept("mia").Accept(new InviteTokenBody(good));
        Assert.Equal(409, Http.Status(wrong));
        Assert.Equal("wrong_account", Http.Code(wrong));
        Assert.Null(db.RoleOf(ws, "newbie"));

        Assert.Equal(200, Http.Status(await Accept("newbie", "new@example.com").Accept(new InviteTokenBody(good))));
        Assert.Equal("admin", db.RoleOf(ws, "newbie"));
        var again = Http.Body<AcceptInviteDto>(await Accept("newbie", "new@example.com").Accept(new InviteTokenBody(good)));
        Assert.True(again.AlreadyMember);
        Assert.Equal("invite_used", Http.Code(await Accept(null).Preview(new InviteTokenBody(good))));
        Assert.Equal("invite_invalid", Http.Code(await Accept(null).Preview(new InviteTokenBody("not-a-token"))));
    }

    [Fact]
    public async Task Deleting_takes_the_owner_and_the_typed_name_switches_off_disconnects_then_deletes()
    {
        var wrong = await Workspaces("olga").Delete(ws, new DeleteWorkspaceBody("northwind"), default);
        Assert.Equal(400, Http.Status(wrong));
        Assert.Equal("confirm_mismatch", Http.Code(wrong));
        Assert.Empty(db.Log);

        var ok = await Workspaces("olga").Delete(ws, new DeleteWorkspaceBody("Northwind"), default);
        Assert.Equal(200, Http.Status(ok));
        Assert.Equal(["pause", "disconnect", "delete"], db.Log);
        Assert.False(db.Rows.ContainsKey(ws));
        Assert.Equal(0, db.Flows);

        // Again: already gone is fine.
        Assert.Equal(204, Http.Status(await Workspaces("olga").Delete(ws, new DeleteWorkspaceBody("Northwind"), default)));
    }

    [Fact]
    public async Task A_delete_that_fails_part_way_can_be_run_again()
    {
        db.FailDelete = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workspaces("olga").Delete(ws, new DeleteWorkspaceBody("Northwind"), default));
        Assert.Equal("owner", db.RoleOf(ws, "olga"));
        db.FailDelete = false;
        Assert.Equal(200, Http.Status(await Workspaces("olga").Delete(ws, new DeleteWorkspaceBody("Northwind"), default)));
        Assert.False(db.Rows.ContainsKey(ws));
    }

    [Fact]
    public async Task Creating_checks_the_name_look_and_how_many_you_own()
    {
        Assert.Equal(400, Http.Status(await Workspaces("olga").Create(new CreateWorkspaceBody("A", "UTC"))));
        Assert.Equal("invalid_color", Http.Code(await Workspaces("olga").Create(new CreateWorkspaceBody("Studio", "UTC", "blue"))));
        Assert.Equal("invalid_logo", Http.Code(await Workspaces("olga").Create(new CreateWorkspaceBody("Studio", "UTC", null, "data:image/svg+xml;base64,PHN2Zz4="))));
        var made = await Workspaces("olga").Create(new CreateWorkspaceBody("Studio North", "Europe/London", "#8B5CF6"));
        var dto = Assert.IsType<WorkspaceDto>(((ObjectResult)made.Result!).Value);
        Assert.Equal("#8b5cf6", dto.Color);
        Assert.Equal("owner", dto.Role);
        Assert.Equal("Europe/London", dto.TimeZone);

        var capped = Workspaces("olga", new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:MaxOwned"] = "2" }).Build());
        Assert.Equal("workspace_limit", Http.Code(await capped.Create(new CreateWorkspaceBody("Third", "UTC"))));
    }

    [Fact]
    public async Task The_list_carries_the_look_once_db_005_has_run()
    {
        var list = Assert.IsAssignableFrom<IReadOnlyList<WorkspaceDto>>(((ObjectResult)(await Workspaces("mia").List()).Result!).Value);
        var w = Assert.Single(list);
        Assert.Equal(WorkspaceRules.DefaultColor, w.Color);
        Assert.Equal(3, w.MemberCount);
        Assert.False(w.IsPrimaryOwner);
        features.Invites = false;
        list = Assert.IsAssignableFrom<IReadOnlyList<WorkspaceDto>>(((ObjectResult)(await Workspaces("mia").List()).Result!).Value);
        Assert.Null(list[0].Color);
    }
}

/// <summary>db/005 is paste-ready for Azure SQL.</summary>
public sealed class WorkspaceMigrationTests
{
    private static string Sql => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "005_workspace_invites.sql"));

    [Fact]
    public void The_migration_parses_and_is_azure_ready()
    {
        var sql = Sql;
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\b(CREATE\s+(UNIQUE\s+)?INDEX|CREATE\s+TABLE|ALTER\s+TABLE)\b", RegexOptions.IgnoreCase), outside);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_probes_index_comes_last_and_no_token_column_is_stored_in_plain()
    {
        var sql = Sql;
        var last = Regex.Matches(sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("UX_Invites_Token", last);
        Assert.Contains("UX_Invites_Token", VantraFlow.Api.Data.FlowSchema.InvitesProbeSql);
        Assert.DoesNotContain("definition", VantraFlow.Api.Data.FlowSchema.InvitesProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TokenHash     BINARY(32)", sql);
        Assert.DoesNotMatch(new Regex(@"^\s+Token\s", RegexOptions.Multiline), sql);
    }
}

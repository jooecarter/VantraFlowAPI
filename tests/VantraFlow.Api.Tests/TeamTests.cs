using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Team;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>Folders, tags, owners, comments and the activity log: who may do what (checked on the server), and what's logged.</summary>
public sealed class TeamTests
{
    private readonly FakeWorkspaces db = new();
    private readonly FakeFlows flows = new();
    private readonly FakeTeam team;
    private readonly FakeActivity log = new();
    private readonly FakeMentionMail mail = new();
    private readonly Guid ws;
    private const string Def = """{"trigger":{"app":"schedule","event":"interval","config":{}},"steps":[]}""";

    public TeamTests()
    {
        team = new FakeTeam(flows);
        ws = db.Add("Northwind", ("alex", FlowRoles.Owner), ("priya", FlowRoles.Admin), ("tom", FlowRoles.Member), ("sam", FlowRoles.Member));
        flows.Add(ws, "flw_a", "Welcome new leads", Def, createdBy: "tom");
    }

    private OrganiseController Organise(string? sub) => new(db, team) { ControllerContext = TeamHttp.As(sub, log) };
    private CommentsController Comments(string? sub) => new(db, team, mail, NullLogger<CommentsController>.Instance) { ControllerContext = TeamHttp.As(sub, log) };
    private ActivityController Activity(string? sub) => new(db, team) { ControllerContext = TeamHttp.As(sub, log) };

    // ── Permissions ──

    [Fact]
    public async Task Only_members_see_how_flows_are_organised_and_the_owner_defaults_to_the_creator()
    {
        Assert.Equal(404, Http.Status(await Organise("stranger").Get(ws)));
        var body = Http.Body<OrganiseDto>(await Organise("tom").Get(ws));
        Assert.Equal("tom", Assert.Single(body.Flows).OwnerId);
        Assert.True(body.Can.Edit);
        Assert.False(body.Can.ManageFolders);
        Assert.False(body.Can.Audit);
        Assert.True(Http.Body<OrganiseDto>(await Organise("priya").Get(ws)).Can.Audit);
    }

    [Fact]
    public async Task Before_db_011_everything_answers_not_ready()
    {
        team.Ready = false;
        Assert.Equal(503, Http.Status(await Organise("alex").Get(ws)));
        Assert.Equal("not_ready", Http.Code(await Organise("alex").CreateFolder(ws, new FolderBody("Sales"))));
        Assert.Equal(503, Http.Status(await Comments("tom").List(ws, "flw_a")));
        Assert.Equal(503, Http.Status(await Activity("alex").Workspace(ws, null, null, null, null, null)));
    }

    [Fact]
    public async Task Admins_manage_folders_and_members_only_file_flows_in_them()
    {
        Assert.Equal(403, Http.Status(await Organise("tom").CreateFolder(ws, new FolderBody("Sales"))));
        var made = await Organise("priya").CreateFolder(ws, new FolderBody("  Sales   team "));
        Assert.Equal(201, Http.Status(made));
        var folder = Http.Body<FolderDto>(made);
        Assert.Equal("Sales team", folder.Name);
        Assert.Equal(409, Http.Status(await Organise("alex").CreateFolder(ws, new FolderBody("Sales team"))));
        Assert.Equal(400, Http.Status(await Organise("alex").CreateFolder(ws, new FolderBody(new string('x', 81)))));

        // A member moves a flow in; the move is logged.
        Assert.Equal(204, Http.Status(await Organise("tom").Move(ws, "flw_a", new FlowFolderBody(folder.Id))));
        Assert.Equal(folder.Id, team.Places["flw_a"].Folder);
        var moved = Assert.Single(log.Lines, l => l.Kind == ActivityKinds.FlowMoved);
        Assert.Equal(("tom", "flw_a", "Into “Sales team”"), (moved.ActorId, moved.FlowId, moved.Detail));
        Assert.Equal(404, Http.Status(await Organise("tom").Move(ws, "flw_a", new FlowFolderBody("fld_nope"))));

        // Members can't rename or delete it; deleting puts its flows back at the top.
        Assert.Equal(403, Http.Status(await Organise("tom").RenameFolder(ws, folder.Id, new FolderBody("Mine"))));
        Assert.Equal(403, Http.Status(await Organise("tom").DeleteFolder(ws, folder.Id)));
        Assert.Equal(204, Http.Status(await Organise("alex").DeleteFolder(ws, folder.Id)));
        Assert.Null(team.Places["flw_a"].Folder);
        Assert.Contains(log.Lines, l => l.Kind == ActivityKinds.FolderChanged && l.Detail == "Deleted “Sales team”");
    }

    [Fact]
    public async Task Members_create_and_add_tags_and_admins_change_or_delete_them()
    {
        var made = await Organise("tom").CreateTag(ws, new TagBody("Sales", "blue"));
        Assert.Equal(201, Http.Status(made));
        var tag = Http.Body<TagDto>(made);
        // The same name again is the same tag.
        Assert.Equal(tag.Id, Http.Body<TagDto>(await Organise("sam").CreateTag(ws, new TagBody("sales", "red"))).Id);
        Assert.Equal(400, Http.Status(await Organise("tom").CreateTag(ws, new TagBody("Ops", "#ff00ff"))));

        Assert.Equal(200, Http.Status(await Organise("tom").SetTags(ws, "flw_a", new FlowTagsBody([tag.Id]))));
        Assert.Equal(404, Http.Status(await Organise("tom").SetTags(ws, "flw_a", new FlowTagsBody(["tag_other"]))));
        Assert.Equal(400, Http.Status(await Organise("tom").SetTags(ws, "flw_a", new FlowTagsBody(Enumerable.Range(0, 11).Select(i => $"t{i}").ToList()))));

        Assert.Equal(403, Http.Status(await Organise("tom").UpdateTag(ws, tag.Id, new TagBody("Revenue", null))));
        Assert.Equal(403, Http.Status(await Organise("tom").DeleteTag(ws, tag.Id)));
        Assert.Equal("Revenue", Http.Body<TagDto>(await Organise("priya").UpdateTag(ws, tag.Id, new TagBody("Revenue", null))).Name);
        Assert.Equal(204, Http.Status(await Organise("priya").DeleteTag(ws, tag.Id)));
        Assert.Empty(team.FlowTags);
    }

    [Fact]
    public async Task Only_admins_change_a_flows_owner_and_only_to_a_member()
    {
        Assert.Equal(403, Http.Status(await Organise("tom").SetOwner(ws, "flw_a", new FlowOwnerBody("sam"))));
        Assert.Equal(400, Http.Status(await Organise("priya").SetOwner(ws, "flw_a", new FlowOwnerBody("stranger"))));
        Assert.Equal(200, Http.Status(await Organise("priya").SetOwner(ws, "flw_a", new FlowOwnerBody("sam"))));
        Assert.Equal("sam", team.Places["flw_a"].Owner);
        var line = Assert.Single(log.Lines, l => l.Kind == ActivityKinds.FlowOwner);
        Assert.Equal(("priya", "To sam"), (line.ActorId, line.Detail));
    }

    // ── Comments ──

    [Fact]
    public async Task Members_comment_only_authors_edit_and_authors_or_admins_delete()
    {
        Assert.Equal(404, Http.Status(await Comments("stranger").Add(ws, "flw_a", new CommentBody("Hello"))));
        Assert.Equal(400, Http.Status(await Comments("tom").Add(ws, "flw_a", new CommentBody("   "))));
        Assert.Equal(400, Http.Status(await Comments("tom").Add(ws, "flw_a", new CommentBody(new string('x', 2001)))));
        var made = await Comments("tom").Add(ws, "flw_a", new CommentBody("Can we run this hourly?"));
        Assert.Equal(201, Http.Status(made));
        var c = Http.Body<CommentDto>(made);
        Assert.True(c.CanEdit);

        Assert.Equal("not_author", Http.Code(await Comments("sam").Edit(ws, c.Id, new CommentBody("Changed"))));
        Assert.Equal("not_author", Http.Code(await Comments("alex").Edit(ws, c.Id, new CommentBody("Changed"))));
        Assert.Equal(200, Http.Status(await Comments("tom").Edit(ws, c.Id, new CommentBody("Can we run this every hour?"))));
        Assert.NotNull(team.Comments.Single().EditedAt);

        Assert.Equal(403, Http.Status(await Comments("sam").Delete(ws, c.Id)));
        var seenBySam = Http.Body<List<CommentDto>>(await Comments("sam").List(ws, "flw_a")).Single();
        Assert.False(seenBySam.CanEdit || seenBySam.CanDelete);
        Assert.True(Http.Body<List<CommentDto>>(await Comments("priya").List(ws, "flw_a")).Single().CanDelete);
        Assert.Equal(204, Http.Status(await Comments("priya").Delete(ws, c.Id)));
        Assert.Empty(team.Comments);
    }

    [Fact]
    public async Task Mentions_email_each_member_once_an_hour_per_flow_and_never_the_author()
    {
        await Comments("tom").Add(ws, "flw_a", new CommentBody("@[priya](priya) and @[sam](sam), and me @[tom](tom). @[x](stranger)"));
        Assert.Equal(["priya@example.com", "sam@example.com"], mail.Sent.Select(s => s.To).Order().ToArray());
        Assert.Contains("@priya and @sam", mail.Sent[0].Comment);

        // Within the hour: no second email for the same flow.
        await Comments("alex").Add(ws, "flw_a", new CommentBody("@[priya](priya) see above"));
        Assert.Equal(2, mail.Sent.Count);

        // An edit emails only people it newly mentions.
        var c = team.Comments.First();
        team.Notices.Clear();
        await Comments("tom").Edit(ws, c.Id, new CommentBody(c.Body + " @[alex](alex)"));
        Assert.Equal("alex@example.com", mail.Sent[^1].To);
        Assert.Equal(3, mail.Sent.Count);
    }

    [Fact]
    public void Mention_rules()
    {
        var now = DateTime.UtcNow;
        Assert.True(TeamRules.MentionDue(null, now));
        Assert.False(TeamRules.MentionDue(now.AddMinutes(-59), now));
        Assert.True(TeamRules.MentionDue(now.AddMinutes(-60), now));
        Assert.Equal(["a", "b"], TeamRules.Mentions("@[A](a) @[B](b) @[A again](a)"));
        Assert.Equal("Hi @Priya Shah", TeamRules.Plain("Hi @[Priya Shah](p1)"));
    }

    // ── The activity log ──

    [Fact]
    public async Task Only_owners_and_admins_read_the_workspace_log_a_page_at_a_time()
    {
        for (var i = 0; i < 7; i++)
            team.Activity.Add(new ActivityRow(i + 1, ws, DateTime.UtcNow, i % 2 == 0 ? "tom" : "priya", null, i < 5 ? ActivityKinds.FlowEdited : ActivityKinds.ConnectionAdded,
                i < 5 ? "flw_a" : null, "Welcome new leads", i + 1, null));
        Assert.Equal(403, Http.Status(await Activity("tom").Workspace(ws, null, null, null, null, null)));

        var first = Http.Body<ActivityPageDto>(await Activity("priya").Workspace(ws, null, null, null, null, 3));
        Assert.Equal(["7", "6", "5"], first.Items.Select(a => a.Id).ToArray());
        Assert.Equal("5", first.Next);
        var second = Http.Body<ActivityPageDto>(await Activity("priya").Workspace(ws, null, null, null, first.Next, 3));
        Assert.Equal(["4", "3", "2"], second.Items.Select(a => a.Id).ToArray());

        Assert.Equal(2, Http.Body<ActivityPageDto>(await Activity("alex").Workspace(ws, null, null, "connection", null, null)).Items.Count);
        Assert.All(Http.Body<ActivityPageDto>(await Activity("alex").Workspace(ws, "tom", null, null, null, null)).Items, a => Assert.Equal("tom", a.ActorId));
        Assert.Equal(400, Http.Status(await Activity("alex").Workspace(ws, null, null, "nonsense", null, null)));
        Assert.Equal(400, Http.Status(await Activity("alex").Workspace(ws, null, null, null, "abc", null)));

        // A flow's own history: any member.
        var mine = Http.Body<ActivityPageDto>(await Activity("tom").Flow(ws, "flw_a", null, null));
        Assert.Equal(5, mine.Items.Count);
        Assert.Null(mine.Next);
    }

    [Fact]
    public async Task The_log_writes_nothing_before_db_011_and_never_fails_a_change()
    {
        var writer = new ActivityLog(team, NullLogger<ActivityLog>.Instance);
        team.Ready = false;
        await writer.WriteAsync(new ActivityEntry(ws, "tom", "Tom", ActivityKinds.FlowEdited, "flw_a", "Welcome", 4));
        Assert.Empty(team.Activity);
        team.Ready = true;
        await writer.WriteAsync(new ActivityEntry(ws, "tom", "Tom", ActivityKinds.FlowEdited, "flw_a", new string('n', 300), 4, new string('d', 900)));
        var row = Assert.Single(team.Activity);
        Assert.Equal((4, 160, 400), (row.Version!.Value, row.FlowName!.Length, row.Detail!.Length));
        team.FailActivity = true;
        await writer.WriteAsync(new ActivityEntry(ws, "tom", "Tom", ActivityKinds.FlowDeleted, "flw_a", "Welcome"));
    }

    [Fact]
    public async Task Changing_a_role_and_workspace_settings_are_logged()
    {
        var users = new FakeUsers();
        var members = new MembersController(db, db, users, new FakeMail(), new FakeFeatures(), NullLogger<MembersController>.Instance)
        { ControllerContext = TeamHttp.As("alex", log) };
        Assert.Equal(204, Http.Status(await members.Role(ws, "tom", new UpdateMemberBody("admin"))));
        var line = Assert.Single(log.Lines);
        Assert.Equal((ActivityKinds.MemberRole, "alex", "tom is now admin"), (line.Kind, line.ActorId, line.Detail));

        // A refused change writes nothing.
        var asTom = new MembersController(db, db, users, new FakeMail(), new FakeFeatures(), NullLogger<MembersController>.Instance)
        { ControllerContext = TeamHttp.As("sam", log) };
        Assert.Equal(403, Http.Status(await asTom.Role(ws, "tom", new UpdateMemberBody("member"))));
        Assert.Single(log.Lines);

        var workspaces = new WorkspacesController(db, new ConfigurationBuilder().Build(), new FakeAlerts(), new FakeFeatures(), new FakeTeardown(db),
            NullLogger<WorkspacesController>.Instance) { ControllerContext = TeamHttp.As("alex", log) };
        await workspaces.Update(ws, new UpdateWorkspaceBody("Northwind Trading", null));
        Assert.Equal((ActivityKinds.SettingsChanged, "Changed the name to “Northwind Trading”"), (log.Lines[^1].Kind, log.Lines[^1].Detail));
        await workspaces.Alerts(ws, new WorkspaceAlertsBody(true));
        Assert.Equal("Turned failure emails on", log.Lines[^1].Detail);
    }
}

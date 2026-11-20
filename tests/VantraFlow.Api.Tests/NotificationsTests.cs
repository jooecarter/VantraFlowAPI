using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Notifications;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>Notifications (db/016): the switches, the words, who hears, grouping and email; and Settings → Profile's rules.</summary>
public sealed class NotificationsTests
{
    // ── Fakes ──

    private sealed class Store : INotificationStore
    {
        public bool Ready = true;
        public readonly Dictionary<string, string?> Prefs = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<NotificationRow> Rows = [];
        public (string Name, string Owner)? Flow = ("Log new leads", "olive");
        private long next = 1;

        public Task<bool> ReadyAsync() => Task.FromResult(Ready);
        public Task<IReadOnlyDictionary<string, string?>> PrefsAsync(IReadOnlyCollection<string> ids) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(ids.Where(Prefs.ContainsKey).ToDictionary(i => i, i => Prefs[i]));
        public Task SavePrefsAsync(string vantraId, string json) { Prefs[vantraId] = json; return Task.CompletedTask; }

        public Task<bool> AddAsync(NotificationDraft d, string recipient, DateTime now)
        {
            var i = d.Group is { } g && d.RefKey is not null
                ? Rows.FindIndex(r => r.Recipient == recipient && r.WorkspaceId == d.WorkspaceId && r.Kind == d.Kind && r.RefKey == d.RefKey && r.GroupedAt >= now - g)
                : -1;
            if (i >= 0)
            {
                var r = Rows[i];
                Rows[i] = r with { Times = r.Times + 1, Body = d.BodyMany is null ? d.Body : d.BodyMany.Replace("{n}", (r.Times + 1).ToString()), Link = d.Link, CreatedAt = now, ReadAt = null };
                return Task.FromResult(false);
            }
            Rows.Add(new NotificationRow(next++, d.WorkspaceId, recipient, d.Kind, d.RefKey, d.Lead, d.Title, d.Body, d.Link, 1, now, now, null));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<NotificationRow>> ListAsync(Guid ws, string recipient, long? before, int take, bool unreadOnly, IReadOnlyCollection<string> hidden) =>
            Task.FromResult<IReadOnlyList<NotificationRow>>(Rows.Where(r => r.WorkspaceId == ws && r.Recipient == recipient && !hidden.Contains(r.Kind) && (!unreadOnly || r.ReadAt is null))
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(take).ToList());
        public Task<int> UnreadAsync(Guid ws, string recipient, IReadOnlyCollection<string> hidden) =>
            Task.FromResult(Rows.Count(r => r.WorkspaceId == ws && r.Recipient == recipient && r.ReadAt is null && !hidden.Contains(r.Kind)));
        public Task<bool> MarkAsync(Guid ws, string recipient, long id, bool read, DateTime now)
        {
            var i = Rows.FindIndex(r => r.Id == id && r.Recipient == recipient && r.WorkspaceId == ws);
            if (i < 0) return Task.FromResult(false);
            Rows[i] = Rows[i] with { ReadAt = read ? now : null };
            return Task.FromResult(true);
        }
        public Task<int> MarkAllReadAsync(Guid ws, string recipient, DateTime now)
        {
            var n = 0;
            for (var i = 0; i < Rows.Count; i++)
                if (Rows[i].WorkspaceId == ws && Rows[i].Recipient == recipient && Rows[i].ReadAt is null) { Rows[i] = Rows[i] with { ReadAt = now }; n++; }
            return Task.FromResult(n);
        }
        public Task ResolveAsync(string kind, string refKey, DateTime now)
        {
            for (var i = 0; i < Rows.Count; i++) if (Rows[i].Kind == kind && Rows[i].RefKey == refKey) Rows[i] = Rows[i] with { ReadAt = now };
            return Task.CompletedTask;
        }
        public Task<(string Name, string Owner)?> FlowOwnerAsync(Guid ws, string flowId) => Task.FromResult(Flow);
        public Task PruneAsync(DateTime before) => Task.CompletedTask;
    }

    private sealed class Mail : INotifyMail
    {
        public readonly List<(string To, string Subject, string Html)> Sent = [];
        public bool CanSend() => true;
        public Task<bool> SendAsync(Guid ws, string to, string name, string subject, string html) { Sent.Add((to, subject, html)); return Task.FromResult(true); }
    }

    private static (Notifier Notifier, Store Store, Mail Mail, FakeWorkspaces Workspaces, Guid Ws) Make()
    {
        var workspaces = new FakeWorkspaces();
        var ws = workspaces.Add("Northwind Trading", ("olive", FlowRoles.Owner), ("adam", FlowRoles.Admin), ("mia", FlowRoles.Member));
        var store = new Store();
        var mail = new Mail();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://flow.example.com" }).Build();
        return (new Notifier(store, workspaces, mail, config, NullLogger<Notifier>.Instance), store, mail, workspaces, ws);
    }

    // ── Switches ──

    [Fact]
    public void The_defaults_are_the_mocks_everything_in_the_app_and_email_for_approvals_failures_and_pauses()
    {
        var d = NotificationPrefs.Defaults();
        Assert.All(d.Values, p => Assert.True(p.App));
        Assert.True(d[NotificationKinds.Approval].Email);
        Assert.True(d[NotificationKinds.FlowFailed].Email);
        Assert.True(d[NotificationKinds.FlowPaused].Email);
        Assert.False(d[NotificationKinds.Connection].Email);
        Assert.False(d[NotificationKinds.ReportSent].Email);
        Assert.False(d[NotificationKinds.Members].Email);
    }

    [Fact]
    public void Switches_merge_over_what_is_stored_and_ignore_what_they_dont_know()
    {
        var stored = NotificationPrefs.Parse("""{"members":{"app":false},"nonsense":{"app":false},"approval":{"email":"yes"}}""");
        Assert.False(stored[NotificationKinds.Members].App);
        Assert.True(stored[NotificationKinds.Approval].Email); // not a boolean: the default
        var next = NotificationPrefs.Merge(stored, JsonNode.Parse("""{"connection":{"email":true},"flow_failed":{"app":false}}""") as JsonObject);
        Assert.True(next[NotificationKinds.Connection].Email);
        Assert.False(next[NotificationKinds.FlowFailed].App);
        Assert.True(next[NotificationKinds.FlowFailed].Email);
        Assert.False(next[NotificationKinds.Members].App);
        var round = NotificationPrefs.Parse(NotificationPrefs.Serialize(next));
        Assert.Equal(next, round);
        Assert.Equal(NotificationPrefs.Defaults(), NotificationPrefs.Parse("not json"));
    }

    // ── Words ──

    [Fact]
    public void The_words_read_as_the_mock()
    {
        var ws = Guid.NewGuid();
        var a = NotificationRules.Approval(ws, "apr_1", "Approve big refunds", "£1,240 to Harbour & Co\nRef 42");
        Assert.Equal("is waiting for you · £1,240 to Harbour & Co", a.Body);
        Assert.Equal("/approvals", a.Link);
        Assert.Equal("is waiting for you", NotificationRules.Approval(ws, "apr_1", "Approve", null).Body);
        Assert.Equal("failed 3 times and was paused", NotificationRules.PausedAfter(3));
        Assert.Equal("was paused: it started more than 30 runs in a minute, which looks like a loop",
            NotificationRules.PausedBecause("It started more than 30 runs in a minute, which looks like a loop."));
        var c = NotificationRules.Connection(ws, "c1", "Google Sheets", 0);
        Assert.Equal("The Google Sheets connection needs signing in again", NotificationRules.Text(c.Lead, c.Title, c.Body));
        Assert.Equal("sent to 4 people", NotificationRules.ReportSent(ws, "f", "Weekly sales report", "r", 4).Body);
        Assert.Equal("sent to 1 person", NotificationRules.ReportSent(ws, "f", "Weekly sales report", "r", 1).Body);
        Assert.Equal("joined Northwind Trading", NotificationRules.Member(ws, "p", "Priya Shah", "Northwind Trading", "joined").Body);
        Assert.Equal($"https://flow.example.com/runs/r1?ws={ws:D}", NotificationRules.Url("https://flow.example.com", ws, "/runs/r1"));
        Assert.Equal($"https://flow.example.com/settings?tab=members&ws={ws:D}", NotificationRules.Url("https://flow.example.com", ws, "/settings?tab=members"));
        Assert.Equal($"https://flow.example.com/inbox?ws={ws:D}", NotificationRules.Url("https://flow.example.com", ws, "https://evil.example.com"));
    }

    [Fact]
    public void The_email_encodes_everything()
    {
        var d = NotificationRules.Member(Guid.NewGuid(), "p", "<b>Priya</b>", "A & B", "joined");
        var (subject, html) = NotificationRules.Email(d, "A & B", "https://flow.example.com");
        Assert.Equal("<b>Priya</b> joined A & B", subject);
        Assert.Contains("&lt;b&gt;Priya&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Priya", html);
    }

    // ── Who hears ──

    [Fact]
    public async Task Failures_go_to_the_owner_and_an_hours_worth_is_one_note_counted_up()
    {
        var (n, store, mail, _, ws) = Make();
        await n.FlowFailedAsync(ws, "flw_1", "run_1");
        await n.FlowFailedAsync(ws, "flw_1", "run_2");
        await n.FlowFailedAsync(ws, "flw_1", "run_3");
        var row = Assert.Single(store.Rows);
        Assert.Equal("olive", row.Recipient);
        Assert.Equal(3, row.Times);
        Assert.Equal("failed 3 times", row.Body);
        Assert.Equal("/runs/run_3", row.Link);
        Assert.Empty(mail.Sent); // the failure alert's email is the email
    }

    [Fact]
    public async Task Pauses_reach_the_owner_and_managers_once_each_and_email_by_their_switch()
    {
        var (n, store, mail, _, ws) = Make();
        store.Flow = ("Log new leads", "mia");
        store.Prefs["adam"] = """{"flow_paused":{"email":false}}""";
        await n.FlowPausedAsync(ws, "flw_1", NotificationRules.PausedAfter(3));
        Assert.Equal(["mia", "olive", "adam"], store.Rows.Select(r => r.Recipient));
        Assert.Equal(["mia@example.com", "olive@example.com"], mail.Sent.Select(s => s.To));
        Assert.Contains("Log new leads failed 3 times and was paused", mail.Sent[0].Subject);
        // Again within the hour: one note each, no second email.
        await n.FlowPausedAsync(ws, "flw_1", NotificationRules.PausedAfter(3));
        Assert.Equal(3, store.Rows.Count);
        Assert.Equal(2, mail.Sent.Count);
    }

    [Fact]
    public async Task Members_news_skips_whoever_did_it_and_the_person_and_non_members()
    {
        var (n, store, mail, _, ws) = Make();
        await n.MemberAsync(ws, "adam", "Adam", "left", "adam");
        Assert.Equal(["olive"], store.Rows.Select(r => r.Recipient));
        Assert.Empty(mail.Sent); // members' email is off by default
        await n.RaiseAsync(NotificationRules.Member(ws, "x", "X", "W", "joined"), ["stranger", null, ""]);
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task A_kind_switched_off_in_the_app_is_kept_for_its_email_but_hidden_from_the_list()
    {
        var (n, store, mail, _, ws) = Make();
        store.Prefs["olive"] = """{"connection":{"app":false,"email":true}}""";
        await n.ConnectionAsync(ws, Guid.NewGuid(), "Google Sheets", "mia", 2);
        await n.ConnectionAsync(ws, Guid.Parse("00000000-0000-0000-0000-000000000001"), "Slack", "mia", 0);
        Assert.Equal(2, mail.Sent.Count(m => m.To == "olive@example.com"));
        var hidden = NotificationPrefs.Parse(store.Prefs["olive"]).Where(p => !p.Value.App).Select(p => p.Key).ToList();
        Assert.Empty(await store.ListAsync(ws, "olive", null, 30, false, hidden));
        Assert.Equal(2, (await store.ListAsync(ws, "mia", null, 30, false, [])).Count);
        Assert.Equal("connection needs signing in again. 2 flows are paused", store.Rows.First(r => r.Recipient == "mia").Body);
    }

    [Fact]
    public async Task Nothing_is_raised_before_db016_and_emails_are_wanted_as_before()
    {
        var (n, store, mail, _, ws) = Make();
        store.Ready = false;
        await n.FlowPausedAsync(ws, "flw_1", "was paused");
        await n.ApprovalAsync(ws, "apr_1", "Approve", null, ["olive"]);
        Assert.Empty(store.Rows);
        Assert.Empty(mail.Sent);
        Assert.True(await n.WantsEmailAsync("olive", NotificationKinds.Approval));
        store.Ready = true;
        store.Prefs["olive"] = """{"approval":{"email":false}}""";
        Assert.False(await n.WantsEmailAsync("olive", NotificationKinds.Approval));
    }

    [Fact]
    public async Task A_decided_approval_is_read_for_everyone()
    {
        var (n, store, _, _, ws) = Make();
        await n.ApprovalAsync(ws, "apr_1", "Approve big refunds", null, ["olive", "adam"]);
        Assert.Equal(2, await store.UnreadAsync(ws, "olive", []) + await store.UnreadAsync(ws, "adam", []));
        await n.ApprovalDoneAsync("apr_1");
        Assert.All(store.Rows, r => Assert.NotNull(r.ReadAt));
    }

    [Fact]
    public void A_report_counts_who_its_emails_went_to_only_for_a_scheduled_flow_with_a_report()
    {
        var def = FlowDefinition.Parse("""
            {"trigger":{"app":"schedule","event":"weekly"},"steps":[
              {"id":"s1","type":"action","app":"orbit","action":"report.get"},
              {"id":"s2","type":"action","app":"email","action":"send"},
              {"id":"s3","type":"action","app":"email","action":"send"}]}
            """)!;
        var steps = JsonNode.Parse("""
            {"s1":{"dashboard":"Sales"},"s2":{"to":["a@example.com","b@example.com"],"sent":2},"s3":{"to":["B@example.com","c@example.com","d@example.com"],"sent":3}}
            """)!.AsObject();
        Assert.Equal(4, Notifier.ReportRecipients(def, steps));
        def.Trigger.App = "orbit";
        Assert.Equal(0, Notifier.ReportRecipients(def, steps));
        def.Trigger.App = "schedule";
        def.Steps.RemoveAt(0);
        Assert.Equal(0, Notifier.ReportRecipients(def, steps));
    }

    [Fact]
    public async Task Auto_pause_hands_its_email_to_notifications_once_they_are_in()
    {
        var (n, store, mail, _, ws) = Make();
        var rel = new RelStore();
        var relMail = new RelMail();
        var r = new Reliability(rel, relMail, new FlowLimits(), "https://flow.example.com", NullLogger.Instance, n);
        Assert.True(await r.AfterFailureAsync(ws, "flw_1", "Boom", DateTime.UtcNow));
        Assert.Empty(relMail.Sent);
        Assert.Equal("failed 3 times and was paused", store.Rows[0].Body);
        Assert.NotEmpty(mail.Sent);

        store.Ready = false;
        var rel2 = new RelStore();
        Assert.True(await new Reliability(rel2, relMail, new FlowLimits(), "https://flow.example.com", NullLogger.Instance, n).AfterFailureAsync(ws, "flw_1", "Boom", DateTime.UtcNow));
        Assert.Single(relMail.Sent); // the owners' email, as before
    }

    private sealed class RelStore : IReliabilityStore
    {
        private AutoPauseFlow? flow = new("flw_1", "Log new leads", "Northwind", true, 3, DateTime.UtcNow.AddDays(-1));
        public Task<IReadOnlyList<DigestWorkspace>> DigestCandidatesAsync(DateTime d) => Task.FromResult<IReadOnlyList<DigestWorkspace>>([]);
        public Task<bool> ClaimDigestAsync(Guid ws, DateTime d) => Task.FromResult(false);
        public Task<IReadOnlyList<DigestRun>> FailedRunsAsync(Guid ws, DateTime s, int t) => Task.FromResult<IReadOnlyList<DigestRun>>([]);
        public Task<IReadOnlyList<MailContact>> RecipientsAsync(Guid ws, bool admins) => Task.FromResult<IReadOnlyList<MailContact>>([new("owner@example.com", "Olive")]);
        public Task<bool> CountEmailAsync(Guid ws, DateTime now, int cap) => Task.FromResult(true);
        public Task<AutoPauseFlow?> AutoPauseOfAsync(Guid ws, string id) => Task.FromResult(flow);
        public Task<IReadOnlyList<string>> LastStatusesAsync(Guid ws, string id, DateTime s, int t) =>
            Task.FromResult<IReadOnlyList<string>>([RunStatuses.Failed, RunStatuses.Failed, RunStatuses.Failed]);
        public Task<bool> PauseAsync(Guid ws, string id, string reason) { var was = flow?.Enabled == true; flow = flow! with { Enabled = false }; return Task.FromResult(was); }
    }

    private sealed class RelMail : IReliabilityMail
    {
        public readonly List<string> Sent = [];
        public bool CanSend() => true;
        public Task<bool> SendAsync(string to, string name, string subject, string html) { Sent.Add(to); return Task.FromResult(true); }
    }

    [Fact]
    public void Once_notifications_are_in_the_default_failure_alert_goes_to_the_flows_owner()
    {
        Assert.Contains("ISNULL(f.AlertTo, ISNULL(f.OwnerId, f.CreatedBy))", AlertsRepository.DueOwnerSql);
        Assert.Equal(2, Regex.Matches(AlertsRepository.DueOwnerSql, "OwnerId").Count);
        Assert.DoesNotContain("OwnerId", AlertsRepository.DueSql);
    }

    // ── Profile ──

    [Fact]
    public void A_profile_is_checked_and_its_zone_kept_in_the_identitys_shape()
    {
        var (ok, err) = ProfileRules.Check(new ProfileController.ProfileBody("  Alex ", " Morgan ", " Operations lead ", "Europe/London", null));
        Assert.Null(err);
        Assert.Equal("Alex", ok!.FirstName);
        Assert.Equal("Morgan", ok.LastName);
        Assert.Equal("Operations lead", ok.JobTitle);
        Assert.Equal("Europe/London (GMT)", ok.TimeZone);
        Assert.Null(ok.Photo);
        Assert.Equal("Europe/London", ProfileRules.ZoneOf("Europe/London (GMT)"));
        Assert.Equal("Asia/Tokyo (GMT+9)", ProfileRules.Label("Asia/Tokyo"));
        Assert.NotNull(ProfileRules.Check(new ProfileController.ProfileBody("", "Morgan", null, null, null)).Error);
        Assert.NotNull(ProfileRules.Check(new ProfileController.ProfileBody("Alex", null, null, "Mars/Olympus", null)).Error);
        Assert.NotNull(ProfileRules.Check(new ProfileController.ProfileBody("Alex", null, null, null, "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")).Error);
        Assert.Equal("", ProfileRules.Check(new ProfileController.ProfileBody("Alex", null, null, "", "")).Profile!.TimeZone);
    }

    // ── Migration ──

    [Fact]
    public void The_notifications_migration_parses_is_azure_ready_stops_without_db001_and_db011_and_makes_the_probes_index_last()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "016_notifications.sql"));
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\b(CREATE|ALTER)\s+(TABLE|INDEX)\b", RegexOptions.IgnoreCase), outside);
        Assert.Contains("THROW 51016", sql);
        Assert.Contains("OwnerId", sql);
        var last = Regex.Matches(sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("IX_Notifications_Recipient", last);
        Assert.Contains(last, FlowSchema.NotificationsProbeSql);
        Assert.DoesNotContain("definition", FlowSchema.NotificationsProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
    }
}

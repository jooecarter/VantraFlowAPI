using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

internal sealed class FakeStore : IEngineStore
{
    public readonly List<FlowRow> Flows = [];
    public readonly HashSet<string> Keys = [];
    public readonly List<RunRow> Runs = [];
    public readonly List<string> Paused = [];
    public int Quota = int.MaxValue;
    public int Counted;
    public int RecentForFlow;
    public WorkspaceRow Workspace = new(Guid.NewGuid(), "Acme", "UTC", "owner", null, DateTime.UtcNow);

    public Task<IReadOnlyList<FlowRow>> CandidatesAsync(FlowEventRow ev) => Task.FromResult<IReadOnlyList<FlowRow>>(Flows);
    public Task<WorkspaceRow?> WorkspaceAsync(Guid workspaceId) => Task.FromResult<WorkspaceRow?>(Workspace);
    public Task<bool> ClaimKeyAsync(Guid workspaceId, string flowId, string key) => Task.FromResult(Keys.Add(flowId + "|" + key));
    public Task<bool> CountRunAsync(Guid workspaceId, DateTime now, int quota) => Task.FromResult(++Counted <= Quota);
    public Task<int> RunsSinceAsync(Guid workspaceId, string? flowId, DateTime since) => Task.FromResult(flowId is null ? 0 : RecentForFlow);
    public Task<bool> PauseAsync(Guid workspaceId, string flowId, string reason) { Paused.Add(flowId); return Task.FromResult(true); }
    public Task InsertRunAsync(RunRow run) { Runs.Add(run); return Task.CompletedTask; }
}

public sealed class EventProcessorTests
{
    private static FlowRow Flow(string id, FlowDefinition def) =>
        new(id, Guid.NewGuid(), "Welcome email", null, true, def.ToJson(), 3, def.Trigger.App, def.Trigger.Event, "tbl_leads", null, null, null, null,
            "u", DateTime.UtcNow, "u", DateTime.UtcNow, null, null);

    private static FlowEventRow Event(bool flowCaused = false, string kind = "record.created", JsonObject? payload = null) =>
        new(1, "fev_1", Guid.NewGuid(), "orbit", kind, "tbl_leads", null, (payload ?? Build.Trigger()).ToJsonString(), flowCaused, false, null, DateTime.UtcNow, 1);

    [Fact]
    public async Task Starts_a_run_from_a_snapshot_once_per_event()
    {
        var store = new FakeStore();
        store.Flows.Add(Flow("flw_1", Build.Flow(Build.Email("s1"))));
        var p = new EventProcessor(store, new FlowLimits());

        var outcome = await p.ProcessAsync(Event());
        await p.ProcessAsync(Event()); // the same event again (a redelivery)

        Assert.True(outcome.Done);
        var run = Assert.Single(store.Runs);
        Assert.Equal(RunStatuses.Queued, run.Status);
        Assert.Equal("0", run.Position);
        Assert.Equal(3, run.FlowVersion);
        Assert.Contains("ada@example.com", run.TriggerData);
    }

    [Fact]
    public async Task Changes_Flow_made_itself_start_nothing_unless_the_flow_opts_in()
    {
        var store = new FakeStore();
        var plain = Build.Flow(Build.Email("s1"));
        var optedIn = Build.Flow(Build.Email("s1"));
        optedIn.Options.RunOnFlowChanges = true;
        store.Flows.Add(Flow("flw_plain", plain));
        store.Flows.Add(Flow("flw_opted", optedIn));

        await new EventProcessor(store, new FlowLimits()).ProcessAsync(Event(flowCaused: true));

        Assert.Equal(["flw_opted"], store.Runs.Select(r => r.FlowId));
    }

    [Fact]
    public async Task The_loop_breaker_pauses_a_runaway_flow()
    {
        var store = new FakeStore { RecentForFlow = 60 };
        store.Flows.Add(Flow("flw_1", Build.Flow(Build.Email("s1"))));
        await new EventProcessor(store, new FlowLimits(FlowRunsPerMinute: 60)).ProcessAsync(Event());
        Assert.Empty(store.Runs);
        Assert.Equal(["flw_1"], store.Paused);
    }

    [Fact]
    public async Task Over_the_monthly_quota_a_run_is_logged_as_skipped()
    {
        var store = new FakeStore { Quota = 0 };
        store.Flows.Add(Flow("flw_1", Build.Flow(Build.Email("s1"))));
        await new EventProcessor(store, new FlowLimits(MonthlyRuns: 100)).ProcessAsync(Event());
        var run = Assert.Single(store.Runs);
        Assert.Equal(RunStatuses.Skipped, run.Status);
        Assert.Contains("100 runs", run.Error);
    }
}

public sealed class TriggerMatcherTests
{
    private static FlowEventRow Ev(string source, string kind, string? scope, JsonObject payload, bool flowCaused = false, string? flowId = null) =>
        new(1, "fev_1", Guid.Empty, source, kind, scope, flowId, payload.ToJsonString(), flowCaused, false, null, DateTime.UtcNow, 1);

    private static JsonObject Updated(string field, string from, string to)
    {
        var t = Build.Trigger(to);
        t["event"] = "record.updated";
        t["changes"] = new JsonArray(new JsonObject { ["fieldId"] = "fld_stage", ["field"] = field, ["from"] = from, ["to"] = to });
        return t;
    }

    [Fact]
    public void Record_updated_can_watch_chosen_fields()
    {
        var def = Build.Flow(Build.Email("s1"));
        def.Trigger = new TriggerDef { App = "orbit", Event = "record.updated", Config = new JsonObject { ["tableId"] = "tbl_leads", ["fieldIds"] = new JsonArray("fld_stage") } };
        var ev = Updated("Stage", "Lead", "Won");
        Assert.NotNull(TriggerMatcher.Match(def, "f", Ev("orbit", "record.updated", "tbl_leads", ev), ev));
        def.Trigger.Config["fieldIds"] = new JsonArray("fld_owner");
        Assert.Null(TriggerMatcher.Match(def, "f", Ev("orbit", "record.updated", "tbl_leads", ev), ev));
        Assert.Null(TriggerMatcher.Match(def, "f", Ev("orbit", "record.updated", "tbl_other", ev), ev));
    }

    [Fact]
    public void Record_matches_fires_only_when_the_condition_becomes_true()
    {
        var def = Build.Flow(Build.Email("s1"));
        def.Trigger = new TriggerDef
        {
            App = "orbit", Event = "record.matches",
            Config = new JsonObject
            {
                ["tableId"] = "tbl_leads",
                ["condition"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject { ["left"] = "{{trigger.record.fields.Stage}}", ["op"] = "equals", ["right"] = "Won" }) },
            },
        };
        var becameWon = Updated("Stage", "Lead", "Won");
        Assert.NotNull(TriggerMatcher.Match(def, "f", Ev("orbit", "record.updated", "tbl_leads", becameWon), becameWon));
        var stillWon = Updated("Name", "Ada", "Ada L");
        Assert.Null(TriggerMatcher.Match(def, "f", Ev("orbit", "record.updated", "tbl_leads", stillWon), stillWon));
        var created = Build.Trigger("Won");
        Assert.NotNull(TriggerMatcher.Match(def, "f", Ev("orbit", "record.created", "tbl_leads", created), created));
    }

    [Fact]
    public void Projects_triggers_can_be_limited_to_a_project()
    {
        var pid = Guid.NewGuid().ToString();
        var def = Build.Flow(Build.Email("s1"));
        def.Trigger = new TriggerDef { App = "projects", Event = "task.completed", Config = new JsonObject { ["projectId"] = pid } };
        var payload = new JsonObject { ["event"] = "task.completed", ["task"] = new JsonObject { ["id"] = "t", ["projectId"] = pid } };
        Assert.NotNull(TriggerMatcher.Match(def, "f", Ev("projects", "task.completed", null, payload), payload));
        payload["task"]!["projectId"] = Guid.NewGuid().ToString();
        Assert.Null(TriggerMatcher.Match(def, "f", Ev("projects", "task.completed", null, payload), payload));
        Assert.Null(TriggerMatcher.Match(def, "f", Ev("projects", "task.created", null, payload), payload));
    }

    [Fact]
    public void A_targeted_event_goes_to_its_flow_only()
    {
        var def = Build.Flow(Build.Email("s1"));
        var payload = new JsonObject { ["scheduledFor"] = "2026-11-10T09:00:00Z" };
        Assert.NotNull(TriggerMatcher.Match(def, "flw_a", Ev("schedule", "daily", null, payload, flowId: "flw_a"), payload));
        Assert.Null(TriggerMatcher.Match(def, "flw_b", Ev("schedule", "daily", null, payload, flowId: "flw_a"), payload));
    }

    [Fact]
    public void The_origin_marker_is_this_connections_key()
    {
        var payload = new JsonObject { ["source"] = new JsonObject { ["kind"] = "api", ["keyId"] = "key_1" } };
        Assert.True(IncomingController.IsFlowOrbit(payload, "key_1"));
        Assert.False(IncomingController.IsFlowOrbit(payload, "key_2"));
        payload["source"]!["kind"] = "app";
        Assert.False(IncomingController.IsFlowOrbit(payload, "key_1"));
    }
}

public sealed class SecretsTests
{
    private static FlowSecrets Secrets() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 40) }).Build());

    [Fact]
    public void A_sealed_token_opens_only_for_its_own_row()
    {
        var s = Secrets();
        var ws = Guid.NewGuid();
        var sealedKey = s.Protect("orbk_live_secret", ws, "row1");
        Assert.Equal("orbk_live_secret", s.Unprotect(sealedKey, ws, "row1"));
        Assert.Null(s.Unprotect(sealedKey, ws, "row2"));
        Assert.Null(s.Unprotect(sealedKey, Guid.NewGuid(), "row1"));
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(sealedKey));
    }

    [Fact]
    public void Signatures_are_checked_with_a_five_minute_window()
    {
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var now = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        var ts = new DateTimeOffset(now).ToUnixTimeSeconds();
        var sig = FlowSecrets.Sign("whsec_x", ts, body);
        Assert.True(FlowSecrets.VerifyTimestamped("whsec_x", sig, ts.ToString(), body, now.AddMinutes(4)));
        Assert.False(FlowSecrets.VerifyTimestamped("whsec_x", sig, ts.ToString(), body, now.AddMinutes(6)));
        Assert.False(FlowSecrets.VerifyTimestamped("whsec_y", sig, ts.ToString(), body, now));
        Assert.False(FlowSecrets.VerifyTimestamped("whsec_x", sig, ts.ToString(), Encoding.UTF8.GetBytes("{\"a\":2}"), now));
    }
}

public sealed class StepActionsTests
{
    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://localhost:8080/")]
    [InlineData("ftp://example.com/")]
    [InlineData("https://user:pass@example.com/")]
    public void The_http_step_refuses_private_and_odd_addresses(string url) =>
        Assert.NotNull(StepActions.CheckUrl(url, out _));

    [Fact]
    public void The_http_step_accepts_a_public_https_url() => Assert.Null(StepActions.CheckUrl("https://example.com/hook", out _));

    [Fact]
    public void The_guard_knows_public_from_private() =>
        Assert.True(NetworkGuard.IsPublic(System.Net.IPAddress.Parse("93.184.216.34")) && !NetworkGuard.IsPublic(System.Net.IPAddress.Parse("192.168.1.1")));

    [Fact]
    public void Recipients_are_checked_and_capped()
    {
        Assert.Equal(["a@example.com", "b@example.com"], StepActions.Recipients("a@example.com; b@example.com, a@example.com").List);
        Assert.NotNull(StepActions.Recipients("nobody").Error);
        Assert.NotNull(StepActions.Recipients(string.Join(",", Enumerable.Range(0, 11).Select(i => $"p{i}@example.com"))).Error);
    }

    [Fact]
    public void An_email_body_is_escaped_with_its_lines_kept() =>
        Assert.Contains("&lt;script&gt;<br>Line two", StepActions.Html("<script>\nLine two"));
}

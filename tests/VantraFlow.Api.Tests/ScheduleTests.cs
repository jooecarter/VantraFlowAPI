using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Schedule;
using VantraFlow.Api.Services.Triggers;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>The Schedule page: occurrences by the worker's own rule, the board it builds, and who sees it.</summary>
public sealed class ScheduleTests
{
    private static DateTime U(int y, int m, int d, int h = 0, int mi = 0) => new(y, m, d, h, mi, 0, DateTimeKind.Utc);

    private static TriggerDef T(string ev, object config) => new()
    {
        App = "schedule", Event = ev, Config = JsonNode.Parse(JsonSerializer.Serialize(config))!.AsObject(),
    };

    // ── occurrences ──

    [Fact]
    public void Occurrences_are_the_slots_the_worker_fires_in_the_workspace_zone()
    {
        var weekdays = T("weekly", new { time = "17:00", days = new[] { "mon", "tue", "wed", "thu", "fri" } });
        var runs = ScheduleRules.Occurrences(weekdays, "Europe/London", U(2026, 11, 16), U(2026, 11, 30));
        Assert.Equal(10, runs.Count);
        Assert.Equal(U(2026, 11, 16, 17), runs[0]);
        Assert.Equal("Every weekday at 17:00", ScheduleRules.Describe(weekdays));
        // The same minutes the worker's DueSlots fires, over a whole day.
        var every15 = T("interval", new { every = 15, unit = "minutes" });
        Assert.Equal(ScheduleRules.DueSlots(every15, "Europe/London", U(2026, 11, 17), TimeSpan.FromDays(1)),
            ScheduleRules.Occurrences(every15, "Europe/London", U(2026, 11, 16), U(2026, 11, 17)));
    }

    [Fact]
    public void Occurrences_keep_the_local_time_across_both_clock_changes()
    {
        var daily = T("daily", new { time = "08:00" });
        // London: 08:00 BST is 07:00 UTC until 25 Oct 2026, then 08:00 GMT.
        Assert.Equal([U(2026, 10, 24, 7), U(2026, 10, 25, 8), U(2026, 10, 26, 8)], ScheduleRules.Occurrences(daily, "Europe/London", U(2026, 10, 24), U(2026, 10, 26, 23)));
        // A time the spring change skips (01:30 on 29 March 2026) doesn't fire; one the autumn change repeats fires twice, as the worker does.
        var night = T("daily", new { time = "01:30" });
        Assert.Equal([U(2026, 3, 28, 1, 30), U(2026, 3, 30, 0, 30)], ScheduleRules.Occurrences(night, "Europe/London", U(2026, 3, 28), U(2026, 3, 30, 23)));
        Assert.Equal([U(2026, 10, 25, 0, 30), U(2026, 10, 25, 1, 30)], ScheduleRules.Occurrences(night, "Europe/London", U(2026, 10, 25), U(2026, 10, 25, 12)));
        Assert.Equal("Every Monday at 08:00", ScheduleRules.Describe(T("weekly", new { time = "08:00", days = new[] { "mon" } })));
        Assert.Equal("Monthly on the 1st at 09:00", ScheduleRules.Describe(T("monthly", new { time = "09:00", day = 1 })));
        Assert.Equal("Every hour", ScheduleRules.Describe(T("interval", new { every = 1, unit = "hours" })));
    }

    [Fact]
    public void A_frequent_schedule_shows_its_first_run_each_day_and_how_many_more()
    {
        var hourly = T("interval", new { every = 1, unit = "hours" });
        var runs = ScheduleRules.Occurrences(hourly, "Europe/London", U(2026, 11, 16, 9, 30), U(2026, 11, 17, 23, 59));
        var days = ScheduleBoard.ByDay(runs, ScheduleRules.Zone("Europe/London")).OfType<JsonObject>().ToList();
        Assert.Equal(2, days.Count);
        Assert.Equal("2026-11-16T10:00:00Z", days[0]["at"]!.GetValue<string>());
        Assert.Equal(13, days[0]["more"]!.GetValue<int>()); // 10:00 … 23:00
        Assert.Equal(24, days[1]["count"]!.GetValue<int>());
        // The window cap.
        Assert.Equal(50, ScheduleRules.Occurrences(T("interval", new { every = 5, unit = "minutes" }), "UTC", U(2026, 11, 16), U(2026, 12, 16), max: 50).Count);
        // Days are the viewer's: 23:30 UTC is the next day in Sydney.
        var late = ScheduleBoard.ByDay([U(2026, 11, 16, 23, 30)], ScheduleRules.Zone("Australia/Sydney")).OfType<JsonObject>().Single();
        Assert.Equal(0, late["more"]!.GetValue<int>());
    }

    // ── the board ──

    private sealed class Sources : IScheduleSources
    {
        public List<FlowRow> Flows { get; } = [];
        public bool Connected { get; set; } = true;
        public Func<JsonObject>? Orbit { get; set; }
        public int OrbitCalls { get; private set; }
        public int DateCalls { get; private set; }
        public Task<IReadOnlyList<FlowRow>> FlowsAsync(Guid workspaceId) => Task.FromResult<IReadOnlyList<FlowRow>>(Flows);
        public Task<DateUpcoming> DatesAsync(Guid workspaceId, FlowRow flow, FlowDefinition def, string timeZone, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            DateCalls++;
            return Task.FromResult(new DateUpcoming([U(2026, 11, 16, 18), U(2026, 11, 16, 18), U(2026, 11, 16, 18), U(2026, 11, 20, 9)], "Renewal date"));
        }
        public Task<bool> OrbitConnectedAsync(Guid workspaceId) => Task.FromResult(Connected);
        public Task<JsonObject> OrbitReportsAsync(Guid workspaceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            OrbitCalls++;
            return Task.FromResult(Orbit!());
        }
    }

    private static readonly Guid WsId = Guid.Parse("4e2f0b8c-3c1e-4a7b-9b1f-5d6e7f809a1b");
    private static readonly WorkspaceRow Ws = new(WsId, "Northwind", "Europe/London", "u-owner", null, U(2026, 1, 1));

    private static FlowRow Flow(string id, string name, object trigger, bool enabled = true, object[]? steps = null) => new(
        id, WsId, name, null, enabled, JsonSerializer.Serialize(new { trigger, steps = steps ?? [] }), 3, "schedule", "weekly", null, null, null, null,
        enabled ? null : "Switched off", "u-owner", U(2026, 10, 1), "u-owner", U(2026, 11, 3, 10), null, null);

    private static JsonObject OrbitAnswer() => new()
    {
        ["available"] = true,
        ["schedules"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "rps_weekly", ["title"] = "Weekly sales report", ["dashboardName"] = "Sales Dashboard", ["summary"] = "Mondays 08:30", ["rangeLabel"] = "Last week",
                ["recipientCount"] = 3, ["enabled"] = true, ["canChange"] = true, ["link"] = "https://orbit.example.com/dashboards/db?schedule=rps_weekly",
                ["runs"] = new JsonArray("2026-11-23T08:30:00.000Z", "2026-12-28T08:30:00.000Z"),
                ["emails"] = new JsonArray("finance@example.com"), // an Orbit that sent too much: dropped
            },
            new JsonObject { ["id"] = "rps_board", ["title"] = "Board pack", ["enabled"] = false, ["pausedAt"] = "2026-10-28T09:00:00.000Z", ["canChange"] = false, ["runs"] = new JsonArray() }),
    };

    private static (ScheduleBoard Board, Sources Src) Board()
    {
        var src = new Sources { Orbit = OrbitAnswer };
        src.Flows.Add(Flow("flw_eod", "End-of-day summary", new { app = "schedule", @event = "weekly", config = new { time = "17:00", days = new[] { "mon", "tue", "wed", "thu", "fri" } } },
            steps: [new { id = "s1", type = "action", app = "slack", action = "message.send", config = new { } }]));
        src.Flows.Add(Flow("flw_ren", "Renewal reminders", new { app = "orbit", @event = "date.relative", config = new { tableId = "t1", field = "f1", amount = 14, unit = "days", direction = "before" } }));
        src.Flows.Add(Flow("flw_old", "Old lead digest", new { app = "schedule", @event = "daily", config = new { time = "09:00" } }, enabled: false));
        src.Flows.Add(Flow("flw_hook", "On a webhook", new { app = "webhook", @event = "catch", config = new { } }));
        return (new ScheduleBoard(src, new ScheduleCache()), src);
    }

    private static JsonObject Build(ScheduleBoard b) =>
        b.BuildAsync(WsId, Ws, U(2026, 11, 16), U(2026, 11, 30), "Europe/London", U(2026, 11, 16, 12), CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void The_board_lists_flows_dates_and_reports_and_what_is_paused_never_an_address()
    {
        var (board, _) = Board();
        var o = Build(board);
        var items = ((JsonArray)o["items"]!).OfType<JsonObject>().ToList();
        Assert.Equal(["flow:flw_eod", "date:flw_ren", "report:rps_weekly"], items.Select(i => $"{i["kind"]}:{i["id"]}"));
        Assert.True(items[0]["sends"]!.GetValue<bool>());
        Assert.Equal(10, ((JsonArray)items[0]["runs"]!).Count);
        Assert.Equal("14 days before Renewal date", items[1]["summary"]!.GetValue<string>());
        var due = ((JsonArray)items[1]["runs"]!).OfType<JsonObject>().ToList();
        Assert.Equal([3, 1], due.Select(d => d["count"]!.GetValue<int>()));
        // Orbit's runs outside the window are dropped; a recipient count, never an address.
        Assert.Single((JsonArray)items[2]["runs"]!);
        Assert.Equal(3, items[2]["recipientCount"]!.GetValue<int>());
        Assert.DoesNotContain("@", o.ToJsonString());
        var paused = ((JsonArray)o["paused"]!).OfType<JsonObject>().ToList();
        Assert.Equal(["flow:flw_old", "report:rps_board"], paused.Select(i => $"{i["kind"]}:{i["id"]}"));
        Assert.False(paused[1]["canResume"]!.GetValue<bool>());
        Assert.True(o["orbit"]!["available"]!.GetValue<bool>());
    }

    [Fact]
    public void Orbit_unreachable_not_ready_or_not_connected_shows_the_rest()
    {
        var (board, src) = Board();
        src.Orbit = () => throw new AppUnavailableException("Vantra Orbit couldn't be reached.");
        var o = Build(board);
        Assert.Equal("unreachable", o["orbit"]!["reason"]!.GetValue<string>());
        Assert.Single((JsonArray)o["notices"]!);
        Assert.Equal(2, ((JsonArray)o["items"]!).Count);

        (board, src) = Board();
        src.Orbit = () => new JsonObject { ["available"] = false, ["reason"] = "not_ready", ["schedules"] = new JsonArray() };
        o = Build(board);
        Assert.Equal("not_ready", o["orbit"]!["reason"]!.GetValue<string>());
        Assert.Empty((JsonArray)o["notices"]!);

        (board, src) = Board();
        src.Orbit = () => throw new AppRefusedException(404, "Not found", "not_found"); // an Orbit from before the schedules route
        Assert.Equal("not_ready", Build(board)["orbit"]!["reason"]!.GetValue<string>());

        (board, src) = Board();
        src.Connected = false;
        o = Build(board);
        Assert.False(o["orbit"]!["connected"]!.GetValue<bool>());
        Assert.Equal(0, src.OrbitCalls);
    }

    [Fact]
    public void Orbit_and_the_date_scans_are_cached_briefly()
    {
        var (board, src) = Board();
        Build(board);
        Build(board);
        Assert.Equal(1, src.OrbitCalls);
        Assert.Equal(1, src.DateCalls);
    }

    [Fact]
    public void Run_now_asks_first_only_when_a_flow_sends_something()
    {
        FlowDefinition Def(object steps) => FlowDefinition.Parse(JsonSerializer.Serialize(new { trigger = new { app = "schedule", @event = "daily" }, steps }))!;
        Assert.True(ScheduleBoard.Sends(Def(new[] { new { id = "s", type = "action", app = "email", action = "send" } })));
        Assert.True(ScheduleBoard.Sends(Def(new[] { new { id = "s", type = "action", app = "microsoft", action = "teams.post" } })));
        Assert.False(ScheduleBoard.Sends(Def(new[] { new { id = "s", type = "action", app = "orbit", action = "record.find" } })));
    }

    // ── who sees it ──

    [Fact]
    public void Only_members_of_the_workspace_see_its_schedule()
    {
        var db = new FakeWorkspaces();
        var ws = db.Add("Northwind", ("u-owner", FlowRoles.Owner), ("u-member", FlowRoles.Member));
        var (board, _) = Board();
        ScheduleController As(string sub) => new(db, board, new ScheduleCache(), null!, null!, null!, null!, null!, null!, null!) { ControllerContext = Http.As(sub) };
        Assert.Equal(404, Http.Status(As("u-stranger").Get(ws, null, null, null, CancellationToken.None).GetAwaiter().GetResult()));
        Assert.Equal(404, Http.Status(As("u-stranger").ReportSend(ws, "rps_weekly", CancellationToken.None).GetAwaiter().GetResult()));
        Assert.Equal(404, Http.Status(As("u-stranger").ReportEnabled(ws, "rps_weekly", new(false), CancellationToken.None).GetAwaiter().GetResult()));
        Assert.Equal(404, Http.Status(As("u-stranger").RunNow(ws, "flw_eod").GetAwaiter().GetResult()));
        var ok = As("u-member").Get(ws, "2026-11-16T00:00:00Z", "2026-11-30T00:00:00Z", "Europe/London", CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(200, Http.Status(ok));
        Assert.Equal(3, ((JsonArray)((JsonObject)((OkObjectResult)ok).Value!)["items"]!).Count);
    }
}

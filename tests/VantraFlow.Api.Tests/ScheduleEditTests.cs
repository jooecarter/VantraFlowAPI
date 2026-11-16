using System.Text.Json;
using System.Text.Json.Nodes;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Schedule;
using VantraFlow.Api.Services.Team;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>The Schedule page's edit panel: only the timing changes, saved as the builder saves (a version kept), and what it can't show.</summary>
public sealed class ScheduleEditTests
{
    private static readonly Guid WsId = Guid.Parse("4e2f0b8c-3c1e-4a7b-9b1f-5d6e7f809a1b");

    private static FlowDefinition Def(object trigger) => FlowDefinition.Parse(JsonSerializer.Serialize(new
    {
        trigger,
        steps = new object[] { new { id = "s1", type = "action", app = "slack", action = "message.send", config = new { channel = "C1", text = "Hi" } } },
    }))!;

    private static FlowDefinition Eod() => Def(new { app = "schedule", @event = "weekly", config = new { time = "17:00", days = new[] { "mon", "tue", "wed", "thu", "fri" } } });

    private static FlowDefinition Renewal() => Def(new
    {
        app = "orbit", @event = "date.relative",
        config = new { tableId = "t1", field = "f1", amount = 14, unit = "days", direction = "before", time = "18:00", zone = "Europe/London",
            condition = new { match = "all", rules = new[] { new { left = "{{trigger.record.fields.Status}}", op = "equals", right = "Active" } } } },
    });

    private static JsonObject Change(string ev, object config) => new() { ["event"] = ev, ["config"] = JsonSerializer.SerializeToNode(config) };

    [Fact]
    public void A_schedule_s_days_and_time_change_and_nothing_else()
    {
        var def = Eod();
        var r = ScheduleEdit.Apply(def, Change("weekly", new { time = "9:15", days = new[] { "sun", "mon", "mon", "fri", "nope" } }), 3, 3);
        Assert.Null(r.Code);
        var t = r.Definition!.Trigger;
        Assert.Equal(("schedule", "weekly"), (t.App, t.Event));
        Assert.Equal("09:15", t.Config["time"]!.GetValue<string>());
        Assert.Equal(["mon", "fri", "sun"], ((JsonArray)t.Config["days"]!).Select(d => d!.GetValue<string>()));
        Assert.Equal(JsonSerializer.Serialize(def.Steps, FlowDefinition.Json), JsonSerializer.Serialize(r.Definition.Steps, FlowDefinition.Json));
        Assert.Equal("Every Mon, Fri and Sun at 09:15", ScheduleRules.Describe(t));
        // The original is untouched.
        Assert.Equal("17:00", def.Trigger.Config["time"]!.GetValue<string>());

        // Every day, every hour, every month on day N; old keys don't linger.
        var hourly = ScheduleEdit.Apply(def, Change("interval", new { time = "10:00" }), 3, 3).Definition!.Trigger;
        Assert.Equal("Every hour", ScheduleRules.Describe(hourly));
        Assert.Null(hourly.Config["time"]);
        Assert.Null(hourly.Config["days"]);
        var monthly = ScheduleEdit.Apply(def, Change("monthly", new { time = "08:00", day = 15 }), 3, 3).Definition!.Trigger;
        Assert.Equal("Monthly on the 15th at 08:00", ScheduleRules.Describe(monthly));
        Assert.Equal("Every day at 06:30", ScheduleRules.Describe(ScheduleEdit.Apply(def, Change("daily", new { time = "06:30" }), 3, 3).Definition!.Trigger));

        // Refused: no day, no time, a day past 31, a cadence the panel doesn't offer.
        Assert.Equal("Choose at least one day.", ScheduleEdit.Apply(def, Change("weekly", new { time = "09:00", days = Array.Empty<string>() }), 3, 3).Error);
        Assert.Equal(400, ScheduleEdit.Apply(def, Change("daily", new { time = "25:00" }), 3, 3).Status);
        Assert.Equal(400, ScheduleEdit.Apply(def, Change("monthly", new { time = "09:00", day = 32 }), 3, 3).Status);
        Assert.Equal(400, ScheduleEdit.Apply(def, Change("cron", new { expression = "*/5 * * * *" }), 3, 3).Status);
    }

    [Fact]
    public void A_date_trigger_keeps_its_table_zone_and_only_if()
    {
        var def = Renewal();
        var r = ScheduleEdit.Apply(def, Change("date.relative", new { amount = 2, unit = "weeks", direction = "after", field = "f2", time = "09:00", tableId = "t-other" }), 3, 3);
        Assert.Null(r.Code);
        var c = r.Definition!.Trigger.Config;
        Assert.Equal(2, c["amount"]!.GetValue<int>());
        Assert.Equal("weeks", c["unit"]!.GetValue<string>());
        Assert.Equal("after", c["direction"]!.GetValue<string>());
        Assert.Equal("f2", c["field"]!.GetValue<string>());
        Assert.Equal("09:00", c["time"]!.GetValue<string>());
        Assert.Equal("t1", c["tableId"]!.GetValue<string>());
        Assert.Equal("Europe/London", c["zone"]!.GetValue<string>());
        Assert.NotNull(c["condition"]);
        Assert.Empty(Services.Triggers.DateRules.Check(r.Definition.Trigger));
        // On the day: no amount.
        var on = ScheduleEdit.Apply(def, Change("date.relative", new { amount = 5, unit = "days", direction = "on", field = "f1", time = "08:00" }), 3, 3).Definition!.Trigger.Config;
        Assert.Equal(0, on["amount"]!.GetValue<int>());
        // Refused: minutes, no amount, no field, or turning it into another trigger.
        Assert.Equal("Choose days or weeks.", ScheduleEdit.Apply(def, Change("date.relative", new { amount = 2, unit = "minutes", direction = "before", field = "f1", time = "08:00" }), 3, 3).Error);
        Assert.Equal(400, ScheduleEdit.Apply(def, Change("date.relative", new { amount = 0, unit = "days", direction = "before", field = "f1", time = "08:00" }), 3, 3).Status);
        Assert.Equal(400, ScheduleEdit.Apply(def, Change("date.relative", new { amount = 2, unit = "days", direction = "before", field = "", time = "08:00" }), 3, 3).Status);
        Assert.Equal(400, ScheduleEdit.Apply(def, Change("daily", new { time = "08:00" }), 3, 3).Status);
        // An overdue trigger: its time only.
        var overdue = Def(new { app = "projects", @event = "task.overdue", config = new { time = "09:00" } });
        Assert.Equal("07:30", ScheduleEdit.Apply(overdue, Change("task.overdue", new { time = "07:30", amount = 3 }), 1, 1).Definition!.Trigger.Config["time"]!.GetValue<string>());
        Assert.Null(ScheduleEdit.Apply(overdue, Change("task.overdue", new { time = "07:30", amount = 3 }), 1, 1).Definition!.Trigger.Config["amount"]);
    }

    [Fact]
    public void A_schedule_the_panel_can_t_show_is_read_only_and_refused()
    {
        var every15 = Def(new { app = "schedule", @event = "interval", config = new { every = 15, unit = "minutes" } });
        var every6h = Def(new { app = "schedule", @event = "interval", config = new { every = 6, unit = "hours" } });
        var lastDay = Def(new { app = "schedule", @event = "monthly", config = new { day = "last", time = "09:00" } });
        var cron = Def(new { app = "schedule", @event = "cron", config = new { expression = "0 9 * * 1-5" } });
        var byMinutes = Def(new { app = "orbit", @event = "date.relative", config = new { tableId = "t1", field = "f1", amount = 30, unit = "minutes", direction = "before" } });
        var webhook = Def(new { app = "webhook", @event = "catch", config = new { } });
        foreach (var d in new[] { every15, every6h, lastDay, cron, byMinutes, webhook })
        {
            Assert.False(ScheduleEdit.Supported(d.Trigger));
            var r = ScheduleEdit.Apply(d, Change("daily", new { time = "09:00" }), 1, 1);
            Assert.Equal((409, "unsupported"), (r.Status, r.Code));
            Assert.Equal("This schedule can't be changed here. Edit it in the flow.", r.Error);
        }
        Assert.True(ScheduleEdit.Supported(Def(new { app = "schedule", @event = "interval", config = new { every = 1, unit = "hours" } }).Trigger));
        Assert.True(ScheduleEdit.Supported(Eod().Trigger));
        Assert.True(ScheduleEdit.Supported(Renewal().Trigger));
    }

    [Fact]
    public void A_flow_saved_meanwhile_is_refused_and_any_member_may_edit()
    {
        var r = ScheduleEdit.Apply(Eod(), Change("daily", new { time = "09:00" }), 3, 4);
        Assert.Equal((409, "changed"), (r.Status, r.Code));
        Assert.Null(r.Definition);
        Assert.Equal(409, ScheduleEdit.Apply(Eod(), Change("daily", new { time = "09:00" }), null, 4).Status);
        // The builder's rights: every member edits flows; someone who isn't one doesn't.
        Assert.True(TeamRules.CanEdit(FlowRoles.Member));
        Assert.True(TeamRules.CanEdit(FlowRoles.Admin));
        Assert.False(TeamRules.CanEdit(null));
        Assert.False(TeamRules.CanEdit("viewer"));
    }

    [Fact]
    public void Saving_goes_through_the_builder_s_path_and_keeps_a_version()
    {
        var def = Eod();
        var row = new FlowRow("flw_eod", WsId, "End-of-day summary", null, true, def.ToJson(), 3, "schedule", "weekly", null, null, null, null, null,
            "u-owner", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), "u-owner", new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), null, null);
        var next = ScheduleEdit.Apply(def, Change("weekly", new { time = "17:30", days = new[] { "mon", "wed" } }), row.Version, row.Version).Definition!;
        // The builder's checks pass, and its Shape keeps the row's trigger columns in step.
        Assert.Empty(DefinitionRules.Validate(next, new FlowLimits()));
        var shaped = FlowsController.Shape(row, next);
        Assert.Equal(("schedule", "weekly"), (shaped.TriggerApp, shaped.TriggerEvent));
        // The version the save keeps: the new definition, its steps, and the panel's note.
        var saved = shaped with { Version = row.Version + 1 };
        var v = FlowsController.VersionOf(saved, ScheduleEdit.Note, "u-sam", "Sam Patel", new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc));
        Assert.Equal((4, "Changed on the Schedule page", 1, "Sam Patel"), (v.Version, v.Note, v.StepCount, v.SavedByName));
        var kept = FlowDefinition.Parse(v.Definition)!;
        Assert.Equal("17:30", kept.Trigger.Config["time"]!.GetValue<string>());
        Assert.Equal("Every Mon and Wed at 17:30", ScheduleRules.Describe(kept.Trigger));
        Assert.Equal("message.send", kept.Steps[0].Action);
    }

    [Fact]
    public void Orbit_s_answer_reaches_the_panel_with_masked_addresses_only()
    {
        var orbit = new JsonObject
        {
            ["schedule"] = new JsonObject
            {
                ["id"] = "rps_weekly", ["title"] = "Weekly sales report", ["dashboardName"] = "Sales Dashboard", ["cadence"] = "week", ["weekday"] = 1, ["time"] = "08:30",
                ["range"] = "lastWeek", ["message"] = "Morning all", ["widgetIds"] = new JsonArray("w1"), ["version"] = "638990", ["canChange"] = true,
                ["link"] = "javascript:alert(1)", ["ownerId"] = "u-alex", ["sentTo"] = "[\"e:finance@example.com\"]",
                ["recipients"] = new JsonObject
                {
                    ["members"] = new JsonArray(new JsonObject { ["vantraId"] = "u-sam", ["name"] = "Sam Patel", ["email"] = "sam@example.com" }),
                    ["external"] = new JsonArray(new JsonObject { ["id"] = "r0", ["masked"] = "f•••@example.com" }, new JsonObject { ["id"] = "r1", ["masked"] = "full@example.com" }),
                    ["emails"] = new JsonArray("finance@example.com"),
                },
            },
            ["details"] = false,
            ["cards"] = new JsonArray(new JsonObject { ["id"] = "w1", ["title"] = "Revenue won" }),
        };
        var view = ScheduleEdit.ReportView(orbit, new JsonObject { ["members"] = new JsonArray(new JsonObject { ["vantraId"] = "u-jo", ["name"] = "Jo Reyes", ["email"] = "jo@example.com" }) });
        var json = view.ToJsonString();
        foreach (var leak in new[] { "finance@", "sam@", "jo@", "full@", "sentTo", "ownerId" }) Assert.DoesNotContain(leak, json);
        Assert.Single((JsonArray)view["schedule"]!["recipients"]!["external"]!);
        Assert.Null(view["schedule"]!["link"]);
        // Before db/075 (details false): no message or cards for the panel.
        Assert.False(((JsonObject)view["schedule"]!).ContainsKey("message"));
        Assert.Equal("Jo Reyes", view["members"]![0]!["name"]!.GetValue<string>());

        // A change carries the known fields only.
        var change = ScheduleEdit.ReportChange(new JsonObject
        {
            ["version"] = "638990", ["time"] = "09:00", ["ownerId"] = "u-me", ["dashboardId"] = "db-other",
            ["recipients"] = new JsonObject { ["members"] = new JsonArray("u-sam", 7), ["keep"] = new JsonArray("r0"), ["add"] = new JsonArray("new@example.com"), ["emails"] = new JsonArray("x@example.com") },
        });
        Assert.False(change.ContainsKey("ownerId"));
        Assert.False(change.ContainsKey("dashboardId"));
        Assert.False(change.ContainsKey("message"));
        Assert.Equal("""{"members":["u-sam"],"keep":["r0"],"add":["new@example.com"]}""", change["recipients"]!.ToJsonString());
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Triggers;

namespace VantraFlow.Api.Services.Schedule;

/// <summary>A change from the Schedule page's panel, worked out: the definition to save, or why not (<see cref="Code"/>).</summary>
public sealed record ScheduleChange(FlowDefinition? Definition, int Status, string? Code, string? Error)
{
    public static ScheduleChange Refused(int status, string code, string error) => new(null, status, code, error);
}

/// <summary>
/// Editing when a flow runs from the Schedule page. Only the trigger's timing changes — a schedule's cadence, days and time; a
/// date trigger's amount, unit, before / on / after, date field and time; an overdue trigger's time — everything else in the
/// flow (its steps, a date trigger's table, zone and "Only if") is kept. The flow is then saved through the builder's own path
/// (<c>FlowsController.SaveAsync</c>): the same checks, and a version kept (db/003) with <see cref="Note"/>.
/// <para>
/// The panel offers what it can show: every day, on these days, every hour, and every month on day N; days or weeks for a
/// date. Anything else (every 15 minutes, every 6 hours, the last day of the month, minutes or hours before a date) is
/// <see cref="Supported"/> false: shown read-only, with a way to the flow, and refused here.
/// </para>
/// </summary>
public static class ScheduleEdit
{
    public const string Note = "Changed on the Schedule page";
    private static readonly string[] CadenceKeys = ["time", "days", "day", "every", "unit"];
    private static readonly string[] DateKeys = ["amount", "unit", "direction", "field", "time"];

    /// <summary>Whether the panel can show and change this trigger's timing.</summary>
    public static bool Supported(TriggerDef t)
    {
        var c = t.Config;
        if (t.App == "schedule")
            return t.Event switch
            {
                "daily" => ScheduleRules.Time(c["time"]) is not null,
                "weekly" => ScheduleRules.Time(c["time"]) is not null && ScheduleRules.DaysOf(c["days"]).Count > 0,
                "monthly" => ScheduleRules.Time(c["time"]) is not null && ScheduleRules.DayOfMonth(c["day"]) is >= 1,
                "interval" => Tokens.Text(c["unit"]) == "hours" && IntOf(c["every"]) == 1,
                _ => false,
            };
        if (t.Event == DateRules.Overdue) return t.App == "projects";
        if (!DateRules.IsDateTrigger(t)) return false;
        var direction = Tokens.Text(c["direction"]);
        return direction is "" or DateRules.On || Tokens.Text(c["unit"]) is "" or "days" or "weeks";
    }

    private static int? IntOf(JsonNode? n) =>
        int.TryParse(Tokens.Text(n).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static string? TimeOf(JsonNode? n) => ScheduleRules.Time(n) is { } t ? t.ToString("hh\\:mm", CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// The change asked for — <c>{ event, config }</c>, the panel's fields only — on the flow as saved at <paramref name="currentVersion"/>.
    /// Refused: someone else saved it since (409 <c>changed</c>), the panel can't show its trigger (409 <c>unsupported</c>), or a field
    /// isn't right (400 <c>invalid</c>). Anyone who can edit the flow may (<see cref="Team.TeamRules.CanEdit"/>), checked by the caller.
    /// </summary>
    public static ScheduleChange Apply(FlowDefinition def, JsonObject? change, int? version, int currentVersion)
    {
        if (version != currentVersion)
            return ScheduleChange.Refused(409, "changed", "Someone saved this flow meanwhile. Close it and open it again to see their changes.");
        var t = def.Trigger;
        if (!Supported(t))
            return ScheduleChange.Refused(409, "unsupported", "This schedule can't be changed here. Edit it in the flow.");
        if (change is null) return ScheduleChange.Refused(400, "invalid", "Say when it runs.");
        var ev = Tokens.Text(change["event"]);
        var given = change["config"] as JsonObject ?? [];
        var config = (JsonObject)t.Config.DeepClone();

        if (t.App == "schedule")
        {
            foreach (var k in CadenceKeys) config.Remove(k);
            var time = TimeOf(given["time"]);
            switch (ev)
            {
                case "daily":
                case "weekly":
                case "monthly":
                    if (time is null) return ScheduleChange.Refused(400, "invalid", "Choose a time, like 09:00.");
                    config["time"] = time;
                    if (ev == "weekly")
                    {
                        var days = ScheduleRules.DaysOf(given["days"]);
                        if (days.Count == 0) return ScheduleChange.Refused(400, "invalid", "Choose at least one day.");
                        // In the week's order, Monday first.
                        config["days"] = new JsonArray(ScheduleRules.Days.Skip(1).Append(ScheduleRules.Days[0]).Where(days.Contains).Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
                    }
                    if (ev == "monthly")
                    {
                        if (IntOf(given["day"]) is not { } day || day is < 1 or > 31) return ScheduleChange.Refused(400, "invalid", "Choose a day of the month, 1 to 31.");
                        config["day"] = day;
                    }
                    break;
                case "interval":
                    config["every"] = 1;
                    config["unit"] = "hours";
                    break;
                default:
                    return ScheduleChange.Refused(400, "invalid", "Choose every day, these days, every hour or every month.");
            }
        }
        else
        {
            if (ev != t.Event) return ScheduleChange.Refused(400, "invalid", "A date trigger stays a date trigger. Change it in the flow.");
            var time = TimeOf(given["time"]);
            if (time is null) return ScheduleChange.Refused(400, "invalid", "Choose a time, like 09:00.");
            if (t.Event == DateRules.Overdue) config["time"] = time;
            else
            {
                foreach (var k in DateKeys) config.Remove(k);
                var direction = Tokens.Text(given["direction"]);
                if (direction is not (DateRules.Before or DateRules.On or DateRules.After)) return ScheduleChange.Refused(400, "invalid", "Choose before, on the day or after.");
                if (direction == DateRules.On) { config["amount"] = 0; config["unit"] = "days"; }
                else
                {
                    if (IntOf(given["amount"]) is not { } amount || amount is < 1 or > DateRules.MaxAmount)
                        return ScheduleChange.Refused(400, "invalid", $"Choose how long before or after: 1 to {DateRules.MaxAmount:N0}.");
                    var unit = Tokens.Text(given["unit"]);
                    if (unit is not ("days" or "weeks")) return ScheduleChange.Refused(400, "invalid", "Choose days or weeks.");
                    config["amount"] = amount;
                    config["unit"] = unit;
                }
                config["direction"] = direction;
                config["time"] = time;
                if (t.App == "orbit")
                {
                    var field = Tokens.Text(given["field"]).Trim();
                    if (field.Length is 0 or > 64) return ScheduleChange.Refused(400, "invalid", "Choose the date field.");
                    config["field"] = field;
                }
            }
        }

        var next = FlowDefinition.Parse(def.ToJson())!;
        next.Trigger = new TriggerDef { App = t.App, Event = t.App == "schedule" ? ev : t.Event, Config = config };
        return new(next, 200, null, null);
    }

    // ── Orbit's report schedules ──

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static JsonNode? Copy(JsonNode? n) => n?.DeepClone();

    /// <summary>
    /// Orbit's answer for the panel, with only the fields it shows: an outside recipient only as its mask and id (Orbit sends
    /// nothing else; anything that looks like a full address is dropped all the same).
    /// </summary>
    public static JsonObject ReportView(JsonObject orbit, JsonObject? membersAnswer)
    {
        var s = orbit["schedule"] as JsonObject ?? [];
        var to = s["recipients"] as JsonObject ?? [];
        var link = Text(s["link"]);
        var schedule = new JsonObject
        {
            ["id"] = Text(s["id"]), ["title"] = Text(s["title"]), ["dashboardId"] = Text(s["dashboardId"]), ["dashboardName"] = Text(s["dashboardName"]),
            ["cadence"] = Text(s["cadence"]), ["weekday"] = Copy(s["weekday"]), ["monthDay"] = Copy(s["monthDay"]), ["time"] = Text(s["time"]),
            ["timeZone"] = Text(s["timeZone"]), ["zoneLabel"] = Text(s["zoneLabel"]), ["range"] = Text(s["range"]),
            ["attachPdf"] = Copy(s["attachPdf"]), ["showSummary"] = Copy(s["showSummary"]), ["enabled"] = Copy(s["enabled"]),
            ["nextRunAt"] = Text(s["nextRunAt"]), ["version"] = Text(s["version"]), ["canChange"] = Copy(s["canChange"]),
            ["link"] = link is not null && link.StartsWith("https://", StringComparison.Ordinal) ? link : null,
            ["recipients"] = new JsonObject
            {
                ["members"] = new JsonArray((to["members"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(m => (JsonNode)new JsonObject { ["vantraId"] = Text(m["vantraId"]), ["name"] = Text(m["name"]) }).ToArray()),
                ["external"] = new JsonArray((to["external"] as JsonArray ?? []).OfType<JsonObject>()
                    .Where(e => Text(e["masked"]) is { } m && m.Contains('•') && Text(e["id"]) is { Length: > 0 and <= 8 })
                    .Select(e => (JsonNode)new JsonObject { ["id"] = Text(e["id"]), ["masked"] = Text(e["masked"]) }).ToArray()),
            },
        };
        var details = orbit["details"] is JsonValue dv && dv.TryGetValue<bool>(out var d) && d;
        if (details)
        {
            schedule["message"] = Text(s["message"]);
            schedule["widgetIds"] = Copy(s["widgetIds"]);
        }
        return new JsonObject
        {
            ["schedule"] = schedule,
            ["details"] = details,
            ["cards"] = new JsonArray((orbit["cards"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(c => (JsonNode)new JsonObject { ["id"] = Text(c["id"]), ["title"] = Text(c["title"]) }).ToArray()),
            ["limits"] = Copy(orbit["limits"]),
            ["members"] = new JsonArray((membersAnswer?["members"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(m => (JsonNode)new JsonObject { ["vantraId"] = Text(m["vantraId"]), ["name"] = Text(m["name"]) }).ToArray()),
        };
    }

    private static readonly string[] ReportFields =
        ["version", "title", "message", "widgetIds", "cadence", "weekday", "monthDay", "time", "range", "attachPdf", "showSummary"];

    /// <summary>The panel's change as Orbit takes it: the known fields only (Orbit checks each), the recipients as members, ids to keep and new addresses.</summary>
    public static JsonObject ReportChange(JsonObject body)
    {
        var o = new JsonObject();
        foreach (var k in ReportFields) if (body.ContainsKey(k)) o[k] = Copy(body[k]);
        var to = body["recipients"] as JsonObject ?? [];
        static JsonArray Strings(JsonNode? n, int max) =>
            new((n as JsonArray ?? []).Select(Text).Where(x => x is { Length: > 0 and <= 254 }).Take(max).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        o["recipients"] = new JsonObject { ["members"] = Strings(to["members"], 100), ["keep"] = Strings(to["keep"], 100), ["add"] = Strings(to["add"], 100) };
        return o;
    }
}

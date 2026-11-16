using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Triggers;

namespace VantraFlow.Api.Services.Schedule;

/// <summary>What the Schedule page reads, so it can be tested without a database or the other apps.</summary>
public interface IScheduleSources
{
    Task<IReadOnlyList<FlowRow>> FlowsAsync(Guid workspaceId);
    /// <summary>A date flow's runs in the window (one per record) and its field's name. Throws the app exceptions.</summary>
    Task<DateUpcoming> DatesAsync(Guid workspaceId, FlowRow flow, FlowDefinition def, string timeZone, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    /// <summary>Whether the workspace has a live Orbit connection.</summary>
    Task<bool> OrbitConnectedAsync(Guid workspaceId);
    /// <summary>Orbit's report schedules, as the person who connected it sees them. Throws the app exceptions.</summary>
    Task<JsonObject> OrbitReportsAsync(Guid workspaceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

public sealed class ScheduleSources(FlowsRepository flows, DateTriggers dates, Connections connections, ConnectionsRepository connectionRows) : IScheduleSources
{
    public Task<IReadOnlyList<FlowRow>> FlowsAsync(Guid workspaceId) => flows.ListAsync(workspaceId);

    public Task<DateUpcoming> DatesAsync(Guid workspaceId, FlowRow flow, FlowDefinition def, string timeZone, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        dates.UpcomingAsync(workspaceId, def, timeZone, fromUtc, toUtc, ct);

    public async Task<bool> OrbitConnectedAsync(Guid workspaceId) => await connectionRows.LiveAsync(workspaceId, "orbit") is not null;

    public Task<JsonObject> OrbitReportsAsync(Guid workspaceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        connections.CallAsync(workspaceId, "orbit", (api, key) => ((OrbitApi)api).ReportSchedulesAsync(key, fromUtc, toUtc, ct));
}

/// <summary>
/// A short cache for what the Schedule page asks the other apps (Orbit's report schedules, a date flow's records): 60 seconds
/// for an answer, 15 when the app didn't answer, per workspace and window. Holds counts and times only, never record data.
/// </summary>
public sealed class ScheduleCache(TimeProvider? clock = null)
{
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Failed = TimeSpan.FromSeconds(15);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTime Until, object Value)> items = new(StringComparer.Ordinal);

    public async Task<T> GetAsync<T>(string key, Func<Task<T>> load, Func<T, bool>? failed = null) where T : notnull
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (items.TryGetValue(key, out var hit) && hit.Until > now && hit.Value is T t) return t;
        var value = await load();
        items[key] = (now + (failed?.Invoke(value) == true ? Failed : Fresh), value);
        if (items.Count > 5_000)
            foreach (var (k, v) in items) if (v.Until <= now) items.TryRemove(k, out _);
        return value;
    }

    /// <summary>Forget a workspace's answers (after a change made from the page).</summary>
    public void Forget(Guid workspaceId)
    {
        var prefix = workspaceId.ToString("N") + ":";
        foreach (var k in items.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) items.TryRemove(k, out _);
    }
}

/// <summary>
/// The Schedule page: everything set to run on its own in a window — schedule flows' slots (the worker's own rule,
/// <see cref="ScheduleRules.Occurrences"/>), date flows' runs (the builder's "Coming up" scan, counts only), and Orbit's report
/// schedules (as the connection's person sees them in Orbit; a count of recipients, never an address). Runs are grouped by day
/// in the viewer's zone: a schedule that runs often shows its first run each day and how many more. Paused schedule and date
/// flows, and paused reports, are listed apart. An app that can't be reached leaves its part out with a notice.
/// </summary>
public sealed class ScheduleBoard(IScheduleSources sources, ScheduleCache cache)
{
    /// <summary>The widest window a page asks for.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(45);
    /// <summary>The most flows scanned for dates in one go (each asks its app).</summary>
    public const int MaxDateFlows = 40;

    /// <summary>The actions that send something to people (Run now asks first).</summary>
    private static readonly HashSet<string> SendingActions = new(StringComparer.Ordinal)
    {
        "send", "mail.send", "gmail.send", "message.send", "dm.send", "teams.post", "invoice.send", "sequence.add", "request",
    };

    public static bool Sends(FlowDefinition def) => def.AllSteps().Any(s =>
        s.Type == StepTypes.Approval || (s.Type == StepTypes.Action && (s.App is "email" or "http" || (s.Action is { } a && SendingActions.Contains(a)))));

    /// <summary>A run on a day: the day's first time, and how many more that day.</summary>
    public static JsonArray ByDay(IEnumerable<DateTime> utc, TimeZoneInfo zone, Func<DateTime, int>? weight = null)
    {
        var o = new JsonArray();
        foreach (var day in utc.OrderBy(t => t).GroupBy(t => TimeZoneInfo.ConvertTimeFromUtc(t, zone).Date))
        {
            var list = day.ToList();
            o.Add(new JsonObject
            {
                ["at"] = Formatter.IsoUtc(list[0]),
                ["count"] = weight is null ? list.Count : list.Sum(weight),
                ["more"] = list.Count - 1,
            });
        }
        return o;
    }

    private static string Apps(FlowDefinition def) =>
        string.Join(",", def.AllSteps().Where(s => s.Type == StepTypes.Action && !string.IsNullOrEmpty(s.App)).Select(s => s.App!).Distinct().Take(4));

    public static string DateSummary(TriggerDef t, string? fieldName)
    {
        var spec = DateRules.Read(t, "UTC");
        if (t.Event == DateRules.Overdue) return t.App == "projects" ? "Projects · a task becomes overdue" : "A task becomes overdue";
        var field = string.IsNullOrWhiteSpace(fieldName) ? "the date" : fieldName;
        var prefix = t.App == "projects" ? "Projects · " : "";
        if (spec.Direction == DateRules.On) return $"{prefix}On the day of {field}";
        var unit = spec.Amount == 1 ? spec.Unit.TrimEnd('s') : spec.Unit;
        return $"{prefix}{spec.Amount} {unit} {spec.Direction} {field}";
    }

    public async Task<JsonObject> BuildAsync(Guid workspaceId, WorkspaceRow ws, DateTime fromUtc, DateTime toUtc, string viewZone, DateTime nowUtc, CancellationToken ct)
    {
        if (toUtc < fromUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        if (toUtc - fromUtc > MaxWindow) toUtc = fromUtc + MaxWindow;
        var zone = ScheduleRules.Zone(viewZone);
        var items = new JsonArray();
        var paused = new JsonArray();
        var notices = new JsonArray();

        var flows = await sources.FlowsAsync(workspaceId);
        var dateFlows = 0;
        foreach (var f in flows.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (FlowDefinition.Parse(f.Definition) is not { } def) continue;
            var t = def.Trigger;
            var isSchedule = t.App == "schedule";
            var isDate = DateRules.IsDateTrigger(t);
            if (!isSchedule && !isDate) continue;
            if (!f.Enabled)
            {
                paused.Add(new JsonObject
                {
                    ["kind"] = isSchedule ? "flow" : "date", ["id"] = f.Id, ["name"] = f.Name, ["since"] = Formatter.IsoUtc(f.UpdatedAt),
                    ["reason"] = f.PausedReason, ["canResume"] = true,
                });
                continue;
            }
            if (isSchedule)
            {
                var runs = ScheduleRules.Occurrences(t, ws.TimeZone, fromUtc, toUtc);
                items.Add(new JsonObject
                {
                    ["kind"] = "flow", ["id"] = f.Id, ["name"] = f.Name, ["summary"] = ScheduleRules.Describe(t), ["apps"] = Apps(def),
                    ["sends"] = Sends(def), ["event"] = t.Event, ["runs"] = ByDay(runs, zone),
                });
                continue;
            }
            if (++dateFlows > MaxDateFlows) continue;
            var scanFrom = fromUtc > nowUtc ? fromUtc : nowUtc;
            var key = $"{workspaceId:N}:date:{f.Id}:{f.Version}:{f.UpdatedAt.Ticks}:{Bucket(scanFrom)}:{Bucket(toUtc)}";
            var (upcoming, error) = await cache.GetAsync(key, async () =>
            {
                try { return (await sources.DatesAsync(workspaceId, f, def, ws.TimeZone, scanFrom, toUtc, ct), (string?)null); }
                catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException) { return (new DateUpcoming([], null), e.Message); }
            }, v => v.Item2 is not null);
            var item = new JsonObject
            {
                ["kind"] = "date", ["id"] = f.Id, ["name"] = f.Name, ["app"] = t.App, ["summary"] = DateSummary(t, upcoming.FieldName),
                ["noun"] = t.App == "projects" ? "task" : "record", ["apps"] = Apps(def),
                ["runs"] = ByDay(upcoming.FireAts.Where(x => x <= toUtc), zone),
            };
            if (error is not null) item["error"] = error;
            items.Add(item);
        }
        if (dateFlows > MaxDateFlows) notices.Add($"Only the first {MaxDateFlows} date triggers are shown.");

        // Orbit's report schedules, when it's connected.
        var orbit = new JsonObject { ["connected"] = false, ["available"] = false, ["reason"] = "not_connected" };
        if (await sources.OrbitConnectedAsync(workspaceId))
        {
            orbit["connected"] = true;
            var key = $"{workspaceId:N}:orbit:{Bucket(fromUtc)}:{Bucket(toUtc)}";
            var (answer, reason) = await cache.GetAsync(key, async () =>
            {
                try { return (await sources.OrbitReportsAsync(workspaceId, fromUtc, toUtc, ct), (string?)null); }
                catch (AppRefusedException e) when (e.Status == 404) { return (new JsonObject(), "not_ready"); }
                catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException) { return (new JsonObject(), "unreachable"); }
            }, v => v.Item2 == "unreachable");
            if (reason is null && answer["available"] is JsonValue av && av.TryGetValue<bool>(out var ok) && ok)
            {
                orbit["available"] = true;
                orbit["reason"] = null;
                foreach (var s in (answer["schedules"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var report = Report(s, zone, fromUtc, toUtc);
                    if (report is null) continue;
                    if (s["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var on) && !on)
                        paused.Add(new JsonObject
                        {
                            ["kind"] = "report", ["id"] = report["id"]!.DeepClone(), ["name"] = report["name"]!.DeepClone(), ["since"] = s["pausedAt"]?.DeepClone(),
                            ["canResume"] = report["canChange"]?.DeepClone(), ["link"] = report["link"]?.DeepClone(),
                        });
                    else items.Add(report);
                }
            }
            else
            {
                orbit["reason"] = reason ?? Text(answer["reason"]) ?? "not_ready";
                if (reason == "unreachable") notices.Add("Vantra Orbit couldn't be reached, so its reports aren't shown. Try again in a minute.");
            }
        }

        return new JsonObject
        {
            ["from"] = Formatter.IsoUtc(fromUtc), ["to"] = Formatter.IsoUtc(toUtc), ["zone"] = zone.Id, ["workspaceZone"] = ws.TimeZone,
            ["items"] = items, ["paused"] = paused, ["orbit"] = orbit, ["notices"] = notices,
        };
    }

    /// <summary>Orbit's schedule as a row: only the fields the page shows (never an address: Orbit sends none).</summary>
    public static JsonObject? Report(JsonObject s, TimeZoneInfo zone, DateTime fromUtc, DateTime toUtc)
    {
        var id = Text(s["id"]);
        if (string.IsNullOrEmpty(id) || id.Length > 64) return null;
        var runs = (s["runs"] as JsonArray ?? []).Select(r => DateTime.TryParse(Text(r), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null)
            .Where(d => d is { } x && x >= fromUtc && x <= toUtc).Select(d => d!.Value);
        var count = s["recipientCount"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
        var link = Text(s["link"]);
        return new JsonObject
        {
            ["kind"] = "report", ["id"] = id, ["name"] = Text(s["title"]) ?? "Report", ["dashboardName"] = Text(s["dashboardName"]),
            ["summary"] = Text(s["summary"]), ["cadence"] = Text(s["cadence"]), ["rangeLabel"] = Text(s["rangeLabel"]), ["recipientCount"] = count,
            ["canChange"] = s["canChange"] is JsonValue cc && cc.TryGetValue<bool>(out var can) && can,
            ["link"] = link is not null && link.StartsWith("https://", StringComparison.Ordinal) ? link : null,
            ["lastStatus"] = Text(s["lastStatus"]),
            ["runs"] = ByDay(runs, zone),
        };
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A time rounded down to the minute, so a page's asks share the cache.</summary>
    private static string Bucket(DateTime utc) => utc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
}

using System.Globalization;
using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>
/// Schedule triggers, in the workspace's time zone (as Orbit's schedule automations are):
/// <list type="bullet">
/// <item><c>interval</c> <c>{ every, unit: minutes | hours }</c>: every N minutes (from midnight: 15 → :00, :15, …;
/// at least 5) or every N hours on the hour (from midnight).</item>
/// <item><c>daily</c> <c>{ time: "HH:mm" }</c>.</item>
/// <item><c>weekly</c> <c>{ time, days: ["mon", …] }</c>.</item>
/// </list>
/// The worker asks once a minute for the slots in the last few minutes (<see cref="DueSlots"/>); each slot's key is
/// claimed once (flow.FiredKeys), so a slow tick or a second instance never fires one twice.
/// </summary>
public static class ScheduleRules
{
    public static readonly string[] Days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    public const int MinMinutes = 5;

    public static IEnumerable<DefinitionError> Validate(TriggerDef t)
    {
        switch (t.Event)
        {
            case "interval":
                var every = Int(t.Config["every"]);
                var unit = t.Config["unit"]?.GetValue<string>() ?? "minutes";
                if (unit is not ("minutes" or "hours")) yield return new(null, "Choose minutes or hours.");
                else if (every is null || every < 1) yield return new(null, "Set how often it runs.");
                else if (unit == "minutes" && (every < MinMinutes || every > 720)) yield return new(null, $"Every {MinMinutes} to 720 minutes.");
                else if (unit == "hours" && every > 24) yield return new(null, "Every 1 to 24 hours.");
                break;
            case "daily":
            case "weekly":
                if (Time(t.Config["time"]) is null) yield return new(null, "Set a time, like 09:00.");
                if (t.Event == "weekly" && DaysOf(t.Config["days"]).Count == 0) yield return new(null, "Choose at least one day.");
                break;
        }
    }

    public static TimeZoneInfo Zone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return TimeZoneInfo.Utc;
    }

    /// <summary>The slots due in (<paramref name="nowUtc"/> − <paramref name="window"/>, <paramref name="nowUtc"/>], as UTC minutes.</summary>
    public static IReadOnlyList<DateTime> DueSlots(TriggerDef t, string? timeZone, DateTime nowUtc, TimeSpan window)
    {
        var zone = Zone(timeZone);
        var end = Minute(nowUtc);
        var slots = new List<DateTime>();
        for (var m = end; m > nowUtc - window; m = m.AddMinutes(-1))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(m, zone);
            if (Matches(t, local)) slots.Add(m);
        }
        slots.Reverse();
        return slots;
    }

    /// <summary>Whether the schedule fires at this local minute.</summary>
    public static bool Matches(TriggerDef t, DateTime local)
    {
        switch (t.Event)
        {
            case "interval":
                var every = Int(t.Config["every"]) ?? 0;
                if (every < 1) return false;
                var minuteOfDay = local.Hour * 60 + local.Minute;
                return (t.Config["unit"]?.GetValue<string>() ?? "minutes") == "hours"
                    ? local.Minute == 0 && local.Hour % every == 0
                    : every >= MinMinutes && minuteOfDay % every == 0;
            case "daily":
                return Time(t.Config["time"]) is { } d && local.Hour == d.Hours && local.Minute == d.Minutes;
            case "weekly":
                return Time(t.Config["time"]) is { } w && local.Hour == w.Hours && local.Minute == w.Minutes
                    && DaysOf(t.Config["days"]).Contains(Days[(int)local.DayOfWeek]);
            default:
                return false;
        }
    }

    /// <summary>The next time it fires after <paramref name="nowUtc"/> (for the studio's "Next run"), within 8 days.</summary>
    public static DateTime? Next(TriggerDef t, string? timeZone, DateTime nowUtc)
    {
        var zone = Zone(timeZone);
        for (var m = Minute(nowUtc).AddMinutes(1); m < nowUtc.AddDays(8); m = m.AddMinutes(1))
            if (Matches(t, TimeZoneInfo.ConvertTimeFromUtc(m, zone))) return m;
        return null;
    }

    public static string Key(DateTime slotUtc) => "s:" + slotUtc.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    private static DateTime Minute(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

    private static int? Int(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : n is JsonValue d && d.TryGetValue<double>(out var dd) ? (int)dd
        : n is JsonValue s && s.TryGetValue<string>(out var str) && int.TryParse(str, out var p) ? p : null;

    public static TimeSpan? Time(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s)
        && TimeSpan.TryParseExact(s.Trim(), ["hh\\:mm", "h\\:mm"], CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;

    public static IReadOnlySet<string> DaysOf(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => Tokens.Text(x).ToLowerInvariant()).Where(Days.Contains).ToHashSet() : new HashSet<string>();
}

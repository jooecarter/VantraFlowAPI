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
/// <item><c>monthly</c> <c>{ time, day: 1–31 | "last" }</c>: a day past the month's end (the 31st in June) is its last day.</item>
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
            case "monthly":
                if (Time(t.Config["time"]) is null) yield return new(null, "Set a time, like 09:00.");
                if (t.Event == "weekly" && DaysOf(t.Config["days"]).Count == 0) yield return new(null, "Choose at least one day.");
                if (t.Event == "monthly" && DayOfMonth(t.Config["day"]) is null) yield return new(null, "Choose a day of the month.");
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
            case "monthly":
                if (Time(t.Config["time"]) is not { } mt || local.Hour != mt.Hours || local.Minute != mt.Minutes || DayOfMonth(t.Config["day"]) is not { } day) return false;
                var last = DateTime.DaysInMonth(local.Year, local.Month);
                return local.Day == (day == 0 ? last : Math.Min(day, last));
            default:
                return false;
        }
    }

    /// <summary>The next time it fires after <paramref name="nowUtc"/> (for the studio's "Next run"), within 8 days (a monthly one, 32).</summary>
    public static DateTime? Next(TriggerDef t, string? timeZone, DateTime nowUtc)
    {
        var zone = Zone(timeZone);
        var horizon = nowUtc.AddDays(t.Event == "monthly" ? 32 : 8);
        for (var m = Minute(nowUtc).AddMinutes(1); m < horizon; m = m.AddMinutes(1))
            if (Matches(t, TimeZoneInfo.ConvertTimeFromUtc(m, zone))) return m;
        return null;
    }

    /// <summary>
    /// Every time it fires in (<paramref name="fromUtc"/>, <paramref name="toUtc"/>] (UTC minutes), minute by minute through
    /// <see cref="Matches"/> — the worker's own rule, so the Schedule page shows exactly the slots it will fire (a time a
    /// clock change skips is skipped; one that happens twice fires twice, as the worker does). At most <paramref name="max"/>.
    /// </summary>
    public static List<DateTime> Occurrences(TriggerDef t, string? timeZone, DateTime fromUtc, DateTime toUtc, int max = 20_000)
    {
        var zone = Zone(timeZone);
        var o = new List<DateTime>();
        for (var m = Minute(fromUtc).AddMinutes(1); m <= toUtc && o.Count < max; m = m.AddMinutes(1))
            if (Matches(t, TimeZoneInfo.ConvertTimeFromUtc(m, zone))) o.Add(m);
        return o;
    }

    private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>"Every weekday at 17:00", "Every Monday at 08:00", "Every 15 minutes", "Monthly on the 1st at 09:00".</summary>
    public static string Describe(TriggerDef t)
    {
        var at = Time(t.Config["time"]) is { } tm ? $" at {tm:hh\\:mm}" : "";
        switch (t.Event)
        {
            case "interval":
                var every = Int(t.Config["every"]) ?? 0;
                var hours = (t.Config["unit"]?.GetValue<string>() ?? "minutes") == "hours";
                return every <= 1 ? (hours ? "Every hour" : "Every minute") : $"Every {every} {(hours ? "hours" : "minutes")}";
            case "daily":
                return $"Every day{at}";
            case "weekly":
                var days = DaysOf(t.Config["days"]);
                var ordered = Days.Skip(1).Append(Days[0]).Where(days.Contains).ToList();
                if (ordered.Count == 7) return $"Every day{at}";
                if (ordered.Count == 5 && !days.Contains("sat") && !days.Contains("sun")) return $"Every weekday{at}";
                if (ordered.Count == 2 && days.Contains("sat") && days.Contains("sun")) return $"Every weekend day{at}";
                if (ordered.Count == 1) return $"Every {DayNames[Array.IndexOf(Days, ordered[0])]}{at}";
                var names = ordered.Select(d => DayNames[Array.IndexOf(Days, d)][..3]).ToList();
                return $"Every {string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}{at}";
            case "monthly":
                var day = DayOfMonth(t.Config["day"]);
                return day == 0 ? $"On the last day of the month{at}" : $"Monthly on the {Ordinal(day ?? 1)}{at}";
            default:
                return "On a schedule";
        }
    }

    public static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    public static string Key(DateTime slotUtc) => "s:" + slotUtc.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    private static DateTime Minute(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

    private static int? Int(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : n is JsonValue d && d.TryGetValue<double>(out var dd) ? (int)dd
        : n is JsonValue s && s.TryGetValue<string>(out var str) && int.TryParse(str, out var p) ? p : null;

    public static TimeSpan? Time(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s)
        && TimeSpan.TryParseExact(s.Trim(), ["hh\\:mm", "h\\:mm"], CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;

    /// <summary>A monthly schedule's day: 1–31, or 0 for "last".</summary>
    public static int? DayOfMonth(JsonNode? n) =>
        Tokens.Text(n).Trim().ToLowerInvariant() is var s && s == "last" ? 0
        : int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 31 ? d : null;

    public static IReadOnlySet<string> DaysOf(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => Tokens.Text(x).ToLowerInvariant()).Where(Days.Contains).ToHashSet() : new HashSet<string>();
}

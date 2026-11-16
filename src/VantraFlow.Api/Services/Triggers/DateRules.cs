using System.Globalization;
using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Triggers;

/// <summary>A date trigger's settings: the field, how far before or after, at what time of day in which zone, and "Only if".</summary>
public sealed record DateSpec(string Field, int Amount, string Unit, string Direction, TimeOnly Time, string Zone, Condition? Condition)
{
    /// <summary>The ledger's offset part: a changed offset (or time) is a new key, so each setting fires once per date.</summary>
    public string OffsetKey => Direction == DateRules.On
        ? $"on@{Time:HH\\:mm}"
        : $"{Direction}:{Amount}{Unit[0]}@{Time:HH\\:mm}";
}

/// <summary>One record a date trigger will run for, and when.</summary>
public sealed record DateDue(string RecordId, string DateValue, DateTime FireAt, JsonObject Data);

/// <summary>
/// "Before or after a date" (<c>orbit|projects / date.relative</c>) and "A task becomes overdue" (<c>projects / task.overdue</c>).
/// <para>
/// When a date fires: a date-only value is a day in the trigger's zone, so "3 days before" is that day minus three at the
/// chosen time there (DST respected: 09:00 is 09:00 local on either side of a change); minutes and hours count from that
/// time. A date-and-time value keeps its own time: days and weeks move it by calendar days in the zone (so it keeps its
/// local time across a change), minutes and hours by the clock. An overdue task fires the morning after its due day, at
/// the chosen time (09:00) in the zone, unless it's done.
/// </para>
/// Each (flow, record, date value, offset) fires once (<c>flow.DateFirings</c>): a changed date is a new key, so its run
/// moves. The scan fires what came due since it last looked, reaching back at most <see cref="CatchUp"/> after downtime and
/// never before the flow was switched on or saved.
/// </summary>
public static class DateRules
{
    public const string Relative = "date.relative";
    public const string Overdue = "task.overdue";
    public const string Before = "before";
    public const string On = "on";
    public const string After = "after";
    public static readonly string[] Units = ["minutes", "hours", "days", "weeks"];
    public static readonly TimeSpan CatchUp = TimeSpan.FromHours(24);
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(3);
    /// <summary>How far a scan's window reaches back over the last one (a slow clock, a slow app).</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);
    /// <summary>"Coming up" looks this far ahead.</summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(120);
    public const int MaxAmount = 3650;
    public static readonly TimeOnly DefaultTime = new(9, 0);

    public static bool IsDateEvent(string? e) => e is Relative or Overdue;
    public static bool IsDateTrigger(TriggerDef t) =>
        (t.App == "orbit" && t.Event == Relative) || (t.App == "projects" && t.Event is Relative or Overdue);

    public static DateSpec Read(TriggerDef t, string workspaceZone)
    {
        var c = t.Config;
        var overdue = t.Event == Overdue;
        var amount = int.TryParse(Tokens.Text(c["amount"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? Math.Clamp(a, 0, MaxAmount) : 0;
        var unit = Tokens.Text(c["unit"]) is var u && Units.Contains(u) ? u : "days";
        var direction = Tokens.Text(c["direction"]) is Before or After ? Tokens.Text(c["direction"]) : On;
        if (direction != On && amount == 0) direction = On;
        var time = TimeOnly.TryParseExact(Tokens.Text(c["time"]), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var tm) ? tm : DefaultTime;
        var zone = Tokens.Text(c["zone"]) is { Length: > 0 } z && FindZone(z) is not null ? z : workspaceZone;
        var field = overdue ? "dueDate" : t.App == "projects" ? "dueDate" : Tokens.Text(c["field"]).Trim();
        return new DateSpec(field, overdue ? 1 : amount, overdue ? "days" : unit, overdue ? After : direction, time, zone, Conditions.Parse(c["condition"]));
    }

    public static IEnumerable<string> Check(TriggerDef t)
    {
        var c = t.Config;
        if (t.Event == Overdue)
        {
            if (Tokens.Text(c["time"]) is { Length: > 0 } tm && !TimeOnly.TryParseExact(tm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                yield return "Choose a time, like 09:00.";
            yield break;
        }
        var direction = Tokens.Text(c["direction"]);
        if (direction is not ("" or Before or On or After)) yield return "Choose before, on the day or after.";
        if (direction is Before or After)
        {
            if (!int.TryParse(Tokens.Text(c["amount"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) || a < 1 || a > MaxAmount)
                yield return $"Choose how long before or after: 1 to {MaxAmount:N0}.";
            if (Tokens.Text(c["unit"]) is { Length: > 0 } u && !Units.Contains(u)) yield return "Choose minutes, hours, days or weeks.";
        }
        if (Tokens.Text(c["time"]) is { Length: > 0 } time && !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            yield return "Choose a time, like 09:00.";
        if (Tokens.Text(c["zone"]) is { Length: > 0 } zone && FindZone(zone) is null) yield return "Choose a time zone.";
        if (c["condition"] is JsonObject cond && cond.Count > 0 && Conditions.Parse(cond) is not { Count: > 0 }) yield return "Finish the Only if condition, or remove it.";
    }

    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    private static TimeZoneInfo Zone(string id) => FindZone(id) ?? TimeZoneInfo.Utc;

    /// <summary>A date value as it was read: a day, or an instant (UTC); null when it's neither.</summary>
    public readonly record struct DateValue(DateOnly? Day, DateTime? Instant)
    {
        public bool IsDay => Day is not null;
        /// <summary>The ledger's date part: the day, or the instant to the second in UTC.</summary>
        public string Key => Day is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : Instant!.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a field's value: <c>YYYY-MM-DD</c> (or a date at midnight with no zone, as Projects sends a due day) is a day;
    /// an ISO date and time with a zone or Z is an instant.
    /// </summary>
    public static DateValue? Parse(JsonNode? node)
    {
        var s = Tokens.Text(node).Trim();
        if (s.Length == 0) return null;
        if (s.Length == 10 && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return new DateValue(day, null);
        var zoned = s.EndsWith('Z') || s.EndsWith('z') || (s.Length > 19 && (s[^6] is '+' or '-') && s[^3] == ':');
        if (!zoned)
        {
            // No zone: a day at midnight is the day itself (Projects' due dates); any other time is read as UTC.
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var plain))
                return plain.TimeOfDay == TimeSpan.Zero ? new DateValue(DateOnly.FromDateTime(plain), null) : new DateValue(null, DateTime.SpecifyKind(plain, DateTimeKind.Utc));
            return null;
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? new DateValue(null, dto.UtcDateTime)
            : null;
    }

    /// <summary>When a value fires under a spec (UTC).</summary>
    public static DateTime FireAt(DateValue v, DateSpec spec)
    {
        var zone = Zone(spec.Zone);
        var sign = spec.Direction == Before ? -1 : spec.Direction == After ? 1 : 0;
        var n = sign * spec.Amount;
        if (v.Day is { } day)
        {
            if (spec.Unit is "days" or "weeks")
            {
                var local = day.AddDays(spec.Unit == "weeks" ? n * 7 : n).ToDateTime(spec.Time);
                return ToUtc(local, zone);
            }
            var start = ToUtc(day.ToDateTime(spec.Time), zone);
            return spec.Unit == "hours" ? start.AddHours(n) : start.AddMinutes(n);
        }
        var instant = v.Instant!.Value;
        if (spec.Unit is "days" or "weeks")
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
            return ToUtc(local.AddDays(spec.Unit == "weeks" ? n * 7 : n), zone);
        }
        return spec.Unit == "hours" ? instant.AddHours(n) : instant.AddMinutes(n);
    }

    /// <summary>
    /// A wall-clock time in a zone as UTC. A time a clock change skips (02:30 on the morning the clocks go forward) moves on
    /// by the gap; a time that happens twice (when they go back) is the first.
    /// </summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var after = local;
            for (var i = 0; i < 8 && zone.IsInvalidTime(after); i++) after = after.AddMinutes(30);
            local = after;
        }
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var first = offsets.Max(); // the earlier instant has the larger offset
            return DateTime.SpecifyKind(local - first, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>
    /// The values whose fire time can fall in [from, to]: a range of days to ask the app for, padded a couple of days for
    /// zones and clock changes (what comes back is checked exactly).
    /// </summary>
    public static (DateOnly From, DateOnly To) ValueRange(DateSpec spec, DateTime fromUtc, DateTime toUtc)
    {
        var sign = spec.Direction == Before ? -1 : spec.Direction == After ? 1 : 0;
        var span = spec.Unit switch
        {
            "weeks" => TimeSpan.FromDays(7 * spec.Amount),
            "days" => TimeSpan.FromDays(spec.Amount),
            "hours" => TimeSpan.FromHours(spec.Amount),
            _ => TimeSpan.FromMinutes(spec.Amount),
        } * sign;
        var a = fromUtc - span;
        var b = toUtc - span;
        return (DateOnly.FromDateTime(a).AddDays(-2), DateOnly.FromDateTime(b).AddDays(2));
    }

    /// <summary>Where this scan starts: since the last one (with a little overlap), at most a day back, never before the flow was switched on or saved.</summary>
    public static DateTime WindowStart(DateTime now, DateTime scannedUntil, DateTime flowSince) =>
        new[] { scannedUntil - Overlap, now - CatchUp, flowSince }.Max();

    /// <summary>
    /// The records due to fire in (<paramref name="fromUtc"/>, <paramref name="toUtc"/>]: each record's value read from
    /// <paramref name="valueOf"/>, its fire time worked out, "Only if" checked on the trigger data <paramref name="dataOf"/>
    /// makes. Done tasks never fire the overdue trigger. Ordered by fire time.
    /// </summary>
    public static List<DateDue> Due(DateSpec spec, bool overdue, IEnumerable<JsonObject> records, Func<JsonObject, string> idOf, Func<JsonObject, JsonNode?> valueOf,
        Func<JsonObject, bool> doneOf, Func<JsonObject, DateValue, DateTime, JsonObject> dataOf, DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<DateDue>();
        foreach (var r in records)
        {
            var id = idOf(r);
            if (id.Length == 0) continue;
            if (Parse(valueOf(r)) is not { } v) continue;
            if (overdue && doneOf(r)) continue;
            var at = FireAt(overdue ? new DateValue(v.Day ?? DateOnly.FromDateTime(v.Instant!.Value), null) : v, spec);
            if (at <= fromUtc || at > toUtc) continue;
            var data = dataOf(r, v, at);
            if (spec.Condition is { Count: > 0 } cond && !Conditions.Evaluate(cond, new JsonObject { ["trigger"] = data.DeepClone() })) continue;
            list.Add(new DateDue(id, v.Key, at, data));
        }
        return list.OrderBy(d => d.FireAt).ThenBy(d => d.RecordId, StringComparer.Ordinal).ToList();
    }
}

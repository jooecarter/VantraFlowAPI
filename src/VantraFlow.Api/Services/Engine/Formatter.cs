using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VantraFlow.Api.Services.Engine;

/// <summary>
/// The formatter step: one change to one value, its result in <c>{{steps.id.output}}</c>. Its config is
/// <c>{ kind, op, input, … }</c>:
/// <list type="bullet">
/// <item><b>text</b>: <c>trim</c>; <c>case</c> (<c>case</c>: upper | lower | title | sentence); <c>replace</c> (<c>find</c>,
/// <c>with</c>: every match, case-sensitive); <c>split</c> (<c>separator</c>, default a comma: a list, with
/// <c>first</c>, <c>last</c> and <c>count</c>); <c>extract</c> (<c>what</c>: email | url | number | custom, with
/// <c>pattern</c>, a regular expression run with a <see cref="RegexTimeout"/> limit: the first match, or its first
/// group, with <c>matches</c> and <c>found</c>).</item>
/// <item><b>number</b>: <c>format</c> (<c>decimals</c>, <c>separator</c>: comma | period | space | none, <c>prefix</c>,
/// <c>suffix</c>); <c>round</c> (<c>decimals</c>, <c>mode</c>: nearest | up | down); <c>math</c> (<c>operator</c>: add |
/// subtract | multiply | divide, <c>value</c>).</item>
/// <item><b>date</b>: <c>format</c> (<c>pattern</c>: yyyy MM dd HH mm …); <c>parse</c> (<c>from</c>: auto | dmy | mdy);
/// <c>add</c> (<c>operator</c>: add | subtract, <c>amount</c>, <c>unit</c>); <c>timezone</c> (to <c>zone</c>). A date
/// without an offset is read in <c>zone</c> (else the workspace's time zone); <c>iso</c> is always the instant in UTC.</item>
/// <item><b>lookup</b>: <c>rows</c> of <c>{ key, value }</c> (keys matched without regard to case), else <c>fallback</c>.</item>
/// </list>
/// The demo (lib/formatter.ts) does the same, so a test there gives what the server gives.
/// </summary>
public static partial class Formatter
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    public const int MaxInput = 100_000;
    public const int MaxRows = 100;

    public static readonly IReadOnlyDictionary<string, string[]> Ops = new Dictionary<string, string[]>
    {
        ["text"] = ["trim", "case", "replace", "split", "extract"],
        ["number"] = ["format", "round", "math"],
        ["date"] = ["format", "parse", "add", "timezone"],
        ["lookup"] = ["lookup"],
    };

    private static readonly string[] Units = ["minutes", "hours", "days", "weeks", "months", "years"];

    /// <summary>What's missing or wrong in a formatter's config before it can run (null: nothing).</summary>
    public static string? Check(JsonObject c)
    {
        var kind = Tokens.Text(c["kind"]);
        var op = kind == "lookup" ? "lookup" : Tokens.Text(c["op"]);
        if (!Ops.TryGetValue(kind, out var ops)) return "Choose what to format.";
        if (!ops.Contains(op)) return "Choose how to format it.";
        if (DefinitionRules.Empty(c["input"])) return "Choose the value to format.";
        bool Static(string key, out string text) { text = Tokens.Text(c[key]); return !text.Contains("{{", StringComparison.Ordinal); }
        switch (kind, op)
        {
            case ("text", "replace") when Tokens.Text(c["find"]).Length == 0: return "Fill in what to replace.";
            case ("text", "extract") when Tokens.Text(c["what"]) is "" or "custom":
                if (Tokens.Text(c["pattern"]).Length == 0) return "Fill in the pattern to look for.";
                if (Static("pattern", out var pattern) && Compile(pattern) is null) return "The pattern isn't a regular expression Vantra Flow can run.";
                break;
            case ("number", "math") when DefinitionRules.Empty(c["value"]): return "Fill in the number to use.";
            case ("number", "math") when Tokens.Text(c["operator"]) is not ("" or "add" or "subtract" or "multiply" or "divide"): return "Choose add, subtract, multiply or divide.";
            case ("date", "add") when DefinitionRules.Empty(c["amount"]): return "Fill in how much to add.";
            case ("date", "add") when !Units.Contains(Tokens.Text(c["unit"]) is { Length: > 0 } u ? u : "days"): return "Choose minutes, hours, days, weeks, months or years.";
            case ("date", "format") when Tokens.Text(c["pattern"]).Trim().Length == 0: return "Fill in the date format.";
            case ("date", "timezone") when Tokens.Text(c["zone"]).Trim().Length == 0: return "Choose the time zone.";
            case ("lookup", _) when (c["rows"] as JsonArray)?.Any(r => Tokens.Text(r?["key"]).Length > 0) != true: return "Add a row to the table.";
            case ("lookup", _) when (c["rows"] as JsonArray)?.Count > MaxRows: return $"A lookup table has at most {MaxRows} rows.";
        }
        if (kind == "date" && Static("zone", out var zone) && zone.Trim().Length > 0 && FindZone(zone.Trim()) is null) return $"“{zone.Trim()}” isn't a time zone.";
        return null;
    }

    /// <summary>
    /// Runs a formatter over its config with the tokens filled in. <paramref name="timeZone"/> is the workspace's (for
    /// dates without an offset). The output, or why it can't be done.
    /// </summary>
    public static (JsonObject? Output, string? Error) Run(JsonObject input, string? timeZone)
    {
        var kind = Tokens.Text(input["kind"]);
        var op = kind == "lookup" ? "lookup" : Tokens.Text(input["op"]);
        if (!Ops.TryGetValue(kind, out var ops) || !ops.Contains(op)) return (null, "Choose how to format it.");
        var value = input["input"];
        var text = Tokens.Text(value);
        if (text.Length > MaxInput) return (null, "The value is too long to format.");
        try
        {
            return kind switch
            {
                "text" => Text(op, text, input),
                "number" => Number(op, value, input),
                "date" => Date(op, text, input, timeZone),
                _ => (Lookup(text, input), null),
            };
        }
        catch (RegexMatchTimeoutException) { return (null, "The pattern took too long on this text."); }
    }

    private static JsonObject Out(JsonNode? value) => new() { ["output"] = value };

    // ── Text ──

    private static (JsonObject?, string?) Text(string op, string text, JsonObject c)
    {
        switch (op)
        {
            case "trim":
                return (Out(Squash().Replace(text.Trim(), " ")), null);
            case "case":
                var mode = Tokens.Text(c["case"]) is { Length: > 0 } m ? m : "title";
                return (Out(mode switch
                {
                    "upper" => text.ToUpperInvariant(),
                    "lower" => text.ToLowerInvariant(),
                    "sentence" => text.Length == 0 ? "" : char.ToUpperInvariant(text.TrimStart()[0]) + text.TrimStart()[1..].ToLowerInvariant(),
                    _ => TitleCase(text),
                }), null);
            case "replace":
                var find = Tokens.Text(c["find"]);
                return (Out(find.Length == 0 ? text : text.Replace(find, Tokens.Text(c["with"]), StringComparison.Ordinal)), null);
            case "split":
                var sep = Tokens.Text(c["separator"]) switch { "" => ",", "\\n" => "\n", var s => s };
                var parts = text.Split(sep, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(1000).ToList();
                var list = new JsonArray(parts.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
                return (new JsonObject
                {
                    ["output"] = list, ["first"] = parts.FirstOrDefault() ?? "", ["last"] = parts.LastOrDefault() ?? "", ["count"] = parts.Count,
                }, null);
            default:
                var what = Tokens.Text(c["what"]) is { Length: > 0 } w ? w : "custom";
                var regex = what switch
                {
                    "email" => EmailPattern(),
                    "url" => UrlPattern(),
                    "number" => NumberPattern(),
                    _ => Compile(Tokens.Text(c["pattern"])),
                };
                if (regex is null) return (null, "The pattern isn't a regular expression Vantra Flow can run.");
                var found = new List<string>();
                foreach (Match match in regex.Matches(text))
                {
                    found.Add(match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value);
                    if (found.Count >= 100) break;
                }
                return (new JsonObject
                {
                    ["output"] = found.FirstOrDefault() ?? "",
                    ["matches"] = new JsonArray(found.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                    ["found"] = found.Count > 0,
                }, null);
        }
    }

    public static Regex? Compile(string pattern)
    {
        if (pattern.Length is 0 or > 500) return null;
        try { return new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout); }
        catch (ArgumentException) { return null; }
    }

    private static string TitleCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        var start = true;
        foreach (var ch in s)
        {
            sb.Append(start ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
            start = char.IsWhiteSpace(ch) || ch is '-' or '(' or '"' or '/';
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Squash();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.None, 250)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"https?://[^\s<>""')\]]+", RegexOptions.None, 250)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"-?\d[\d,]*(?:\.\d+)?", RegexOptions.None, 250)]
    private static partial Regex NumberPattern();

    // ── Number ──

    /// <summary>A number from a value: a JSON number as it is; text with its thousands commas, spaces and currency signs dropped.</summary>
    public static double? ToNumber(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        var s = Tokens.Text(node).Trim();
        var cleaned = new string(s.Where(ch => char.IsDigit(ch) || ch is '.' or '-' or 'e' or 'E' or '+').ToArray());
        if (cleaned.Length == 0 || !s.Any(char.IsDigit)) return null;
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    }

    private static int Decimals(JsonNode? n, int fallback) =>
        ToNumber(n) is { } d ? (int)Math.Clamp(Math.Floor(d), 0, 10) : fallback;

    private static (JsonObject?, string?) Number(string op, JsonNode? value, JsonObject c)
    {
        if (ToNumber(value) is not { } n) return (null, $"“{Clip(Tokens.Text(value))}” isn't a number.");
        switch (op)
        {
            case "format":
                return (Out(FormatNumber(n, Decimals(c["decimals"], 2), Tokens.Text(c["separator"]), Tokens.Text(c["prefix"]), Tokens.Text(c["suffix"]))), null);
            case "round":
                var places = Decimals(c["decimals"], 0);
                var factor = Math.Pow(10, places);
                var rounded = Tokens.Text(c["mode"]) switch
                {
                    "up" => Math.Ceiling(n * factor) / factor,
                    "down" => Math.Floor(n * factor) / factor,
                    _ => Math.Round(n, places, MidpointRounding.AwayFromZero),
                };
                return (Out(Num(rounded)), null);
            default:
                if (ToNumber(c["value"]) is not { } by) return (null, $"“{Clip(Tokens.Text(c["value"]))}” isn't a number.");
                var opName = Tokens.Text(c["operator"]) is { Length: > 0 } o ? o : "add";
                if (opName == "divide" && by == 0) return (null, "Can't divide by zero.");
                var result = opName switch { "subtract" => n - by, "multiply" => n * by, "divide" => n / by, _ => n + by };
                return (Out(Num(Math.Round(result, 10))), null);
        }
    }

    private static JsonNode Num(double d) => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);

    /// <summary>1234.5 → "1,234.50" (comma), "1.234,50" (period), "1 234,50" (space), "1234.50" (none).</summary>
    public static string FormatNumber(double n, int decimals, string separator, string prefix = "", string suffix = "")
    {
        var fixedText = Math.Abs(Math.Round(n, decimals, MidpointRounding.AwayFromZero)).ToString("F" + decimals, CultureInfo.InvariantCulture);
        var dot = fixedText.IndexOf('.');
        var whole = dot < 0 ? fixedText : fixedText[..dot];
        var frac = dot < 0 ? "" : fixedText[(dot + 1)..];
        var (group, point) = separator switch { "period" => (".", ","), "space" => (" ", ","), "none" => ("", "."), _ => (",", ".") };
        var sb = new StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) sb.Append(group);
            sb.Append(whole[i]);
        }
        var negative = n < 0 && Math.Round(n, decimals, MidpointRounding.AwayFromZero) != 0;
        return (negative ? "-" : "") + prefix + sb + (frac.Length > 0 ? point + frac : "") + suffix;
    }

    // ── Date ──

    public static TimeZoneInfo? FindZone(string id)
    {
        if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return null; }
    }

    private static (JsonObject?, string?) Date(string op, string text, JsonObject c, string? workspaceZone)
    {
        var zoneId = Tokens.Text(c["zone"]).Trim();
        var zone = op == "timezone" ? null : FindZone(zoneId.Length > 0 ? zoneId : workspaceZone ?? "UTC") ?? TimeZoneInfo.Utc;
        var from = op == "parse" ? Tokens.Text(c["from"]) : "auto";
        var readIn = op == "timezone" ? FindZone(workspaceZone ?? "UTC") ?? TimeZoneInfo.Utc : zone!;
        if (ParseDate(text, from, readIn) is not { } at) return (null, $"“{Clip(text)}” isn't a date Vantra Flow can read.");
        var iso = IsoUtc(at);
        switch (op)
        {
            case "format":
                return (new JsonObject { ["output"] = FormatDate(TimeZoneInfo.ConvertTimeFromUtc(at, zone!), Tokens.Text(c["pattern"]).Trim()), ["iso"] = iso }, null);
            case "parse":
                return (new JsonObject { ["output"] = iso, ["iso"] = iso, ["unix"] = new DateTimeOffset(at).ToUnixTimeSeconds() }, null);
            case "add":
                if (ToNumber(c["amount"]) is not { } amount) return (null, $"“{Clip(Tokens.Text(c["amount"]))}” isn't a number.");
                if (Tokens.Text(c["operator"]) == "subtract") amount = -amount;
                DateTime moved;
                try
                {
                    // Days and longer move on the calendar in the time zone, so 09:00 stays 09:00 across a clock change.
                    var local = TimeZoneInfo.ConvertTimeFromUtc(at, zone!);
                    moved = (Tokens.Text(c["unit"]) is { Length: > 0 } u ? u : "days") switch
                    {
                        "minutes" => at.AddMinutes(amount),
                        "hours" => at.AddHours(amount),
                        "weeks" => ToUtc(local.AddDays(7 * amount), zone!),
                        "months" => ToUtc(local.AddMonths((int)Math.Truncate(amount)), zone!),
                        "years" => ToUtc(local.AddYears((int)Math.Truncate(amount)), zone!),
                        _ => ToUtc(local.AddDays(amount), zone!),
                    };
                }
                catch (ArgumentOutOfRangeException) { return (null, "That date is out of range."); }
                return (new JsonObject { ["output"] = IsoUtc(moved), ["iso"] = IsoUtc(moved) }, null);
            default:
                var target = FindZone(zoneId) ?? TimeZoneInfo.Utc;
                var there = TimeZoneInfo.ConvertTimeFromUtc(at, target);
                var offset = target.GetUtcOffset(at);
                var sign = offset < TimeSpan.Zero ? "-" : "+";
                var text2 = there.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + sign + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                return (new JsonObject
                {
                    ["output"] = text2, ["iso"] = iso, ["time"] = there.ToString("HH:mm", CultureInfo.InvariantCulture),
                    ["date"] = there.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["zone"] = target.Id,
                }, null);
        }
    }

    public static string IsoUtc(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A local time skipped by a clock change moves an hour on.
        if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    [GeneratedRegex(@"^(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{2}|\d{4})(?:[ T,]+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*([AaPp][Mm])?)?$")]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"(Z|[+\-]\d{2}:?\d{2}|\bGMT|\bUTC)$", RegexOptions.IgnoreCase)]
    private static partial Regex HasOffset();

    /// <summary>
    /// A date in UTC: unix seconds or milliseconds; <c>dmy</c> / <c>mdy</c> numbers like 11/12/2026 (with a time);
    /// otherwise ISO 8601, RFC 1123 / 2822 or anything .NET reads invariantly. Without an offset it's local to
    /// <paramref name="zone"/>. Null when it isn't a date.
    /// </summary>
    public static DateTime? ParseDate(string raw, string from, TimeZoneInfo zone)
    {
        var s = raw.Trim();
        if (s.Length is 0 or > 100) return null;
        if (Regex.IsMatch(s, @"^-?\d{9,13}(\.\d+)?$") && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var unix))
        {
            try { return (Math.Abs(unix) >= 1e11 ? DateTimeOffset.FromUnixTimeMilliseconds((long)unix) : DateTimeOffset.FromUnixTimeSeconds((long)unix)).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (DayMonth().Match(s) is { Success: true } m && from is "dmy" or "mdy" or "auto")
        {
            int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (year < 100) year += 2000;
            var (day, month) = from == "mdy" ? (b, a) : (a, b);
            var hour = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            var minute = m.Groups[5].Success ? int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
            var second = m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
            if (m.Groups[7].Success)
            {
                var pm = m.Groups[7].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
                if (hour is < 1 or > 12) return null;
                hour = hour % 12 + (pm ? 12 : 0);
            }
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month) || hour > 23 || minute > 59 || second > 59) return null;
            return ToUtc(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified), zone);
        }
        if (HasOffset().IsMatch(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset))
            return withOffset.UtcDateTime;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var plain)
            && plain.Year > 1)
            return ToUtc(DateTime.SpecifyKind(plain, DateTimeKind.Unspecified), zone);
        return null;
    }

    private static readonly string[] DateTokens = ["yyyy", "yy", "MMMM", "MMM", "MM", "M", "dddd", "ddd", "dd", "d", "HH", "H", "hh", "h", "mm", "ss", "tt"];

    /// <summary>
    /// A date written with <paramref name="pattern"/>: yyyy yy MMMM MMM MM M dddd ddd dd d HH H hh h mm ss tt, text in
    /// single quotes as it is, and anything else as it is (English names, as the demo's).
    /// </summary>
    public static string FormatDate(DateTime d, string pattern)
    {
        if (pattern.Equals("iso", StringComparison.OrdinalIgnoreCase)) return d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '\'')
            {
                var end = pattern.IndexOf('\'', i + 1);
                if (end < 0) end = pattern.Length;
                sb.Append(pattern, i + 1, end - i - 1);
                i = end + 1;
                continue;
            }
            var token = DateTokens.FirstOrDefault(t => string.CompareOrdinal(pattern, i, t, 0, t.Length) == 0);
            if (token is null) { sb.Append(pattern[i]); i++; continue; }
            var inv = CultureInfo.InvariantCulture;
            sb.Append(token switch
            {
                "yyyy" => d.Year.ToString("0000", inv),
                "yy" => (d.Year % 100).ToString("00", inv),
                "MMMM" => inv.DateTimeFormat.GetMonthName(d.Month),
                "MMM" => inv.DateTimeFormat.GetAbbreviatedMonthName(d.Month),
                "MM" => d.Month.ToString("00", inv),
                "M" => d.Month.ToString(inv),
                "dddd" => inv.DateTimeFormat.GetDayName(d.DayOfWeek),
                "ddd" => inv.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek),
                "dd" => d.Day.ToString("00", inv),
                "d" => d.Day.ToString(inv),
                "HH" => d.Hour.ToString("00", inv),
                "H" => d.Hour.ToString(inv),
                "hh" => (d.Hour % 12 == 0 ? 12 : d.Hour % 12).ToString("00", inv),
                "h" => (d.Hour % 12 == 0 ? 12 : d.Hour % 12).ToString(inv),
                "mm" => d.Minute.ToString("00", inv),
                "ss" => d.Second.ToString("00", inv),
                _ => d.Hour < 12 ? "AM" : "PM",
            });
            i += token.Length;
        }
        return sb.ToString();
    }

    // ── Lookup ──

    private static JsonObject Lookup(string key, JsonObject c)
    {
        var needle = key.Trim();
        foreach (var row in (c["rows"] as JsonArray ?? []).Take(MaxRows))
            if (string.Equals(Tokens.Text(row?["key"]).Trim(), needle, StringComparison.OrdinalIgnoreCase))
                return new JsonObject { ["output"] = row?["value"]?.DeepClone() ?? "", ["matched"] = true };
        return new JsonObject { ["output"] = c["fallback"]?.DeepClone() ?? "", ["matched"] = false };
    }

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "…";
}

/// <summary>
/// "Set variable": <c>{ assignments: [{ name, op, value }] }</c>. Each sets (<c>set</c>), adds to (<c>add</c>, a
/// number) or appends to (<c>append</c>, a list) a variable of the run, read later as <c>{{vars.name}}</c>.
/// Variables start empty, live as long as the run, and carry across a Repeat for each's items, so a loop can count
/// or collect.
/// </summary>
public static partial class VariableRules
{
    public const int MaxAssignments = 10;
    public const int MaxListItems = 500;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    public static partial Regex Name();

    public static IEnumerable<string> Check(JsonObject config)
    {
        var list = config["assignments"] as JsonArray;
        if (list is not { Count: > 0 }) { yield return "Add a variable to set."; yield break; }
        if (list.Count > MaxAssignments) yield return $"A step sets at most {MaxAssignments} variables.";
        foreach (var a in list)
        {
            var name = Tokens.Text(a?["name"]).Trim();
            if (!Name().IsMatch(name)) { yield return name.Length == 0 ? "Name each variable." : $"“{name}”: a name is letters, numbers and _, starting with a letter."; continue; }
            if (Tokens.Text(a?["op"]) is not ("" or "set" or "add" or "append")) yield return $"Choose how to change {name}.";
        }
    }

    /// <summary>The names any Set variable step in the flow sets.</summary>
    public static IReadOnlySet<string> Names(FlowDefinition def) =>
        def.AllSteps().Where(s => s.Type == StepTypes.Variable)
            .SelectMany(s => (s.Config["assignments"] as JsonArray ?? []).Select(a => Tokens.Text(a?["name"]).Trim()))
            .Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>Applies the (rendered) assignments to the run's variables; the step's output is the values it set.</summary>
    public static (JsonObject? Output, string? Error) Apply(JsonObject input, JsonObject vars)
    {
        var output = new JsonObject();
        foreach (var a in input["assignments"] as JsonArray ?? [])
        {
            var name = Tokens.Text(a?["name"]).Trim();
            if (!Name().IsMatch(name)) continue;
            var value = a?["value"]?.DeepClone();
            JsonNode? next;
            switch (Tokens.Text(a?["op"]))
            {
                case "add":
                    var by = Formatter.ToNumber(value);
                    if (by is null) return (null, $"{name}: “{Tokens.Text(value)}” isn't a number to add.");
                    var now = Formatter.ToNumber(vars[name]) ?? 0;
                    var sum = Math.Round(now + by.Value, 10);
                    next = sum == Math.Floor(sum) && Math.Abs(sum) < 1e15 ? JsonValue.Create((long)sum) : JsonValue.Create(sum);
                    break;
                case "append":
                    var list = vars[name] is JsonArray existing ? (JsonArray)existing.DeepClone()
                        : DefinitionRules.Empty(vars[name]) ? new JsonArray() : new JsonArray(vars[name]!.DeepClone());
                    if (list.Count >= MaxListItems) return (null, $"{name} has {MaxListItems} items, as many as a list can hold.");
                    list.Add(value);
                    next = list;
                    break;
                default:
                    next = value ?? JsonValue.Create("");
                    break;
            }
            vars[name] = next;
            output[name] = next?.DeepClone();
        }
        return (output, null);
    }
}

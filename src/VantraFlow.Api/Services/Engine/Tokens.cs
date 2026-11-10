using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VantraFlow.Api.Services.Engine;

/// <summary>
/// Tokens: <c>{{path}}</c> in a step's config, filled in from the run's data when the step runs. The data is
/// <c>{ trigger, steps: { id: output }, run: { id, startedAt }, workspace: { name, timeZone }, now }</c>, so
/// <c>{{trigger.record.fields.Email}}</c> is the triggering record's Email and <c>{{steps.s2.id}}</c> what step
/// s2 made. A path's parts are split on dots; a part may hold spaces (<c>{{trigger.record.fields.Deal value}}</c>)
/// and a list is indexed by number (<c>{{steps.s1.items.0.name}}</c>). A missing value is empty text.
/// <para>As Vantra Orbit's automation tokens ({{field}}), generalised to any step's output.</para>
/// </summary>
public static partial class Tokens
{
    [GeneratedRegex(@"\{\{\s*([^{}]{1,200}?)\s*\}\}")]
    private static partial Regex Pattern();

    /// <summary>Every token path in a config (to check they point at earlier steps).</summary>
    public static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                foreach (Match m in Pattern().Matches(s)) yield return m.Groups[1].Value.Trim();
                break;
            case JsonObject o:
                foreach (var (_, child) in o) foreach (var r in References(child)) yield return r;
                break;
            case JsonArray a:
                foreach (var child in a) foreach (var r in References(child)) yield return r;
                break;
        }
    }

    /// <summary>The value at <paramref name="path"/> in <paramref name="data"/>, or null.</summary>
    public static JsonNode? Resolve(JsonNode? data, string path)
    {
        var cur = data;
        foreach (var raw in path.Split('.'))
        {
            var part = raw.Trim();
            if (cur is JsonObject o)
            {
                if (o.TryGetPropertyValue(part, out var next)) { cur = next; continue; }
                // Field names are matched without regard to case, as Orbit's are.
                var hit = o.FirstOrDefault(kv => string.Equals(kv.Key, part, StringComparison.OrdinalIgnoreCase));
                cur = hit.Key is null ? null : hit.Value;
            }
            else if (cur is JsonArray a && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count)
                cur = a[i];
            else
                return null;
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>A value as text: strings as they are, numbers invariant, lists of plain values joined with ", ", objects as JSON.</summary>
    public static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v => v.ToJsonString(),
        JsonArray a when a.All(x => x is JsonValue) => string.Join(", ", a.Select(Text)),
        JsonArray a when a.All(x => x is JsonObject o && o["name"] is not null) => string.Join(", ", a.Select(x => Text(x!["name"]))),
        JsonObject o when o["name"] is JsonValue && o.Count <= 3 => Text(o["name"]),
        _ => node.ToJsonString(),
    };

    /// <summary>Fills in every token in <paramref name="template"/>; <paramref name="html"/> escapes the values (an email's body).</summary>
    public static string Render(string template, JsonNode? data, bool html = false) =>
        Pattern().Replace(template, m =>
        {
            var text = Text(Resolve(data, m.Groups[1].Value.Trim()));
            return html ? WebUtility.HtmlEncode(text) : text;
        });

    /// <summary>
    /// A config with every string's tokens filled in. A string that is exactly one token keeps the value's own
    /// type (a number stays a number, a list a list), so a field can be copied across as it is.
    /// </summary>
    public static JsonNode? RenderNode(JsonNode? node, JsonNode? data, ISet<string>? htmlKeys = null, string? key = null) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => Whole(s) is { } path
            ? Resolve(data, path)?.DeepClone() ?? JsonValue.Create("")
            : JsonValue.Create(Render(s, data, key is not null && htmlKeys?.Contains(key) == true)),
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, RenderNode(kv.Value, data, htmlKeys, kv.Key)))),
        JsonArray a => new JsonArray(a.Select(x => RenderNode(x, data, htmlKeys, key)).ToArray()),
        _ => node?.DeepClone(),
    };

    private static string? Whole(string s)
    {
        var m = Pattern().Match(s);
        return m.Success && m.Index == 0 && m.Length == s.Length ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>Shortens a value for the run log (a step's input or output), so one huge payload can't fill the table.</summary>
    public static string Clip(JsonNode? node, int max = 16 * 1024)
    {
        var s = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";
        return s.Length <= max ? s : JsonValue.Create(s[..max] + "…")!.ToJsonString();
    }
}

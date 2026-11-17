using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>
/// What a flow is for and who it's for, in brief, for the Flows list: the trigger (its app and event, and the few settings that
/// say what it watches: a table, a project, a sequence, a form's title, a schedule's time and days) and the first
/// <see cref="MaxTargets"/> actions in the order they run (their app and action, and where they send or write: a table, a
/// project, who an email goes to, a channel; an HTTP request's host). Only whitelisted keys, plain values cut short — never a
/// secret, a body, a header, a credential or a long expression. The SPA names tables and projects from its own schema.
/// </summary>
public static class FlowOutline
{
    public const int MaxTargets = 3;
    public const int MaxText = 120;

    private static readonly string[] TriggerKeys = ["tableId", "projectId", "sequenceId", "title", "time", "days", "every", "unit", "zone", "field", "direction", "amount", "target"];
    private static readonly string[] ActionKeys = ["tableId", "viewId", "projectId", "sequenceId", "to", "channel", "channelId", "teamId", "user", "flowId", "spreadsheet", "sheet", "range", "format"];

    public static JsonObject Build(FlowDefinition def)
    {
        var actions = def.AllSteps().Where(s => s.Type == StepTypes.Action && !string.IsNullOrEmpty(s.App)).ToList();
        var list = new JsonArray();
        foreach (var s in actions.Take(MaxTargets))
        {
            var config = Pick(s.Config, ActionKeys);
            if (s.App == "http" && Host(s.Config["url"]) is { } host) config["host"] = host;
            var o = new JsonObject { ["app"] = s.App, ["action"] = s.Action, ["config"] = config };
            if (Clip(s.Name, 80) is { Length: > 0 } name) o["name"] = name;
            list.Add(o);
        }
        return new JsonObject
        {
            ["trigger"] = new JsonObject { ["app"] = def.Trigger.App, ["event"] = def.Trigger.Event, ["config"] = Pick(def.Trigger.Config, TriggerKeys) },
            ["actions"] = list,
            ["actionCount"] = actions.Count,
        };
    }

    private static JsonObject Pick(JsonObject config, string[] keys)
    {
        var o = new JsonObject();
        foreach (var k in keys)
        {
            if (!config.TryGetPropertyValue(k, out var v) || v is null) continue;
            switch (v)
            {
                case JsonValue jv when jv.TryGetValue<string>(out var s):
                    if (Clip(s, MaxText) is { Length: > 0 } c) o[k] = c;
                    break;
                case JsonValue jv when jv.TryGetValue<double>(out var n):
                    o[k] = n;
                    break;
                case JsonValue jv when jv.TryGetValue<bool>(out var b):
                    o[k] = b;
                    break;
                case JsonArray a:
                    var items = a.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var t) ? Clip(t, MaxText) : null).Where(t => t is { Length: > 0 }).Take(7).ToArray();
                    if (items.Length > 0) o[k] = new JsonArray(items.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
                    break;
            }
        }
        return o;
    }

    /// <summary>An HTTP request's host, when the URL is a plain one (no token in its host).</summary>
    private static string? Host(JsonNode? url) =>
        url is JsonValue v && v.TryGetValue<string>(out var s) && !s.Contains("{{", StringComparison.Ordinal)
            && Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.Host : null;

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Trim().Where(c => !char.IsControl(c)).ToArray());
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}

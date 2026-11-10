using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>A queued event (a flow.Events row).</summary>
public sealed record FlowEventRow(
    long Seq, string Id, Guid WorkspaceId, string Source, string Kind, string? Scope, string? FlowId, string Payload,
    bool FlowCaused, bool Test, string? ReplayOf, DateTime At, int Attempts);

public static class EventSources
{
    public const string Orbit = "orbit";
    public const string Projects = "projects";
    public const string Schedule = "schedule";
    public const string Webhook = "webhook";
    public const string Manual = "manual";
}

/// <summary>
/// Whether an event starts a flow, and the trigger data the run gets. App events go to every flow listening to
/// that app, event and table; schedule slots, webhook calls and replays name their flow. A change Flow made itself
/// (the event's origin marker is this workspace's own connection key) only starts flows that opted in
/// (<see cref="FlowOptions.RunOnFlowChanges"/>): the loop protection.
/// </summary>
public static class TriggerMatcher
{
    /// <summary>The trigger data, or null when this flow doesn't start for this event.</summary>
    public static JsonObject? Match(FlowDefinition def, string flowId, FlowEventRow ev, JsonNode? payload)
    {
        if (ev.FlowId is not null) return ev.FlowId == flowId ? payload as JsonObject ?? new JsonObject() : null;
        if (ev.FlowCaused && !def.Options.RunOnFlowChanges) return null;
        var t = def.Trigger;
        if (t.App != ev.Source) return null;
        var data = payload as JsonObject ?? new JsonObject();
        switch (t.App)
        {
            case EventSources.Orbit:
                if (!string.Equals(t.Config["tableId"]?.GetValue<string>(), ev.Scope, StringComparison.Ordinal)) return null;
                return t.Event switch
                {
                    "record.created" => ev.Kind == "record.created" ? data : null,
                    "record.updated" => ev.Kind == "record.updated" && ChangedWatched(t, data) ? data : null,
                    "record.matches" => ev.Kind is "record.created" or "record.updated" && BecameTrue(t, data) ? data : null,
                    _ => null,
                };
            case EventSources.Projects:
                if (t.Event != ev.Kind) return null;
                if (ev.Kind is "task.created" or "task.completed"
                    && t.Config["projectId"]?.GetValue<string>() is { Length: > 0 } project
                    && !string.Equals(Tokens.Text(Tokens.Resolve(data, "task.projectId")), project, StringComparison.OrdinalIgnoreCase)) return null;
                return data;
            default:
                return null;
        }
    }

    /// <summary>"Record updated": any change, or — with fields chosen — a change to one of them (by id or name).</summary>
    public static bool ChangedWatched(TriggerDef t, JsonObject data)
    {
        var watched = (t.Config["fieldIds"] as JsonArray)?.Select(Tokens.Text).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = data["changes"] as JsonArray ?? [];
        if (watched is not { Count: > 0 }) return changes.Count > 0 || data["changes"] is null;
        return changes.Any(c => watched.Contains(Tokens.Text(c?["fieldId"])) || watched.Contains(Tokens.Text(c?["field"])));
    }

    /// <summary>"Record matches": the condition holds now and didn't before this change (a new record: just now).</summary>
    public static bool BecameTrue(TriggerDef t, JsonObject data)
    {
        if (Conditions.Parse(t.Config["condition"]) is not { Rules.Count: > 0 } cond) return false;
        if (!Conditions.Evaluate(cond, new JsonObject { ["trigger"] = data.DeepClone() })) return false;
        if (data["changes"] is not JsonArray { Count: > 0 } changes) return Tokens.Text(data["event"]) != "record.updated";
        // The record as it was: this change's "from" values over its fields now.
        var before = (JsonObject)data.DeepClone();
        if (before["record"]?["fields"] is JsonObject fields)
            foreach (var c in changes)
            {
                var name = Tokens.Text(c?["field"]);
                if (name.Length == 0) continue;
                var from = c?["from"]?.DeepClone();
                if (from is null) fields.Remove(name); else fields[name] = from;
            }
        return !Conditions.Evaluate(cond, new JsonObject { ["trigger"] = before });
    }
}

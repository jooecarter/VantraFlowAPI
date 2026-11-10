using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>A trigger or action the studio offers: its app, its name, the config keys it needs and how to name them.</summary>
public sealed record CatalogEntry(string App, string Key, string Title, IReadOnlyList<string> Required, IReadOnlyDictionary<string, string> Labels)
{
    public string Label(string key) => Labels.TryGetValue(key, out var l) ? l : key;
}

/// <summary>
/// What Flow can listen to and do (v1). The studio has the same list with its forms; the server holds the parts
/// that decide whether a flow can run: which config keys are required, and which app each needs connected.
/// </summary>
public static class FlowCatalog
{
    private static CatalogEntry E(string app, string key, string title, params (string Key, string Label)[] required) =>
        new(app, key, title, required.Select(r => r.Key).ToList(), required.ToDictionary(r => r.Key, r => r.Label));

    public static readonly IReadOnlyList<CatalogEntry> Triggers =
    [
        E("orbit", "record.created", "Record created", ("tableId", "a table")),
        E("orbit", "record.updated", "Record updated", ("tableId", "a table")),
        E("orbit", "record.matches", "Record matches a condition", ("tableId", "a table"), ("condition", "a condition")),
        E("projects", "project.created", "Project created"),
        E("projects", "task.created", "Task created"),
        E("projects", "task.completed", "Task completed"),
        E("projects", "client.created", "Client created"),
        E("schedule", "interval", "Every few minutes or hours", ("every", "how often")),
        E("schedule", "daily", "Every day", ("time", "a time")),
        E("schedule", "weekly", "Every week", ("time", "a time"), ("days", "the days")),
        E("webhook", "catch", "Webhook received"),
    ];

    public static readonly IReadOnlyList<CatalogEntry> Actions =
    [
        E("orbit", "record.create", "Create record", ("tableId", "a table"), ("fields", "at least one field")),
        E("orbit", "record.update", "Update record", ("tableId", "a table"), ("recordId", "the record"), ("fields", "at least one field")),
        E("orbit", "record.find", "Find records", ("tableId", "a table")),
        E("projects", "project.create", "Create project", ("name", "the project's name")),
        E("projects", "task.create", "Create task", ("projectId", "the project"), ("name", "the task's name")),
        E("email", "send", "Send email", ("to", "who it goes to"), ("subject", "a subject")),
        E("http", "request", "HTTP request", ("url", "the URL")),
    ];

    public static CatalogEntry? Trigger(string? app, string? key) => Triggers.FirstOrDefault(t => t.App == app && t.Key == key);
    public static CatalogEntry? Action(string? app, string? key) => Actions.FirstOrDefault(a => a.App == app && a.Key == key);

    /// <summary>The apps a definition needs connected (Orbit, Projects), trigger included.</summary>
    public static IReadOnlySet<string> AppsNeeded(FlowDefinition def)
    {
        var apps = new HashSet<string>(StringComparer.Ordinal);
        if (def.Trigger.App is "orbit" or "projects") apps.Add(def.Trigger.App);
        foreach (var s in def.AllSteps())
            if (s.Type == StepTypes.Action && s.App is "orbit" or "projects") apps.Add(s.App);
        return apps;
    }

    /// <summary>What a trigger hands the first step when there's no real event to test with.</summary>
    public static JsonObject Sample(TriggerDef t, DateTime now) => (t.App, t.Event) switch
    {
        ("orbit", _) => new JsonObject
        {
            ["event"] = t.Event == "record.matches" ? "record.updated" : t.Event,
            ["table"] = new JsonObject { ["id"] = t.Config["tableId"]?.GetValue<string>() ?? "tbl_sample", ["name"] = "Sample table" },
            ["record"] = new JsonObject
            {
                ["id"] = "rec_sample",
                ["url"] = "https://orbit.vantrasoftware.com/r/rec_sample",
                ["fields"] = new JsonObject { ["Name"] = "Ada Lovelace", ["Email"] = "ada@example.com", ["Stage"] = "Won" },
            },
            ["changes"] = new JsonArray(),
            ["actor"] = new JsonObject { ["type"] = "user", ["name"] = "Sample person" },
        },
        ("projects", "project.created") => new JsonObject
        {
            ["event"] = t.Event,
            ["project"] = new JsonObject { ["id"] = Guid.Empty, ["name"] = "Website refresh", ["url"] = "https://projects.vantrasoftware.com" },
            ["actor"] = "Sample person",
        },
        ("projects", "client.created") => new JsonObject
        {
            ["event"] = t.Event,
            ["client"] = new JsonObject { ["id"] = Guid.Empty, ["name"] = "Example Ltd", ["email"] = "hello@example.com" },
            ["actor"] = "Sample person",
        },
        ("projects", _) => new JsonObject
        {
            ["event"] = t.Event,
            ["task"] = new JsonObject { ["id"] = Guid.Empty, ["name"] = "Send the proposal", ["projectId"] = Guid.Empty },
            ["project"] = new JsonObject { ["id"] = Guid.Empty, ["name"] = "Website refresh" },
            ["actor"] = "Sample person",
        },
        ("schedule", _) => new JsonObject { ["scheduledFor"] = now.ToString("o"), ["timeZone"] = "UTC" },
        _ => new JsonObject { ["body"] = new JsonObject { ["hello"] = "world" }, ["headers"] = new JsonObject() },
    };
}

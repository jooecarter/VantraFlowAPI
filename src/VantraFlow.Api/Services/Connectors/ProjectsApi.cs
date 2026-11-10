using System.Globalization;
using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// Vantra Projects' connector. Connect: Projects' integration endpoints (<c>/api/integrations/flow/v1</c>) take the
/// assertion and, for an owner or admin, make a workspace API key named "Vantra Flow" (shown in Settings →
/// Developer, where it can be revoked). After that, the same endpoints with that key (<c>X-Api-Key</c>), always in
/// the key's workspace: projects for the pickers, projects and tasks for the actions, and a workspace webhook for
/// the events — task.created, task.completed, project.created and client.created, signed with an HMAC of the
/// body. Tasks are found, updated (status, assignee, due date) and commented on through <c>tasks</c>. Events Flow's key caused carry <c>via.apiKeyId</c>: the origin marker the loop protection reads.
/// </summary>
public sealed class ProjectsApi(IHttpClientFactory http, IConfiguration config) : AppApi(http, config)
{
    public override string App => "projects";
    public override string AppName => "Vantra Projects";
    protected override string DefaultApiUrl => "https://vantra-projects-api-hhb0a5h7dqamaece.ukwest-01.azurewebsites.net";
    protected override string DefaultAppUrl => "https://projects.vantrasoftware.com";
    public const string Events = "project.created, task.created, task.completed, client.created";
    private const string P = "/api/integrations/flow/v1";

    protected override void Authenticate(HttpRequestMessage req, string key) =>
        req.Headers.TryAddWithoutValidation("X-Api-Key", key);

    public override async Task<JsonObject> SchemaAsync(string key, CancellationToken ct)
    {
        var projects = await SendAsync<JsonArray>(HttpMethod.Get, P + "/projects", null, key, null, ct);
        var list = new JsonArray();
        foreach (var p in projects.OfType<JsonObject>().Take(500))
            list.Add(new JsonObject { ["id"] = p["id"]?.DeepClone(), ["name"] = p["name"]?.DeepClone(), ["color"] = p["color"]?.DeepClone() });
        return new JsonObject { ["projects"] = list };
    }

    public override async Task<AppHook> SubscribeAsync(string key, string scope, string url, CancellationToken ct)
    {
        var res = await SendAsync<JsonObject>(HttpMethod.Post, P + "/webhooks", new { url }, key, null, ct);
        var id = Tokens.Text(res["id"]);
        var secret = Tokens.Text(res["secret"]);
        if (id.Length == 0 || secret.Length == 0) throw new AppUnavailableException("Vantra Projects answered something unexpected.");
        return new AppHook(id, secret);
    }

    public override Task UnsubscribeAsync(string key, string remoteId, CancellationToken ct) =>
        SendAsync<bool>(HttpMethod.Delete, P + "/webhooks/" + Uri.EscapeDataString(remoteId), null, key, null, ct);

    public override async Task<JsonObject?> SampleAsync(string key, TriggerDef trigger, CancellationToken ct)
    {
        if (trigger.Event is not ("project.created" or "task.created" or "task.completed")) return null;
        var projects = await SendAsync<JsonArray>(HttpMethod.Get, P + "/projects", null, key, null, ct);
        if (projects.OfType<JsonObject>().FirstOrDefault() is not { } p) return null;
        var project = new JsonObject { ["id"] = p["id"]?.DeepClone(), ["name"] = p["name"]?.DeepClone(), ["url"] = $"{AppUrl}/projects/{Tokens.Text(p["id"])}" };
        if (trigger.Event == "project.created") return new JsonObject { ["event"] = trigger.Event, ["project"] = project, ["actor"] = "Sample" };
        return new JsonObject
        {
            ["event"] = trigger.Event,
            ["task"] = new JsonObject { ["id"] = Guid.Empty.ToString(), ["name"] = "Sample task", ["projectId"] = p["id"]?.DeepClone() },
            ["project"] = project,
            ["actor"] = "Sample",
        };
    }

    public override async Task<JsonNode?> ActAsync(string key, string action, JsonObject input, string idempotencyKey, CancellationToken ct)
    {
        switch (action)
        {
            case "project.create":
            {
                var body = new
                {
                    name = Required(input, "name", "The project's name"),
                    description = Opt(input, "description"),
                    startDate = Date(input, "startDate"),
                    dueDate = Date(input, "dueDate"),
                };
                var p = await SendAsync<JsonObject>(HttpMethod.Post, P + "/projects", body, key, null, ct, idempotencyKey);
                return Shape(p, $"{AppUrl}/projects/{Tokens.Text(p["id"])}");
            }
            case "task.create":
            {
                var projectId = Required(input, "projectId", "The project");
                if (!Guid.TryParse(projectId, out var pid)) throw new AppRefusedException(400, "The project isn't a Vantra Projects project id.", "bad_project");
                var body = new
                {
                    name = Required(input, "name", "The task's name"),
                    description = Opt(input, "description"),
                    priority = Opt(input, "priority"),
                    dueDate = Date(input, "dueDate"),
                };
                var t = await SendAsync<JsonObject>(HttpMethod.Post, $"{P}/projects/{pid}/tasks", body, key, null, ct, idempotencyKey);
                return Shape(t, $"{AppUrl}/projects/{pid}?task={Tokens.Text(t["id"])}");
            }
            case "task.find":
                return await FindTasksAsync(key, input, ct);
            case "task.update":
            {
                var taskId = TaskId(input);
                var body = new { status = Opt(input, "status"), assignee = Opt(input, "assignee"), dueDate = Opt(input, "dueDate") };
                if (body.status is null && body.assignee is null && body.dueDate is null)
                    throw new AppRefusedException(400, "Nothing to change: fill in the status, the assignee or the due date.", "empty");
                return await SendAsync<JsonObject>(HttpMethod.Patch, $"{P}/tasks/{taskId}", body, key, null, ct, idempotencyKey);
            }
            case "task.comment":
            {
                var taskId = TaskId(input);
                var text = Required(input, "body", "The comment");
                var c = await SendAsync<JsonObject>(HttpMethod.Post, $"{P}/tasks/{taskId}/comments", new { body = text.Length > 10_000 ? text[..10_000] : text },
                    key, null, ct, idempotencyKey);
                return new JsonObject { ["id"] = c["id"]?.DeepClone(), ["taskId"] = taskId.ToString(), ["body"] = c["body"]?.DeepClone() ?? text };
            }
            default:
                throw new AppRefusedException(400, "Vantra Projects can't do that.", "unknown_action");
        }
    }

    private static Guid TaskId(JsonObject input) =>
        Guid.TryParse(Required(input, "taskId", "The task"), out var id) ? id
            : throw new AppRefusedException(400, "The task isn't a Vantra Projects task id.", "bad_task");

    /// <summary>
    /// "Find tasks": up to <c>limit</c> (1–100, default 25) tasks, open ones first, across projects or in one, matching a
    /// name, a column, an assignee's email, open only, or due within <c>dueWithinDays</c>. Hands on
    /// <c>{ count, first, tasks, names }</c>; <c>tasks</c> is the list a "Repeat for each" step goes through.
    /// </summary>
    private async Task<JsonNode> FindTasksAsync(string key, JsonObject input, CancellationToken ct)
    {
        var q = new List<string>();
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) q.Add($"{name}={Uri.EscapeDataString(value.Trim())}"); }
        if (Opt(input, "projectId") is { } p)
        {
            if (!Guid.TryParse(p, out var pid)) throw new AppRefusedException(400, "The project isn't a Vantra Projects project id.", "bad_project");
            Add("projectId", pid.ToString());
        }
        Add("q", Opt(input, "q"));
        Add("status", Opt(input, "status"));
        Add("assignee", Opt(input, "assignee"));
        if (Opt(input, "open") is "true" or "yes" or "1") q.Add("open=true");
        if (double.TryParse(Opt(input, "dueWithinDays"), NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days > 0)
            q.Add("dueBefore=" + Uri.EscapeDataString(DateTime.UtcNow.Date.AddDays(Math.Min(days, 3650) + 1).ToString("yyyy-MM-dd")));
        var limit = int.TryParse(Opt(input, "limit"), out var l) ? Math.Clamp(l, 1, 100) : 25;
        q.Add($"limit={limit}");
        var list = await SendAsync<JsonArray>(HttpMethod.Get, $"{P}/tasks?{string.Join("&", q)}", null, key, null, ct);
        var tasks = new JsonArray(list.OfType<JsonObject>().Select(t => (JsonNode)t.DeepClone()).ToArray());
        return new JsonObject
        {
            ["count"] = tasks.Count,
            ["first"] = tasks.Count > 0 ? tasks[0]!.DeepClone() : null,
            ["tasks"] = tasks,
            ["names"] = string.Join(", ", tasks.OfType<JsonObject>().Select(t => Tokens.Text(t["name"]))),
        };
    }

    private static JsonObject Shape(JsonObject o, string url)
    {
        var copy = (JsonObject)o.DeepClone();
        copy["url"] = url;
        return copy;
    }

    private static string? Opt(JsonObject input, string key) => Tokens.Text(input[key]) is { Length: > 0 } s ? s.Trim() : null;

    private static DateTime? Date(JsonObject input, string key) =>
        Opt(input, key) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
}

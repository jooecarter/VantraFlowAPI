using System.Globalization;
using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// Vantra Projects' connector. Connect: Projects' integration endpoints (<c>/api/integrations/flow/v1</c>) take the
/// assertion and, for an owner or admin, make a workspace API key named "Vantra Flow" (shown in Settings →
/// Developer, where it can be revoked). After that, Projects' own API with that key (<c>X-Api-Key</c>): projects
/// for the pickers, <c>POST /api/projects</c> and <c>POST /api/projects/{id}/tasks</c> for the actions, and a
/// workspace developer webhook (<c>/api/developer/webhooks</c>) for the events — task.created, task.completed,
/// project.created and client.created, signed with an HMAC of the body. Events Flow's key caused carry
/// <c>via.apiKeyId</c>: the origin marker the loop protection reads.
/// </summary>
public sealed class ProjectsApi(IHttpClientFactory http, IConfiguration config) : AppApi(http, config)
{
    public override string App => "projects";
    public override string AppName => "Vantra Projects";
    protected override string DefaultApiUrl => "https://vantra-projects-api-hhb0a5h7dqamaece.ukwest-01.azurewebsites.net";
    protected override string DefaultAppUrl => "https://projects.vantrasoftware.com";
    public const string Events = "project.created, task.created, task.completed, client.created";

    protected override void Authenticate(HttpRequestMessage req, string key) =>
        req.Headers.TryAddWithoutValidation("X-Api-Key", key);

    public override async Task<JsonObject> SchemaAsync(string key, CancellationToken ct)
    {
        var projects = await SendAsync<JsonArray>(HttpMethod.Get, "/api/projects", null, key, null, ct);
        var list = new JsonArray();
        foreach (var p in projects.OfType<JsonObject>().Take(500))
            list.Add(new JsonObject { ["id"] = p["id"]?.DeepClone(), ["name"] = p["name"]?.DeepClone(), ["color"] = p["color"]?.DeepClone() });
        return new JsonObject { ["projects"] = list };
    }

    public override async Task<AppHook> SubscribeAsync(string key, string scope, string url, CancellationToken ct)
    {
        var res = await SendAsync<JsonObject>(HttpMethod.Post, "/api/developer/webhooks", new { url, events = Events }, key, null, ct);
        var id = Tokens.Text(res["id"]);
        var secret = Tokens.Text(res["secret"]);
        if (id.Length == 0 || secret.Length == 0) throw new AppUnavailableException("Vantra Projects answered something unexpected.");
        return new AppHook(id, secret);
    }

    public override Task UnsubscribeAsync(string key, string remoteId, CancellationToken ct) =>
        SendAsync<bool>(HttpMethod.Delete, "/api/developer/webhooks/" + Uri.EscapeDataString(remoteId), null, key, null, ct);

    public override async Task<JsonObject?> SampleAsync(string key, TriggerDef trigger, CancellationToken ct)
    {
        if (trigger.Event is not ("project.created" or "task.created" or "task.completed")) return null;
        var projects = await SendAsync<JsonArray>(HttpMethod.Get, "/api/projects", null, key, null, ct);
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
                var p = await SendAsync<JsonObject>(HttpMethod.Post, "/api/projects", body, key, null, ct, idempotencyKey);
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
                var t = await SendAsync<JsonObject>(HttpMethod.Post, $"/api/projects/{pid}/tasks", body, key, null, ct, idempotencyKey);
                return Shape(t, $"{AppUrl}/projects/{pid}?task={Tokens.Text(t["id"])}");
            }
            default:
                throw new AppRefusedException(400, "Vantra Projects can't do that.", "unknown_action");
        }
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

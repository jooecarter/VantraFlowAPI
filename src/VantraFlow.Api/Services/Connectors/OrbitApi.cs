using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// Vantra Orbit's connector. Connect: Orbit's integration endpoints (<c>/api/integrations/flow/v1</c>) take the
/// assertion and, for an owner or admin, mint an API key named "Vantra Flow" for that workspace (it shows under
/// Developers → API keys in Orbit, where it can be revoked). Everything after that is Orbit's own REST API v1
/// with that key: tables for the pickers, records for the actions (with an Idempotency-Key, so a retried step
/// never makes a record twice), and <c>/api/v1/webhooks</c> to register Flow's receiver on a table — Orbit's
/// outgoing webhooks (db/012): queued in the writer's own transaction, signed, retried with backoff. A record
/// Flow writes comes back as an event whose <c>source</c> is this key: the origin marker the loop protection
/// reads.
/// </summary>
public sealed class OrbitApi(IHttpClientFactory http, IConfiguration config) : AppApi(http, config)
{
    public override string App => "orbit";
    public override string AppName => "Vantra Orbit";
    protected override string DefaultApiUrl => "https://vantraorbit.azurewebsites.net";
    protected override string DefaultAppUrl => "https://orbit.vantrasoftware.com";
    public static readonly string[] Events = ["record.created", "record.updated"];

    protected override void Authenticate(HttpRequestMessage req, string key) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    public override async Task<JsonObject> SchemaAsync(string key, CancellationToken ct)
    {
        var tables = await SendAsync<JsonArray>(HttpMethod.Get, "/api/v1/tables", null, key, null, ct);
        return new JsonObject { ["tables"] = tables };
    }

    public override async Task<AppHook> SubscribeAsync(string key, string scope, string url, CancellationToken ct)
    {
        var res = await SendAsync<JsonObject>(HttpMethod.Post, "/api/v1/webhooks",
            new { tableId = scope, url, events = Events, name = "Vantra Flow" }, key, null, ct);
        var id = Tokens.Text(res["id"]);
        var secret = Tokens.Text(res["secret"]);
        if (id.Length == 0 || secret.Length == 0) throw new AppUnavailableException("Vantra Orbit answered something unexpected.");
        return new AppHook(id, secret);
    }

    public override Task UnsubscribeAsync(string key, string remoteId, CancellationToken ct) =>
        SendAsync<bool>(HttpMethod.Delete, "/api/v1/webhooks/" + Uri.EscapeDataString(remoteId), null, key, null, ct);

    public override async Task<JsonObject?> SampleAsync(string key, TriggerDef trigger, CancellationToken ct)
    {
        var tableId = Tokens.Text(trigger.Config["tableId"]);
        if (tableId.Length == 0) return null;
        var t = Uri.EscapeDataString(tableId);
        var table = await SendAsync<JsonObject>(HttpMethod.Get, $"/api/v1/tables/{t}", null, key, null, ct);
        var page = await SendAsync<JsonObject>(HttpMethod.Get, $"/api/v1/tables/{t}/records?limit=1&sort=createdAt&order=desc", null, key, null, ct);
        if (page["data"] is not JsonArray { Count: > 0 } data || data[0] is not JsonObject rec) return null;
        return TriggerData(trigger.Event == "record.created" ? "record.created" : "record.updated",
            new JsonObject { ["id"] = tableId, ["name"] = table["name"]?.DeepClone() }, Record(rec), new JsonArray(),
            new JsonObject { ["type"] = "user", ["name"] = "Sample" });
    }

    /// <summary>What an Orbit trigger hands its flow: the event, table, record, changes and who made it.</summary>
    public static JsonObject TriggerData(string kind, JsonNode? table, JsonNode? record, JsonNode? changes, JsonNode? actor) => new()
    {
        ["event"] = kind,
        ["table"] = table?.DeepClone(),
        ["record"] = record?.DeepClone(),
        ["changes"] = changes?.DeepClone() ?? new JsonArray(),
        ["actor"] = actor?.DeepClone(),
    };

    private JsonObject Record(JsonObject rec)
    {
        var id = Tokens.Text(rec["id"]);
        var o = (JsonObject)rec.DeepClone();
        o["url"] = $"{AppUrl}/r/{Uri.EscapeDataString(id)}";
        return o;
    }

    public override async Task<JsonNode?> ActAsync(string key, string action, JsonObject input, string idempotencyKey, CancellationToken ct)
    {
        var table = Uri.EscapeDataString(Required(input, "tableId", "The table"));
        var fields = Fields(input["fields"]);
        if (fields.Count == 0) throw new AppRefusedException(400, "None of the fields have a value.", "empty");
        switch (action)
        {
            case "record.create":
                return Record(await SendAsync<JsonObject>(HttpMethod.Post, $"/api/v1/tables/{table}/records", new { fields }, key, null, ct, idempotencyKey));
            case "record.update":
                var record = Uri.EscapeDataString(Required(input, "recordId", "The record"));
                return Record(await SendAsync<JsonObject>(HttpMethod.Patch, $"/api/v1/tables/{table}/records/{record}", new { fields }, key, null, ct));
            default:
                throw new AppRefusedException(400, "Vantra Orbit can't do that.", "unknown_action");
        }
    }

    /// <summary>The field values to write: by name or id, empty ones left out (an empty token doesn't clear a field).</summary>
    public static JsonObject Fields(JsonNode? node)
    {
        var o = new JsonObject();
        if (node is not JsonObject fields) return o;
        foreach (var (name, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(name) || DefinitionRules.Empty(value)) continue;
            o[name.Trim()] = value!.DeepClone();
        }
        return o;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Everything that reaches Flow from outside, anonymous but verified, rate limited per address:
/// <code>
/// POST api/hooks/{hookId}               a webhook trigger: JSON or a form in; with a secret, X-Flow-Signature / X-Flow-Timestamp;
///                                       the newest payload kept as the builder's sample (db/007), also while the flow is off
/// POST api/events/orbit/{subscription}  Orbit's outgoing webhook: X-Vantra-Signature over "{X-Vantra-Timestamp}.{body}"
/// POST api/events/projects/{subscription} Projects' developer webhook: X-Vantra-Signature over the body, its timestamp
/// </code>
/// Each answers quickly (the event is queued for the worker) and the same way for an unknown address as a bad
/// signature, so a guess learns nothing. Deliveries are de-duplicated by the app's event id, and stale ones (over
/// five minutes old) refused, so a replayed delivery starts nothing.
/// </summary>
[ApiController]
[ServiceFilter(typeof(FlowReadyFilter))]
[EnableRateLimiting(RateLimitPolicy)]
public sealed class IncomingController(
    FlowsRepository flows, ConnectionsRepository connections, EngineRepository engine, Connections conns, FlowSecrets secrets, FlowSignal signal,
    ILogger<IncomingController> log, FlowSchema schema, StepsRepository steps) : ControllerBase
{
    public const string RateLimitPolicy = "incoming";
    public const int MaxBodyBytes = 256 * 1024;
    public const string SignatureHeader = "X-Flow-Signature";
    public const string TimestampHeader = "X-Flow-Timestamp";

    [HttpPost("api/hooks/{hookId}")]
    public async Task<IActionResult> Hook(string hookId)
    {
        if (hookId.Length is < 8 or > 40) return Unknown();
        var body = await ReadAsync();
        if (body is null) return StatusCode(413, new { error = "The body is over 256 KB." });
        // A flow that's off still keeps a sample (once db/007 has run), so the builder can map its fields; it starts nothing.
        var samples = await schema.StepsReadyAsync();
        if (await flows.ByHookAsync(hookId) is not { } flow || (!flow.Enabled && !samples)) return Unknown();
        if (flow.HookSecretCipher is not null)
        {
            var secret = secrets.Unprotect(flow.HookSecretCipher, flow.WorkspaceId, hookId);
            if (secret is null || !FlowSecrets.VerifyTimestamped(secret, Request.Headers[SignatureHeader], Request.Headers[TimestampHeader], body, DateTime.UtcNow))
                return Unknown();
        }
        if (HookBody(body, Request.ContentType) is not { } parsed) return BadRequest(new { error = "The body must be JSON or a form." });
        var query = new JsonObject();
        foreach (var (k, v) in Request.Query.Take(50)) query[k] = v.ToString();
        var payload = new JsonObject
        {
            ["body"] = parsed, ["query"] = query, ["headers"] = SafeHeaders(Request.Headers), ["receivedAt"] = DateTime.UtcNow.ToString("o"),
        };
        var text = payload.ToJsonString();
        if (samples) await steps.SaveHookSampleAsync(flow.WorkspaceId, flow.Id, text, DateTime.UtcNow);
        if (!flow.Enabled) return StatusCode(202, new { accepted = true, captured = true });
        var id = FlowSecrets.NewRowId("fev_");
        await engine.EnqueueAsync(flow.WorkspaceId, EventSources.Webhook, "catch", null, flow.Id, text, false, DateTime.UtcNow, id: id);
        signal.Poke();
        return StatusCode(202, new { accepted = true, id });
    }

    /// <summary>
    /// A webhook's body as trigger data: JSON as it is; a form (<c>application/x-www-form-urlencoded</c>) as an object of
    /// its fields (a field sent more than once, a list); empty, an empty object. Null when it's neither.
    /// </summary>
    public static JsonNode? HookBody(byte[] body, string? contentType)
    {
        if (body.Length == 0) return new JsonObject();
        if (contentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
        {
            var form = new JsonObject();
            var fields = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(System.Text.Encoding.UTF8.GetString(body));
            foreach (var (k, v) in fields.Take(200))
                form[k] = v.Count > 1 ? new JsonArray(v.Select(x => (JsonNode?)JsonValue.Create(x ?? "")).ToArray()) : JsonValue.Create(v.ToString());
            return form;
        }
        try { return JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) ?? new JsonObject(); }
        catch (JsonException) { return null; }
    }

    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Authorization", "Cookie", "Proxy-Authorization", SignatureHeader, TimestampHeader, "X-Api-Key", "X-Hub-Signature", "X-Hub-Signature-256", "Stripe-Signature" };

    /// <summary>The request's headers a flow may read: the sender's content type, user agent and its own <c>X-</c> headers, never credentials or signatures.</summary>
    public static JsonObject SafeHeaders(IHeaderDictionary headers)
    {
        var o = new JsonObject();
        foreach (var (k, v) in headers)
        {
            if (o.Count >= 30 || SecretHeaders.Contains(k) || k.Contains("token", StringComparison.OrdinalIgnoreCase) || k.Contains("secret", StringComparison.OrdinalIgnoreCase)) continue;
            if (!(k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                  || k.StartsWith("X-", StringComparison.OrdinalIgnoreCase)) || k.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("X-ARR", StringComparison.OrdinalIgnoreCase) || k.StartsWith("X-Original", StringComparison.OrdinalIgnoreCase)
                || k.Equals("X-Client-IP", StringComparison.OrdinalIgnoreCase) || k.StartsWith("X-Site", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("X-Waws", StringComparison.OrdinalIgnoreCase) || k.StartsWith("X-AppService", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("X-MS-", StringComparison.OrdinalIgnoreCase)) continue;
            var text = v.ToString();
            o[k.ToLowerInvariant()] = text.Length > 300 ? text[..300] : text;
        }
        return o;
    }

    [HttpPost("api/events/orbit/{subscriptionId}")]
    public async Task<IActionResult> Orbit(string subscriptionId)
    {
        var body = await ReadAsync();
        if (body is null) return StatusCode(413, new { error = "Too large." });
        if (await connections.SubscriptionAsync(subscriptionId) is not { App: "orbit" } sub || conns.SubscriptionSecret(sub) is not { } secret) return Unknown();
        if (!FlowSecrets.VerifyTimestamped(secret, Request.Headers["X-Vantra-Signature"], Request.Headers["X-Vantra-Timestamp"], body, DateTime.UtcNow))
            return Unknown();
        if (await connections.GetAsync(sub.ConnectionId) is not { Status: not "revoked" } conn) return Unknown();

        JsonObject root;
        try { root = JsonNode.Parse(body) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return BadRequest(new { error = "The body must be JSON." }); }
        var type = Tokens.Text(root["type"]);
        if (type is not ("record.created" or "record.updated")) return Ok(new { ignored = true });
        var eventId = Request.Headers["X-Vantra-Event-Id"].ToString() is { Length: > 0 and <= 80 } e ? e : Tokens.Text(root["id"]);
        if (!await engine.ClaimKeyAsync(sub.WorkspaceId, "", $"in:{sub.Id}:{eventId}")) return Ok(new { duplicate = true });

        var flowCaused = IsFlowOrbit(root, conn.KeyId);
        var data = OrbitApi.TriggerData(type, root["table"], root["record"], root["changes"], root["actor"]);
        await engine.EnqueueAsync(sub.WorkspaceId, EventSources.Orbit, type, Tokens.Text(root["table"]?["id"]), null, data.ToJsonString(), flowCaused, DateTime.UtcNow);
        await connections.SubscriptionSeenAsync(sub.Id);
        signal.Poke();
        return Ok(new { accepted = true });
    }

    [HttpPost("api/events/projects/{subscriptionId}")]
    public async Task<IActionResult> Projects(string subscriptionId)
    {
        var body = await ReadAsync();
        if (body is null) return StatusCode(413, new { error = "Too large." });
        if (await connections.SubscriptionAsync(subscriptionId) is not { App: "projects" } sub || conns.SubscriptionSecret(sub) is not { } secret) return Unknown();
        if (!FlowSecrets.SignatureMatches(Request.Headers["X-Vantra-Signature"], HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), body)))
            return Unknown();
        if (await connections.GetAsync(sub.ConnectionId) is not { Status: not "revoked" } conn) return Unknown();

        JsonObject root;
        try { root = JsonNode.Parse(body) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return BadRequest(new { error = "The body must be JSON." }); }
        // Projects signs the body alone, so its own timestamp (inside the signed body) bounds a replay.
        if (!DateTime.TryParse(Tokens.Text(root["timestamp"]), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
            || Math.Abs((DateTime.UtcNow - at).TotalMinutes) > 5) return Unknown();
        var kind = Tokens.Text(root["event"]);
        if (kind is not ("project.created" or "task.created" or "task.completed" or "client.created")) return Ok(new { ignored = true });
        var delivery = Request.Headers["X-Vantra-Delivery"].ToString() is { Length: > 0 and <= 80 } d ? d : Convert.ToHexStringLower(SHA256.HashData(body));
        if (!await engine.ClaimKeyAsync(sub.WorkspaceId, "", $"in:{sub.Id}:{delivery}")) return Ok(new { duplicate = true });

        var flowCaused = string.Equals(Tokens.Text(root["via"]?["apiKeyId"]), conn.KeyId, StringComparison.OrdinalIgnoreCase);
        root.Remove("via");
        await engine.EnqueueAsync(sub.WorkspaceId, EventSources.Projects, kind, null, null, root.ToJsonString(), flowCaused, DateTime.UtcNow);
        await connections.SubscriptionSeenAsync(sub.Id);
        signal.Poke();
        return Ok(new { accepted = true });
    }

    /// <summary>The origin marker: Orbit says the change came through the REST API with this connection's key.</summary>
    public static bool IsFlowOrbit(JsonObject payload, string keyId) =>
        Tokens.Text(payload["source"]?["kind"]) == "api" && string.Equals(Tokens.Text(payload["source"]?["keyId"]), keyId, StringComparison.Ordinal);

    private async Task<byte[]?> ReadAsync()
    {
        if (Request.ContentLength > MaxBodyBytes) return null;
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await Request.Body.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxBodyBytes) return null;
        }
        return ms.ToArray();
    }

    private NotFoundObjectResult Unknown()
    {
        log.LogDebug("Incoming request refused for {Path}", Request.Path.Value?.Split('/').Take(4).Aggregate((a, b) => a + "/" + b));
        return NotFound(new { error = "There's nothing here.", code = "unknown" });
    }
}

[ApiController]
public sealed class HealthController(FlowSchema schema) : ControllerBase
{
    [HttpGet("api/health")]
    public async Task<IActionResult> Get()
    {
        bool ready;
        try { ready = await schema.ReadyAsync(); } catch (Exception) { ready = false; }
        return Ok(new { ok = true, ready });
    }
}

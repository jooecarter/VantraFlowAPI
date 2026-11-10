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
/// POST api/hooks/{hookId}               a webhook trigger: JSON in; with a secret, X-Flow-Signature / X-Flow-Timestamp
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
    ILogger<IncomingController> log) : ControllerBase
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
        if (await flows.ByHookAsync(hookId) is not { Enabled: true } flow) return Unknown();
        if (flow.HookSecretCipher is not null)
        {
            var secret = secrets.Unprotect(flow.HookSecretCipher, flow.WorkspaceId, hookId);
            if (secret is null || !FlowSecrets.VerifyTimestamped(secret, Request.Headers[SignatureHeader], Request.Headers[TimestampHeader], body, DateTime.UtcNow))
                return Unknown();
        }
        JsonNode? json = null;
        if (body.Length > 0)
        {
            try { json = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { return BadRequest(new { error = "The body must be JSON." }); }
        }
        var query = new JsonObject();
        foreach (var (k, v) in Request.Query.Take(50)) query[k] = v.ToString();
        var payload = new JsonObject { ["body"] = json ?? new JsonObject(), ["query"] = query, ["receivedAt"] = DateTime.UtcNow.ToString("o") };
        var id = FlowSecrets.NewRowId("fev_");
        await engine.EnqueueAsync(flow.WorkspaceId, EventSources.Webhook, "catch", null, flow.Id, payload.ToJsonString(), false, DateTime.UtcNow, id: id);
        signal.Poke();
        return StatusCode(202, new { accepted = true, id });
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

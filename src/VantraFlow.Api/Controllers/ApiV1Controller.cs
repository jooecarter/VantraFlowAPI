using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Developer;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// The public developer API, v1 (<c>/api/v1</c>, db/012): server to server with <c>Authorization: Bearer vfk_…</c>, a
/// workspace's key from Settings → Developers. Separate from signing in: no cookies, no CORS, <c>Cache-Control:
/// no-store</c>, 120 requests a minute per key (<c>X-RateLimit-*</c> on every answer, 429 with <c>Retry-After</c>).
/// Errors are <c>{ error: { code, message, … } }</c>. On Pro and Business (402 <c>plan_api</c> below).
/// <code>
/// GET    me                          → the key and its workspace
/// GET    flows?enabled=&amp;trigger=&amp;q=&amp;limit=&amp;cursor=   flows:read
/// GET    flows/{id}                  flows:read (with its definition)
/// POST   flows/{id}/enable | disable flows:write
/// POST   flows/{id}/runs             runs:write { data? } → 202, the run (Idempotency-Key)
/// GET    runs?flowId=&amp;status=&amp;from=&amp;to=&amp;includeTests=&amp;limit=&amp;cursor=   runs:read
/// GET    runs/{id}                   runs:read (with its steps)
/// POST   runs/{id}/replay            runs:write → 202, the new run (Idempotency-Key)
/// GET    usage                       this month's runs against the plan
/// GET|POST webhooks, GET|PATCH|DELETE webhooks/{id}, POST webhooks/{id}/test, GET webhooks/{id}/deliveries   webhooks:manage
/// </code>
/// The key acts for the workspace as the person who made it, while they're still in it (<see cref="DevKeyFilter"/>).
/// </summary>
[ApiController]
[Route("api/v1")]
[AllowAnonymous]
[DisableCors]
[ServiceFilter(typeof(DevKeyFilter))]
public sealed class ApiV1Controller(
    FlowsRepository flows, DeveloperRepository repo, EngineRepository engine, WorkspacesRepository workspaces, FlowSwitch flowSwitch,
    Services.Billing.Plans plans, FlowSignal signal, DevEndpoints endpoints, IConfiguration config) : ControllerBase
{
    private DevCaller Caller => (DevCaller)HttpContext.Items[DevKeyFilter.CallerKey]!;
    private Guid Ws => Caller.WorkspaceId;

    // ── The key ──

    [HttpGet("me")]
    public IActionResult Me() => Ok(new JsonObject
    {
        ["workspace"] = new JsonObject { ["id"] = Ws.ToString("D"), ["name"] = Caller.WorkspaceName },
        ["key"] = new JsonObject
        {
            ["id"] = Caller.Key.Id, ["name"] = Caller.Key.Name, ["prefix"] = Caller.Key.Prefix,
            ["scopes"] = new JsonArray(DevScopes.All.Where(Caller.Scopes.Contains).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["expiresAt"] = DevJson.Iso(Caller.Key.ExpiresAt),
        },
    });

    // ── Flows ──

    [HttpGet("flows")]
    [DevScope(DevScopes.FlowsRead)]
    public async Task<IActionResult> Flows(string? enabled = null, string? trigger = null, string? q = null, string? limit = null, string? cursor = null)
    {
        if (Limit(limit) is not { } take) return Bad($"limit must be 1–{DevConstants.MaxLimit}.");
        bool? on = enabled switch { null or "" => null, "true" => true, "false" => false, _ => (bool?)null };
        if (enabled is not (null or "" or "true" or "false")) return Bad("enabled must be true or false.");
        var term = q?.Trim() is { Length: > 0 } t ? t : null;
        var app = trigger?.Trim() is { Length: > 0 } a ? a : null;
        var signature = $"flows|{on}|{app}|{term}";
        (DateTime At, string Id)? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (DevCursor.Decode(cursor, signature) is not { } c || !long.TryParse(c.A, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                return BadCursor();
            after = (new DateTime(ticks, DateTimeKind.Utc), c.B);
        }
        var list = (await flows.ListAsync(Ws))
            .Where(f => on is null || f.Enabled == on)
            .Where(f => app is null || string.Equals(f.TriggerApp, app, StringComparison.OrdinalIgnoreCase))
            .Where(f => term is null || f.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id, StringComparer.Ordinal)
            .Where(f => after is not { } x || f.CreatedAt < x.At || f.CreatedAt == x.At && string.CompareOrdinal(f.Id, x.Id) < 0)
            .Take(take + 1).ToList();
        var page = list.Take(take).ToList();
        var next = list.Count > take ? DevCursor.Encode(signature, page[^1].CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture), page[^1].Id) : null;
        return Ok(new JsonObject
        {
            ["data"] = new JsonArray(page.Select(f => (JsonNode)DevJson.Flow(f, FlowDefinition.Parse(f.Definition))).ToArray()),
            ["nextCursor"] = next,
        });
    }

    [HttpGet("flows/{id}")]
    [DevScope(DevScopes.FlowsRead)]
    public async Task<IActionResult> Flow(string id)
    {
        if (await FlowAsync(id) is not { } f) return NoFlow(id);
        var def = FlowDefinition.Parse(f.Definition);
        var o = DevJson.Flow(f, def);
        o["definition"] = def is null ? null : JsonSerializer.SerializeToNode(def, FlowDefinition.Json);
        return Ok(o);
    }

    [HttpPost("flows/{id}/enable")]
    [DevScope(DevScopes.FlowsWrite)]
    public Task<IActionResult> Enable(string id, CancellationToken ct) => SwitchAsync(id, true, ct);

    [HttpPost("flows/{id}/disable")]
    [DevScope(DevScopes.FlowsWrite)]
    public Task<IActionResult> Disable(string id, CancellationToken ct) => SwitchAsync(id, false, ct);

    private async Task<IActionResult> SwitchAsync(string id, bool on, CancellationToken ct)
    {
        if (await FlowAsync(id) is not { } f) return NoFlow(id);
        var outcome = await flowSwitch.SetAsync(f, on, Caller.Key.CreatedBy, ct);
        if (outcome.Errors is { } errors) return InvalidFlow("This flow can't be switched on yet.", errors);
        if (outcome.Plan is { } block) return DevResults.Error(402, block.Code, block.Message, new JsonObject { ["upgradeTo"] = block.UpgradeTo });
        if (outcome.SubscribeFailed is { } why) return DevResults.Error(502, DevCodes.SubscribeFailed, why);
        return Ok(DevJson.Flow(outcome.Flow!, FlowDefinition.Parse(outcome.Flow!.Definition)));
    }

    /// <summary>
    /// Run a flow now, on or off, with <c>{ data }</c> as its trigger data (a webhook-triggered flow gets it as the request
    /// body, <c>trigger.body</c>). Within the plan: its premium steps and the month's runs (402).
    /// </summary>
    [HttpPost("flows/{id}/runs")]
    [DevScope(DevScopes.RunsWrite)]
    public async Task<IActionResult> RunNow(string id)
    {
        var body = await ReadBodyAsync();
        if (body.Error is { } bodyError) return bodyError;
        JsonObject? data = null;
        if (body.Node is JsonObject root)
        {
            var extra = root.Select(p => p.Key).FirstOrDefault(k => k != "data");
            if (extra is not null) return Bad($"Unknown field \"{Cut(extra)}\". Send {{ \"data\": {{ … }} }}.");
            if (root["data"] is { } d && d is not JsonObject) return Bad("data must be a JSON object.");
            data = root["data"] as JsonObject;
        }
        else if (body.Node is not null) return Bad("The body must be a JSON object: { \"data\": { … } }.");

        return await IdempotentAsync($"run:{id}", body.Bytes, async () =>
        {
            if (await FlowAsync(id) is not { } f) return NoFlowAnswer(id);
            if (FlowDefinition.Parse(f.Definition) is not { } def) return (422, Err(DevCodes.InvalidFlow, "This flow couldn't be read."));
            var errors = DefinitionRules.Validate(def, FlowLimits.From(config));
            if (errors.Count > 0) return (422, Err(DevCodes.InvalidFlow, "Fix this flow before running it.", Details(errors)));
            var ws = (await workspaces.GetAsync(Ws))!;
            var now = DateTime.UtcNow;
            if (await plans.AdmitRunAsync(ws, def, now) is { } refused)
                return (402, Err(refused.Code, refused.Message, new JsonObject { ["upgradeTo"] = refused.UpgradeTo }));
            var run = EventProcessor.NewRun(f, def, ws, Trigger(def, data, now, ws.TimeZone), now, null, null, test: false);
            await engine.InsertRunAsync(run);
            signal.Poke();
            return (202, DevJson.Run(run));
        });
    }

    /// <summary>The trigger data a run now starts with: <c>data</c> as it is, or as a webhook's body; a schedule's slot when none.</summary>
    public static JsonObject Trigger(FlowDefinition def, JsonObject? data, DateTime now, string? timeZone)
    {
        var copy = (JsonObject?)data?.DeepClone() ?? new JsonObject();
        return def.Trigger.App switch
        {
            "webhook" => new JsonObject
            {
                ["body"] = copy, ["query"] = new JsonObject(), ["headers"] = new JsonObject { ["user-agent"] = "Vantra Flow API" }, ["receivedAt"] = now.ToString("o"),
            },
            "schedule" when data is null => new JsonObject { ["scheduledFor"] = now.ToString("o"), ["timeZone"] = timeZone },
            _ => copy,
        };
    }

    // ── Runs ──

    private static readonly string[] RunStatusList =
        [RunStatuses.Queued, RunStatuses.Running, RunStatuses.Waiting, RunStatuses.Succeeded, RunStatuses.Failed, RunStatuses.Skipped, RunStatuses.Cancelled];

    [HttpGet("runs")]
    [DevScope(DevScopes.RunsRead)]
    public async Task<IActionResult> Runs(string? flowId = null, string? status = null, string? from = null, string? to = null, string? includeTests = null,
        string? limit = null, string? cursor = null)
    {
        if (Limit(limit) is not { } take) return Bad($"limit must be 1–{DevConstants.MaxLimit}.");
        var st = status?.Trim() is { Length: > 0 } s ? s : null;
        if (st is not null && !RunStatusList.Contains(st)) return Bad($"status must be one of {string.Join(", ", RunStatusList)}.");
        if (Date(from, out var fromAt) is { } fromError) return Bad($"from {fromError}");
        if (Date(to, out var toAt) is { } toError) return Bad($"to {toError}");
        if (includeTests is not (null or "" or "true" or "false")) return Bad("includeTests must be true or false.");
        var tests = includeTests == "true";
        var flow = flowId?.Trim() is { Length: > 0 } fl ? fl : null;
        var signature = $"runs|{flow}|{st}|{fromAt:O}|{toAt:O}|{tests}";
        DateTime? afterAt = null;
        long? afterSeq = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (DevCursor.Decode(cursor, signature) is not { } c || !long.TryParse(c.A, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !long.TryParse(c.B, NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
                return BadCursor();
            (afterAt, afterSeq) = (new DateTime(ticks, DateTimeKind.Utc), seq);
        }
        var rows = await repo.RunsPageAsync(Ws, flow, st, fromAt, toAt, tests, afterAt, afterSeq, take + 1);
        var page = rows.Take(take).ToList();
        var next = rows.Count > take ? DevCursor.Encode(signature, page[^1].StartedAt.Ticks.ToString(CultureInfo.InvariantCulture), page[^1].Seq.ToString(CultureInfo.InvariantCulture)) : null;
        return Ok(new JsonObject { ["data"] = new JsonArray(page.Select(r => (JsonNode)DevJson.Run(r)).ToArray()), ["nextCursor"] = next });
    }

    [HttpGet("runs/{id}")]
    [DevScope(DevScopes.RunsRead)]
    public async Task<IActionResult> Run(string id)
    {
        if (!ValidId(id) || await engine.RunAsync(Ws, id) is not { } run) return NoRun(id);
        var o = DevJson.Run(run);
        o["trigger"] = Parse(run.TriggerData);
        o["steps"] = new JsonArray((await engine.StepsAsync(run.Id)).Select(s => (JsonNode)new JsonObject
        {
            ["seq"] = s.Seq, ["stepId"] = s.StepId, ["type"] = s.Type, ["app"] = s.App, ["action"] = s.StepAction, ["name"] = s.Name, ["status"] = s.Status,
            ["attempt"] = s.Attempt, ["input"] = Parse(s.Input), ["output"] = Parse(s.Output), ["error"] = s.Error, ["startedAt"] = DevJson.Iso(s.StartedAt),
            ["durationMs"] = s.DurationMs,
        }).ToArray());
        return Ok(o);
    }

    /// <summary>A new run of the flow as it is now, from the same trigger data (as Runs → Replay in the studio).</summary>
    [HttpPost("runs/{id}/replay")]
    [DevScope(DevScopes.RunsWrite)]
    public async Task<IActionResult> Replay(string id)
    {
        var body = await ReadBodyAsync();
        if (body.Error is { } bodyError) return bodyError;
        return await IdempotentAsync($"replay:{id}", body.Bytes, async () =>
        {
            if (!ValidId(id) || await engine.RunAsync(Ws, id) is not { } run) return (404, Err(DevCodes.NoRun, $"There's no run {Cut(id)}."));
            if (await FlowAsync(run.FlowId) is not { } flow) return (404, Err(DevCodes.NoFlow, "Its flow has been deleted."));
            if (FlowDefinition.Parse(flow.Definition) is not { } def) return (422, Err(DevCodes.InvalidFlow, "Its flow couldn't be read."));
            var ws = (await workspaces.GetAsync(Ws))!;
            var now = DateTime.UtcNow;
            if (await plans.AdmitRunAsync(ws, def, now) is { } refused)
                return (402, Err(refused.Code, refused.Message, new JsonObject { ["upgradeTo"] = refused.UpgradeTo }));
            JsonNode trigger;
            try { trigger = JsonNode.Parse(run.TriggerData) ?? new JsonObject(); } catch (JsonException) { trigger = new JsonObject(); }
            var replay = EventProcessor.NewRun(flow, def, ws, trigger, now, null, run.Id, test: run.Test);
            await engine.InsertRunAsync(replay);
            signal.Poke();
            return (202, DevJson.Run(replay));
        });
    }

    // ── Usage ──

    [HttpGet("usage")]
    public async Task<IActionResult> Usage()
    {
        var ws = (await workspaces.GetAsync(Ws))!;
        var p = await plans.DtoAsync(ws, false, DateTime.UtcNow);
        return Ok(new JsonObject
        {
            ["period"] = p.Usage.Period,
            ["plan"] = new JsonObject { ["tier"] = p.Tier, ["name"] = p.Name },
            ["runs"] = new JsonObject { ["used"] = p.Usage.Runs, ["limit"] = p.Usage.RunQuota, ["resetsAt"] = p.Usage.ResetsAt },
            ["activeFlows"] = new JsonObject { ["used"] = p.Usage.ActiveFlows, ["limit"] = p.Limits.ActiveFlows },
        });
    }

    // ── Webhooks ──

    [HttpGet("webhooks")]
    [DevScope(DevScopes.WebhooksManage)]
    public async Task<IActionResult> Webhooks() => Ok(new { data = await endpoints.ListAsync(Ws) });

    [HttpPost("webhooks")]
    [DevScope(DevScopes.WebhooksManage)]
    public async Task<IActionResult> CreateWebhook()
    {
        var (body, bad) = await BodyAsync<EndpointBody>();
        if (bad is not null) return bad;
        var (e, refusal) = await endpoints.CreateAsync(Ws, body, Caller.Key.CreatedBy, $"API · {Caller.Key.Name}");
        return refusal is not null ? Refused(refusal) : StatusCode(201, e);
    }

    [HttpGet("webhooks/{id}")]
    [DevScope(DevScopes.WebhooksManage)]
    public async Task<IActionResult> Webhook(string id) => await endpoints.GetAsync(Ws, id) is { } e ? Ok(e) : Refused(DevEndpoints.NoEndpoint);

    [HttpPatch("webhooks/{id}")]
    [DevScope(DevScopes.WebhooksManage)]
    public async Task<IActionResult> UpdateWebhook(string id)
    {
        var (body, bad) = await BodyAsync<EndpointBody>();
        if (bad is not null) return bad;
        var (e, refusal) = await endpoints.UpdateAsync(Ws, id, body);
        return refusal is not null ? Refused(refusal) : Ok(e);
    }

    [HttpDelete("webhooks/{id}")]
    [DevScope(DevScopes.WebhooksManage)]
    public async Task<IActionResult> DeleteWebhook(string id) => await endpoints.DeleteAsync(Ws, id) ? NoContent() : Refused(DevEndpoints.NoEndpoint);

    [HttpPost("webhooks/{id}/test")]
    [DevScope(DevScopes.WebhooksManage)]
    public async Task<IActionResult> TestWebhook(string id, CancellationToken ct)
    {
        var (body, bad) = await BodyAsync<TestBody>();
        if (bad is not null) return bad;
        var (d, refusal) = await endpoints.TestAsync(Ws, id, body?.Type, ct);
        return refusal is not null ? Refused(refusal) : Ok(d);
    }

    [HttpGet("webhooks/{id}/deliveries")]
    [DevScope(DevScopes.WebhooksManage)]
    public async Task<IActionResult> Deliveries(string id, string? status = null, string? before = null, string? limit = null)
    {
        if (Limit(limit) is not { } take) return Bad($"limit must be 1–{DevConstants.MaxLimit}.");
        var (page, refusal) = await endpoints.DeliveriesAsync(Ws, id, status, before, take);
        return refusal is not null ? Refused(refusal) : Ok(new { data = page });
    }

    public sealed record TestBody(string? Type);

    // ── Helpers ──

    private async Task<FlowRow?> FlowAsync(string id) => ValidId(id) ? await flows.GetAsync(Ws, id) : null;

    private static bool ValidId(string id) => id.Length is > 0 and <= 40;

    private static int? Limit(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return DevConstants.DefaultLimit;
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= DevConstants.MaxLimit ? n : null;
    }

    private static string? Date(string? raw, out DateTime? at)
    {
        at = null;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return "must be an ISO 8601 date or date and time, e.g. 2026-11-01T00:00:00Z.";
        at = DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return null;
    }

    private static JsonNode? Parse(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json); } catch (JsonException) { return JsonValue.Create(json); }
    }

    private static JsonObject Details(IReadOnlyList<DefinitionError> errors) => new()
    {
        ["details"] = new JsonArray(errors.Select(e => (JsonNode)new JsonObject { ["stepId"] = e.StepId, ["message"] = e.Message }).ToArray()),
    };

    private static JsonObject Err(string code, string message, JsonObject? extra = null) => DevResults.Body(code, message, extra);
    private static (int, JsonObject) NoFlowAnswer(string id) => (404, Err(DevCodes.NoFlow, $"There's no flow {Cut(id)}."));
    private static ObjectResult NoFlow(string id) => DevResults.Error(404, DevCodes.NoFlow, $"There's no flow {Cut(id)}.");
    private static ObjectResult NoRun(string id) => DevResults.Error(404, DevCodes.NoRun, $"There's no run {Cut(id)}.");
    private static ObjectResult Bad(string message) => DevResults.Error(400, DevCodes.BadRequest, message);
    private static ObjectResult BadCursor() => DevResults.Error(400, DevCodes.BadRequest, "That cursor isn't from this query. Start again without one.");
    private static ObjectResult InvalidFlow(string message, IReadOnlyList<DefinitionError> errors) => DevResults.Error(422, DevCodes.InvalidFlow, message, Details(errors));
    private static ObjectResult Refused(DevRefusal r) => DevResults.Error(r.Status, r.Code, r.Message);
    private static string Cut(string s) => s.Length <= 64 ? s : s[..64] + "…";

    private sealed record Body(JsonNode? Node, byte[] Bytes, ObjectResult? Error);

    private async Task<Body> ReadBodyAsync()
    {
        if (Request.ContentLength > DevConstants.MaxBodyBytes) return new Body(null, [], TooLarge());
        using var ms = new MemoryStream();
        var chunk = new byte[8192];
        int n;
        while ((n = await Request.Body.ReadAsync(chunk, HttpContext.RequestAborted)) > 0)
        {
            if (ms.Length + n > DevConstants.MaxBodyBytes) return new Body(null, [], TooLarge());
            ms.Write(chunk, 0, n);
        }
        var bytes = ms.ToArray();
        if (bytes.All(b => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')) return new Body(null, bytes, null);
        try { return new Body(JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }), bytes, null); }
        catch (JsonException) { return new Body(null, bytes, DevResults.Error(400, DevCodes.BadRequest, "The body isn't valid JSON.")); }
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A JSON body as <typeparamref name="T"/> (none: null), or the 400 in the API's own shape.</summary>
    private async Task<(T? Value, ObjectResult? Error)> BodyAsync<T>() where T : class
    {
        var body = await ReadBodyAsync();
        if (body.Error is { } e) return (null, e);
        if (body.Node is null) return (null, null);
        if (body.Node is not JsonObject) return (null, Bad("The body must be a JSON object."));
        try { return (body.Node.Deserialize<T>(Web), null); }
        catch (JsonException) { return (null, Bad("A field in the body has the wrong type.")); }
    }

    private static ObjectResult TooLarge() => DevResults.Error(413, DevCodes.TooLarge, $"The body is over {DevConstants.MaxBodyBytes / 1024} KB.");

    /// <summary>
    /// Idempotency-Key, as Stripe's: the first request with a key runs and its answer is kept for a day; the same request
    /// again gets that answer (<c>Idempotent-Replayed: true</c>) and starts nothing; the key with another request is 409.
    /// </summary>
    private async Task<IActionResult> IdempotentAsync(string target, byte[] body, Func<Task<(int Status, JsonObject Body)>> act)
    {
        var (key, error) = Idempotency.Read(Request.Headers["Idempotency-Key"].ToString());
        if (error is not null) return Bad(error);
        if (key is null)
        {
            var (status, json) = await act();
            return new ObjectResult(json) { StatusCode = status };
        }
        var hash = Idempotency.RequestHash(target, body);
        var now = DateTime.UtcNow;
        var existing = await repo.ClaimIdempotencyAsync(Caller.Key.Id, key, Ws, hash, now);
        switch (Idempotency.Decide(existing, hash, now))
        {
            case IdempotencyVerdict.Replay:
                Response.Headers["Idempotent-Replayed"] = "true";
                return new ContentResult { StatusCode = existing!.Status, Content = existing.Response, ContentType = "application/json; charset=utf-8" };
            case IdempotencyVerdict.Mismatch:
                return DevResults.Error(409, DevCodes.IdempotencyConflict, "This Idempotency-Key was already used for a different request.");
            case IdempotencyVerdict.InProgress:
                return DevResults.Error(409, DevCodes.IdempotencyConflict, "A request with this Idempotency-Key is still being handled. Try again in a moment.");
        }
        try
        {
            var (status, json) = await act();
            if (existing is null) await repo.CompleteIdempotencyAsync(Caller.Key.Id, key, status, json.ToJsonString());
            return new ObjectResult(json) { StatusCode = status };
        }
        catch
        {
            if (existing is null) await repo.ReleaseIdempotencyAsync(Caller.Key.Id, key);
            throw;
        }
    }
}

/// <summary>The scope an action needs (<see cref="DevKeyFilter"/> answers 403 <c>missing_scope</c> without it).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DevScopeAttribute(string scope) : Attribute
{
    public string Scope => scope;
}

/// <summary>The API's error answers: <c>{ error: { code, message, … } }</c>.</summary>
public static class DevResults
{
    public static JsonObject Body(string code, string message, JsonObject? extra = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (extra is not null)
            foreach (var (k, v) in extra.ToList())
            {
                extra.Remove(k);
                error[k] = v;
            }
        return new JsonObject { ["error"] = error };
    }

    public static ObjectResult Error(int status, string code, string message, JsonObject? extra = null) =>
        new(Body(code, message, extra)) { StatusCode = status };
}

/// <summary>The request's key, checked: its row, its scopes, and the workspace it acts in.</summary>
public sealed record DevCaller(ApiKeyRow Key, IReadOnlySet<string> Scopes, Guid WorkspaceId, string WorkspaceName);

/// <summary>Who made a key, as a member of its workspace now.</summary>
public sealed record DevKeyOwner(string Role, string WorkspaceName);

/// <summary>What the API's door reads: an interface, so the whole check runs without SQL Server in tests.</summary>
public interface IDevKeyStore
{
    /// <summary>db/012 has run.</summary>
    Task<bool> ReadyAsync();
    Task<ApiKeyRow?> KeyByPrefixAsync(string prefix);
    /// <summary>The key's maker as a member of its workspace now (and Flow on in it), or null.</summary>
    Task<DevKeyOwner?> OwnerAsync(Guid workspaceId, string vantraId);
    /// <summary>Null when the workspace's plan includes the API, else the tier that does.</summary>
    Task<string?> PlanRefusalAsync(Guid workspaceId);
    Task TouchAsync(string keyId, DateTime now);
}

public sealed class SqlDevKeyStore(
    FlowSchema schema, DeveloperRepository repo, WorkspacesRepository workspaces, Services.Billing.Plans plans, IServiceProvider services) : IDevKeyStore
{
    public Task<bool> ReadyAsync() => schema.DeveloperReadyAsync();
    public Task<ApiKeyRow?> KeyByPrefixAsync(string prefix) => repo.KeyByPrefixAsync(prefix);
    public Task TouchAsync(string keyId, DateTime now) => repo.TouchKeyAsync(keyId, now);

    public async Task<DevKeyOwner?> OwnerAsync(Guid workspaceId, string vantraId)
    {
        // As a signed-in member: a shared workspace's list brought in line first, and none while Flow is off in it.
        if (services.GetService<Shared.SharedSync>() is { } sync) await sync.TouchAsync(workspaceId);
        if (services.GetService<Shared.SharedSpaces>() is { } spaces && await spaces.IsOffAsync(Shared.SharedApps.Flow, workspaceId)) return null;
        if (await workspaces.RoleAsync(workspaceId, vantraId) is not { } role) return null;
        return await workspaces.GetAsync(workspaceId) is { } ws ? new DevKeyOwner(role, ws.Name) : null;
    }

    public async Task<string?> PlanRefusalAsync(Guid workspaceId) =>
        await workspaces.GetAsync(workspaceId) is { } ws ? await DevPlan.RefusedAsync(plans, ws, DateTime.UtcNow) : null;
}

/// <summary>
/// The API's door, before every <c>/api/v1</c> action: <c>no-store</c>; db/012 (503 <c>not_ready</c>); the bearer key —
/// its shape, then the row its prefix finds, its hash compared in constant time, revoked or expired (401); the rate
/// limit (429 with <c>Retry-After</c>, headers on every answer from there); its maker still in the workspace (401
/// <c>key_inactive</c>); the plan (402 <c>plan_api</c>); the action's scope (403 <c>missing_scope</c>). After the action,
/// an unexpected failure is 500 <c>server_error</c> (never its text). Keys are never logged.
/// </summary>
public sealed class DevKeyFilter(IDevKeyStore store, DevRateLimiter limiter, ILogger<DevKeyFilter> log, TimeProvider? clock = null) : IAsyncActionFilter
{
    public const string CallerKey = "flow.api.caller";
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static readonly ConcurrentDictionary<string, DateTime> Touched = new(StringComparer.Ordinal);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            if (await CheckAsync(context) is { } refusal)
            {
                context.Result = refusal;
                return;
            }
        }
        catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
        {
            log.LogError(ex, "API request failed before its action ({Method} {Path})", http.Request.Method, http.Request.Path);
            context.Result = DevResults.Error(500, DevCodes.ServerError, "Something went wrong on our side. Try again.");
            return;
        }

        var executed = await next();
        if (executed.Exception is { } failure && !executed.ExceptionHandled)
        {
            log.LogError(failure, "API request failed ({Method} {Path})", http.Request.Method, http.Request.Path);
            executed.Result = DevResults.Error(500, DevCodes.ServerError, "Something went wrong on our side. Try again.");
            executed.ExceptionHandled = true;
        }
    }

    /// <summary>The refusal to answer with, or null (and the caller set) when the request may go ahead.</summary>
    public async Task<ObjectResult?> CheckAsync(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        if (!await store.ReadyAsync()) return DevResults.Error(503, DevCodes.NotReady, "The API isn't set up on this server yet.");
        var presented = DevKeys.FromAuthorization(http.Request.Headers.Authorization.ToString());
        if (presented is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return DevResults.Error(401, DevCodes.Unauthorized, "Send your API key as Authorization: Bearer vfk_…");
        }
        var row = await store.KeyByPrefixAsync(DevKeys.Prefix(presented));
        if (row is null || !DevKeys.Matches(presented, row.KeyHash))
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return DevResults.Error(401, DevCodes.Unauthorized, "That API key isn't valid.");
        }
        var now = time.GetUtcNow().UtcDateTime;
        if (row.RevokedAt is not null) return DevResults.Error(401, DevCodes.KeyRevoked, "This API key was revoked.");
        if (row.ExpiresAt is { } exp && exp <= now) return DevResults.Error(401, DevCodes.KeyExpired, "This API key has expired.");

        var rate = limiter.Take(row.Id);
        var h = http.Response.Headers;
        h["X-RateLimit-Limit"] = rate.Limit.ToString(CultureInfo.InvariantCulture);
        h["X-RateLimit-Remaining"] = rate.Remaining.ToString(CultureInfo.InvariantCulture);
        h["X-RateLimit-Reset"] = rate.ResetUnix.ToString(CultureInfo.InvariantCulture);
        if (!rate.Allowed)
        {
            h.RetryAfter = rate.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return DevResults.Error(429, DevCodes.RateLimited, $"Over {rate.Limit} requests a minute. Try again in {rate.RetryAfterSeconds} s.");
        }

        if (await store.OwnerAsync(row.WorkspaceId, row.CreatedBy) is not { } owner)
            return DevResults.Error(401, DevCodes.KeyInactive, "The person who made this key is no longer in the workspace, so it no longer works.");
        if (await store.PlanRefusalAsync(row.WorkspaceId) is { } tier)
            return DevResults.Error(402, DevCodes.PlanApi, DevPlan.Message, new JsonObject { ["upgradeTo"] = tier });

        var scopes = DevScopes.Parse(row.Scopes);
        var need = context.ActionDescriptor.EndpointMetadata.OfType<DevScopeAttribute>().FirstOrDefault()?.Scope;
        if (need is not null && !scopes.Contains(need))
            return DevResults.Error(403, DevCodes.MissingScope, $"This key doesn't have the {need} scope.", new JsonObject { ["scope"] = need });

        if (!Touched.TryGetValue(row.Id, out var last) || now - last >= DevConstants.TouchEvery)
        {
            Touched[row.Id] = now;
            if (Touched.Count > 10_000) Touched.Clear();
            try { await store.TouchAsync(row.Id, now); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogInformation(ex, "Couldn't note when key {KeyId} was used", row.Id); }
        }
        http.Items[CallerKey] = new DevCaller(row, scopes, row.WorkspaceId, owner.WorkspaceName);
        return null;
    }
}

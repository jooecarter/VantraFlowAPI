using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Developer;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Settings → Developers (db/012; 503 <c>not_ready</c> before), owners and admins: the workspace's API keys and outgoing
/// webhooks. Making keys and webhooks, and sending to them, is on Pro and up (402 <c>plan_api</c>).
/// <code>
/// GET    api/w/{ws}/developer                          → { allowed, upgradeTo, apiBase, keys, webhooks, scopes, events }
/// POST   api/w/{ws}/developer/keys                     { name, scopes, expiresInDays? } → 201 { key, secret } (the key, once)
/// DELETE api/w/{ws}/developer/keys/{id}                → 204: revoked at once
/// POST   api/w/{ws}/developer/webhooks                 { url, description?, events } → 201, with its signing secret once
/// PATCH  api/w/{ws}/developer/webhooks/{id}            { url?, description?, events?, enabled? }
/// POST   api/w/{ws}/developer/webhooks/{id}/secret     → a new signing secret, once
/// DELETE api/w/{ws}/developer/webhooks/{id}
/// POST   api/w/{ws}/developer/webhooks/{id}/test       { type? } → the delivery (a ping, sent now)
/// GET    api/w/{ws}/developer/webhooks/{id}/deliveries?status=&amp;before= → the log, newest first
/// POST   api/w/{ws}/developer/deliveries/{id}/resend   → due again now
/// </code>
/// </summary>
public sealed class DeveloperController(
    WorkspacesRepository workspaces, DeveloperRepository repo, FlowSchema schema, DevEndpoints endpoints, Services.Billing.Plans plans, Connections connections,
    ILogger<DeveloperController> log) : FlowControllerBase(workspaces)
{
    public sealed record CreateKeyBody(string? Name, IReadOnlyList<string?>? Scopes, int? ExpiresInDays);
    public sealed record TestBody(string? Type);

    public sealed record KeyDto(string Id, string Name, string Prefix, IReadOnlyList<string> Scopes, string Status, string? ExpiresAt, string? LastUsedAt,
        string CreatedAt, string? CreatedByName, string? RevokedAt);

    private async Task<ActionResult?> GateAsync(Guid workspaceId)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        bool ready;
        try { ready = await schema.DeveloperReadyAsync(); } catch (Exception e) when (e is not OperationCanceledException) { ready = false; }
        return ready ? null : StatusCode(503, new { error = "The developer API arrives with the next server update.", code = "not_ready" });
    }

    /// <summary>Null when the plan includes the API, else 402 <c>plan_api</c> with the tier that does.</summary>
    private async Task<ActionResult?> PlanAsync(Guid workspaceId)
    {
        if (await Workspaces.GetAsync(workspaceId) is not { } ws) return null;
        return await DevPlan.RefusedAsync(plans, ws, DateTime.UtcNow) is { } tier
            ? StatusCode(402, new { error = DevPlan.Message, code = DevCodes.PlanApi, upgradeTo = tier })
            : null;
    }

    [HttpGet("api/w/{workspaceId:guid}/developer")]
    public async Task<IActionResult> Get(Guid workspaceId)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var tier = await DevPlan.RefusedAsync(plans, ws, DateTime.UtcNow);
        var now = DateTime.UtcNow;
        return Ok(new
        {
            allowed = tier is null,
            upgradeTo = tier ?? DevPlan.UpgradeTo(plans.Catalog),
            apiBase = $"{connections.ApiBase}/api/v1",
            keys = (await repo.KeysAsync(workspaceId)).Select(k => Dto(k, now)).ToList(),
            webhooks = await endpoints.ListAsync(workspaceId),
            scopes = DevScopes.All,
            events = DevEventTypes.All,
        });
    }

    [HttpPost("api/w/{workspaceId:guid}/developer/keys")]
    public async Task<IActionResult> CreateKey(Guid workspaceId, [FromBody] CreateKeyBody? body)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        if (await PlanAsync(workspaceId) is { } plan) return plan;
        if (DevKeys.Name(body?.Name) is not { } name) return BadRequest(new { error = $"Name the key, in up to {DevConstants.MaxName} characters.", code = DevCodes.BadRequest });
        var (scopes, error) = DevScopes.Validate(body!.Scopes);
        if (error is not null) return BadRequest(new { error, code = DevCodes.BadRequest });
        var now = DateTime.UtcNow;
        DateTime? expires = null;
        if (body.ExpiresInDays is { } days)
        {
            if (days is < 1 or > DevConstants.MaxExpiresInDays)
                return BadRequest(new { error = $"A key can last 1 to {DevConstants.MaxExpiresInDays} days, or not expire.", code = DevCodes.BadRequest });
            expires = now.AddDays(days);
        }
        var secret = DevKeys.NewKey();
        var row = new ApiKeyRow(DevKeys.NewId(), workspaceId, name, DevKeys.Prefix(secret), DevKeys.Hash(secret), DevScopes.Format(scopes!), expires, Sub, DisplayName,
            now, null, null, null);
        if (!await repo.InsertKeyAsync(row, DevConstants.MaxActiveKeys))
            return Conflict(new { error = $"A workspace can have {DevConstants.MaxActiveKeys} active keys. Revoke one first.", code = "key_limit" });
        // The key id only: never the key.
        log.LogInformation("Workspace {Workspace} made API key {KeyId}", workspaceId, row.Id);
        return StatusCode(201, new { key = Dto(row, now), secret });
    }

    [HttpDelete("api/w/{workspaceId:guid}/developer/keys/{id}")]
    public async Task<IActionResult> RevokeKey(Guid workspaceId, string id)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        if (!await repo.RevokeKeyAsync(workspaceId, id, Sub, DateTime.UtcNow) && !(await repo.KeysAsync(workspaceId)).Any(k => k.Id == id))
            return NotFound(new { error = "That key doesn't exist any more.", code = DevCodes.NotFound });
        log.LogInformation("Workspace {Workspace} revoked API key {KeyId}", workspaceId, id);
        return NoContent();
    }

    [HttpPost("api/w/{workspaceId:guid}/developer/webhooks")]
    public async Task<IActionResult> CreateWebhook(Guid workspaceId, [FromBody] EndpointBody? body)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        if (await PlanAsync(workspaceId) is { } plan) return plan;
        var (e, refusal) = await endpoints.CreateAsync(workspaceId, body, Sub, DisplayName);
        return refusal is not null ? Refused(refusal) : StatusCode(201, e);
    }

    [HttpPatch("api/w/{workspaceId:guid}/developer/webhooks/{id}")]
    public async Task<IActionResult> UpdateWebhook(Guid workspaceId, string id, [FromBody] EndpointBody? body)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        if (body?.Enabled == true && await PlanAsync(workspaceId) is { } plan) return plan;
        var (e, refusal) = await endpoints.UpdateAsync(workspaceId, id, body);
        return refusal is not null ? Refused(refusal) : Ok(e);
    }

    [HttpPost("api/w/{workspaceId:guid}/developer/webhooks/{id}/secret")]
    public async Task<IActionResult> RollSecret(Guid workspaceId, string id)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        var (e, refusal) = await endpoints.RollSecretAsync(workspaceId, id);
        return refusal is not null ? Refused(refusal) : Ok(e);
    }

    [HttpDelete("api/w/{workspaceId:guid}/developer/webhooks/{id}")]
    public async Task<IActionResult> DeleteWebhook(Guid workspaceId, string id)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        await endpoints.DeleteAsync(workspaceId, id);
        return NoContent();
    }

    [HttpPost("api/w/{workspaceId:guid}/developer/webhooks/{id}/test")]
    public async Task<IActionResult> TestWebhook(Guid workspaceId, string id, [FromBody] TestBody? body, CancellationToken ct)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        if (await PlanAsync(workspaceId) is { } plan) return plan;
        var (d, refusal) = await endpoints.TestAsync(workspaceId, id, body?.Type, ct);
        return refusal is not null ? Refused(refusal) : Ok(d);
    }

    [HttpGet("api/w/{workspaceId:guid}/developer/webhooks/{id}/deliveries")]
    public async Task<IActionResult> Deliveries(Guid workspaceId, string id, string? status = null, string? before = null)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        var (page, refusal) = await endpoints.DeliveriesAsync(workspaceId, id, status, before);
        return refusal is not null ? Refused(refusal) : Ok(page);
    }

    [HttpPost("api/w/{workspaceId:guid}/developer/deliveries/{id}/resend")]
    public async Task<IActionResult> Resend(Guid workspaceId, string id)
    {
        if (await GateAsync(workspaceId) is { } refused) return refused;
        if (await PlanAsync(workspaceId) is { } plan) return plan;
        var (d, refusal) = await endpoints.ResendAsync(workspaceId, id);
        return refusal is not null ? Refused(refusal) : Ok(d);
    }

    private ObjectResult Refused(DevRefusal r) => StatusCode(r.Status, new { error = r.Message, code = r.Code });

    public static KeyDto Dto(ApiKeyRow k, DateTime now) =>
        new(k.Id, k.Name, k.Prefix, DevScopes.All.Where(DevScopes.Parse(k.Scopes).Contains).ToList(), DevKeyStatuses.Of(k.RevokedAt, k.ExpiresAt, now),
            Iso(k.ExpiresAt), Iso(k.LastUsedAt), Iso(k.CreatedAt), k.CreatedByName, Iso(k.RevokedAt));
}

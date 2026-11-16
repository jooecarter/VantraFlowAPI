using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Triggers;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// The builder's side of the new triggers (db/014):
/// <code>
/// GET  api/w/{ws}/triggers/status                 → { ready } (until db/014 has run the studio hides forms, date triggers and buttons)
/// POST api/w/{ws}/flows/{id}/coming-up            { definition } → { items: [{ recordId, title, fireAt }], error }
/// </code>
/// </summary>
public sealed class TriggersController(TriggersRepository repo, DateTriggers dates, IWorkspaceStore workspaces) : FlowControllerBase(workspaces)
{
    [HttpGet("api/w/{workspaceId:guid}/triggers/status")]
    public async Task<IActionResult> Status(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        return Ok(new { ready = await repo.ReadyAsync() });
    }

    public sealed record ComingUpBody(JsonNode? Definition);

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/coming-up")]
    public async Task<IActionResult> ComingUp(Guid workspaceId, string id, [FromBody] ComingUpBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        FlowDefinition? def = null;
        try { def = body.Definition is JsonObject o ? o.Deserialize<FlowDefinition>(FlowDefinition.Json) : null; } catch (JsonException) { }
        if (def is null) return BadRequest(new { error = "That flow couldn't be read." });
        if (!DateRules.IsDateTrigger(def.Trigger)) return Ok(new { items = Array.Empty<object>() });
        var ws = await Workspaces.GetAsync(workspaceId);
        try
        {
            var items = await dates.ComingUpAsync(workspaceId, def, ws?.TimeZone ?? "UTC", 5, DateTime.UtcNow, ct);
            return Ok(new { items });
        }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException)
        {
            return Ok(new { items = Array.Empty<object>(), error = e.Message });
        }
    }
}

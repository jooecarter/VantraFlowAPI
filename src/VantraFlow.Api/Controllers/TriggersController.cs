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
/// POST api/w/{ws}/flows/{id}/coming-up            { definition, days? } → { items: [{ recordId, title, fireAt }], days?: [{ at, count, more }], error }
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

    /// <summary><c>days</c> (1–5): also the next few days it runs on, with how many records each (the Schedule page's panel).</summary>
    public sealed record ComingUpBody(JsonNode? Definition, int? Days = null);

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
            var zone = ws?.TimeZone ?? "UTC";
            var now = DateTime.UtcNow;
            if (body.Days is { } n && n > 0)
            {
                // The same scan as Coming up, counted by day in the workspace's zone.
                var upcoming = await dates.UpcomingAsync(workspaceId, def, zone, now, now.Add(DateRules.Ahead), ct);
                var days = Services.Schedule.ScheduleBoard.ByDay(upcoming.FireAts, Services.Engine.ScheduleRules.Zone(zone));
                while (days.Count > Math.Min(n, 5)) days.RemoveAt(days.Count - 1);
                return Ok(new { items = Array.Empty<object>(), days });
            }
            var items = await dates.ComingUpAsync(workspaceId, def, zone, 5, now, ct);
            return Ok(new { items });
        }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException)
        {
            return Ok(new { items = Array.Empty<object>(), days = Array.Empty<object>(), error = e.Message });
        }
    }
}

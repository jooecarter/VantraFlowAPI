using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Team;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// The activity log (db/011; 503 <c>not_ready</c> before): who changed what, newest first, a page at a time.
/// <code>
/// GET api/w/{ws}/activity?actor=&amp;flowId=&amp;type=&amp;before=&amp;limit=   → { items, next } (owners and admins)
/// GET api/w/{ws}/flows/{id}/activity?before=&amp;limit=                    → one flow's history (members)
/// </code>
/// <c>type</c>: a group (<c>flow</c>, <c>connection</c>, <c>member</c>, <c>settings</c>, <c>folder</c>) or one kind
/// (<see cref="ActivityKinds"/>). <c>next</c>: pass it as <c>before</c> for the page after; null on the last.
/// </summary>
public sealed class ActivityController(IWorkspaceStore workspaces, ITeamStore team) : FlowControllerBase(workspaces)
{
    public const int PageSize = 50;

    [HttpGet("api/w/{workspaceId:guid}/activity")]
    public async Task<IActionResult> Workspace(Guid workspaceId, [FromQuery] string? actor, [FromQuery] string? flowId, [FromQuery] string? type,
        [FromQuery] string? before, [FromQuery] int? limit)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!TeamRules.CanAudit(role)) return StatusCode(403, new { error = "Only owners and admins see the activity log.", code = "not_manager" });
        if (!await team.ReadyAsync()) return NotReady();
        var prefix = ActivityKinds.Prefix(type);
        if (prefix == "") return BadRequest(new { error = "That isn't a kind of activity.", code = "invalid_type" });
        return await PageAsync(workspaceId, new ActivityQuery(Clean(actor, 64), Clean(flowId, 40), prefix), before, limit);
    }

    [HttpGet("api/w/{workspaceId:guid}/flows/{flowId}/activity")]
    public async Task<IActionResult> Flow(Guid workspaceId, string flowId, [FromQuery] string? before, [FromQuery] int? limit)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await team.ReadyAsync()) return NotReady();
        return await PageAsync(workspaceId, new ActivityQuery(FlowId: flowId), before, limit);
    }

    private async Task<IActionResult> PageAsync(Guid workspaceId, ActivityQuery q, string? before, int? limit)
    {
        long? cursor = null;
        if (!string.IsNullOrEmpty(before))
        {
            if (!long.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out var b) || b <= 0) return BadRequest(new { error = "That page can't be found.", code = "invalid_cursor" });
            cursor = b;
        }
        var size = Math.Clamp(limit ?? PageSize, 1, 100);
        var rows = await team.ActivityAsync(workspaceId, q with { Before = cursor, Limit = size + 1 });
        var page = rows.Take(size).Select(Dto).ToList();
        return Ok(new ActivityPageDto(page, rows.Count > size ? page[^1].Id : null));
    }

    private static string? Clean(string? s, int max) => s?.Trim() is { Length: > 0 } t && t.Length <= max ? t : null;

    public static ActivityDto Dto(ActivityRow a) =>
        new(a.Id.ToString(CultureInfo.InvariantCulture), Iso(a.At), a.ActorId, a.ActorName, a.Kind, a.FlowId, a.FlowName, a.Version, a.Detail);

    private ObjectResult NotReady() => StatusCode(503, new { error = "The activity log arrives with the next server update.", code = "not_ready" });
}

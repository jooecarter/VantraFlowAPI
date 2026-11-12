using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Controllers;

/// <summary>The workspace's daily failure digest: on or off (owners and admins change it).</summary>
public sealed record FailureDigestDto(bool On);
public sealed record FailureDigestBody(bool On);

/// <summary>A flow's auto-pause: switched off after <c>After</c> failed runs in a row (null: never).</summary>
public sealed record AutoPauseDto(int? After, IReadOnlyList<int> Choices);
public sealed record AutoPauseBody(int? After);

/// <summary>
/// Error handling's settings (db/010; 503 <c>not_ready</c> before).
/// <code>
/// GET api/w/{ws}/failure-digest               → { on }
/// PUT api/w/{ws}/failure-digest               { on } (owner / admin)
/// GET api/w/{ws}/flows/{id}/auto-pause        → { after, choices }
/// PUT api/w/{ws}/flows/{id}/auto-pause        { after: 2–50 | null }
/// </code>
/// </summary>
public sealed class ReliabilityController(IWorkspaceStore workspaces, FlowSchema schema, ReliabilityRepository repo, FlowsRepository flows)
    : FlowControllerBase(workspaces)
{
    private ObjectResult NotReady() =>
        StatusCode(503, new { error = "The failure digest and auto-pause arrive with the next server update.", code = "not_ready" });

    [HttpGet("api/w/{workspaceId:guid}/failure-digest")]
    public async Task<IActionResult> Digest(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.ReliabilityReadyAsync()) return NotReady();
        return Ok(new FailureDigestDto(await repo.DigestOnAsync(workspaceId)));
    }

    [HttpPut("api/w/{workspaceId:guid}/failure-digest")]
    public async Task<IActionResult> SetDigest(Guid workspaceId, [FromBody] FailureDigestBody body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.ReliabilityReadyAsync()) return NotReady();
        await repo.SetDigestAsync(workspaceId, body.On);
        return Ok(new FailureDigestDto(body.On));
    }

    [HttpGet("api/w/{workspaceId:guid}/flows/{id}/auto-pause")]
    public async Task<IActionResult> AutoPause(Guid workspaceId, string id)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.ReliabilityReadyAsync()) return NotReady();
        if (await flows.GetAsync(workspaceId, id) is null) return NotFound(new { error = "That flow doesn't exist any more." });
        return Ok(new AutoPauseDto(ReliabilityRules.PauseAfter(await repo.PauseAfterAsync(workspaceId, id)), ReliabilityRules.PauseChoices));
    }

    /// <summary>Anyone who can edit the flow sets it; switching it back on after a pause starts the count again.</summary>
    [HttpPut("api/w/{workspaceId:guid}/flows/{id}/auto-pause")]
    public async Task<IActionResult> SetAutoPause(Guid workspaceId, string id, [FromBody] AutoPauseBody body)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.ReliabilityReadyAsync()) return NotReady();
        if (await flows.GetAsync(workspaceId, id) is null) return NotFound(new { error = "That flow doesn't exist any more." });
        if (body.After is { } n && ReliabilityRules.PauseAfter(n) is null)
            return BadRequest(new { error = $"Pause after {ReliabilityRules.MinPauseAfter} to {ReliabilityRules.MaxPauseAfter} failures, or never." });
        await repo.SetPauseAfterAsync(workspaceId, id, body.After);
        return Ok(new AutoPauseDto(body.After, ReliabilityRules.PauseChoices));
    }
}

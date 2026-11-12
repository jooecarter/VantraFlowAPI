using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Approval steps' requests (db/007; 503 <c>not_ready</c> before).
/// <code>
/// GET  api/w/{ws}/approvals?status=pending|all   → newest first; members see every request in the workspace
/// GET  api/w/{ws}/approvals?runId=               → one run's requests (the run page's panel)
/// POST api/w/{ws}/approvals/{id}/decide          { decision: approved | rejected, comment } → the request (its approvers only)
/// POST api/approvals/link/preview                { token } → the request an email link is for (anonymous)
/// POST api/approvals/link/decide                 { token, decision, comment } → decided as the link's approver (anonymous)
/// </code>
/// The first decision counts (409 <c>decided</c> after, 410 <c>expired</c> past its time); it wakes the waiting run at once.
/// An email link's token travels in the body and is checked here: sealed, expiring, and its approver still in the workspace.
/// </summary>
public sealed class ApprovalsController(WorkspacesRepository workspaces, StepsRepository repo, FlowSchema schema, FlowSignal signal)
    : FlowControllerBase(workspaces)
{
    private async Task<IActionResult?> NotReadyAsync() =>
        await schema.StepsReadyAsync() ? null : StatusCode(503, new { error = "Approvals arrive with the next server update.", code = "not_ready" });

    [HttpGet("api/w/{workspaceId:guid}/approvals")]
    public async Task<IActionResult> List(Guid workspaceId, string? status = "pending", string? runId = null, int limit = 100)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync() is { } nr) return nr;
        var rows = runId is { Length: > 0 and <= 40 } run ? await repo.RunApprovalsAsync(workspaceId, run)
            : await repo.ApprovalsAsync(workspaceId, status != "all", Math.Clamp(limit, 1, 200));
        return Ok(rows.Select(a => Dto(a, Sub, DateTime.UtcNow)).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/approvals/{id}/decide")]
    public async Task<IActionResult> Decide(Guid workspaceId, string id, [FromBody] DecideBody body)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync() is { } nr) return nr;
        if (body.Decision is not ("approved" or "rejected")) return BadRequest(new { error = "Approve or reject it." });
        if (await repo.ApprovalAsync(id) is not { } a || a.WorkspaceId != workspaceId) return NotFound(new { error = "That request isn't there any more." });
        if (!ApprovalGate.Approvers(a.Approvers).Any(x => x.Id == Sub))
            return StatusCode(403, new { error = "Only the people asked can decide this one.", code = "not_approver" });
        return await DecideAsync(a, body.Decision, Sub, DisplayName, "app", body.Comment);
    }

    private async Task<IActionResult> DecideAsync(ApprovalRow a, string decision, string by, string? byName, string via, string? comment)
    {
        var now = DateTime.UtcNow;
        var note = comment?.Trim() is { Length: > 0 } c ? (c.Length > 1000 ? c[..1000] : c) : null;
        if (!await repo.DecideAsync(a.Id, decision, by, byName, via, note, now))
        {
            var current = await repo.ApprovalAsync(a.Id) ?? a;
            return current.Status == "pending" && current.ExpiresAt <= now
                ? StatusCode(410, new { error = "Its time ran out before anyone decided.", code = "expired", approval = Dto(current, by, now) })
                : Conflict(new { error = current.Status == "cancelled" ? "Its run was cancelled." : "It has already been decided.", code = "decided", approval = Dto(current, by, now) });
        }
        signal.Poke();
        return Ok(Dto((await repo.ApprovalAsync(a.Id))!, by, now));
    }

    public static ApprovalDto Dto(ApprovalRow a, string viewer, DateTime now)
    {
        var approvers = ApprovalGate.Approvers(a.Approvers);
        var status = a.Status == "pending" && a.ExpiresAt <= now ? "expired" : a.Status;
        return new ApprovalDto(a.Id, a.FlowId, a.FlowName, a.RunId, a.StepId, a.Title, a.Details, approvers, status, a.DecidedByName, Iso(a.DecidedAt), a.DecidedVia,
            a.Comment, Iso(a.RequestedAt), Iso(a.ExpiresAt), status == "pending" && approvers.Any(x => x.Id == viewer));
    }

    public sealed record DecideBody(string? Decision, string? Comment);
}

/// <summary>An approval email's links: anonymous, the sealed token the only credential (<see cref="ApprovalLinks"/>).</summary>
[ApiController]
[ServiceFilter(typeof(FlowReadyFilter))]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
public sealed class ApprovalLinksController(WorkspacesRepository workspaces, StepsRepository repo, FlowSchema schema, FlowSecrets secrets, FlowSignal signal)
    : ControllerBase
{
    public const string RateLimitPolicy = "approval-links";

    public sealed record LinkBody(string? Token, string? Decision, string? Comment);

    private async Task<(ApprovalRow? Row, string Approver, string Decision, IActionResult? Refusal)> OpenAsync(LinkBody? body)
    {
        if (!await schema.StepsReadyAsync()) return (null, "", "", StatusCode(503, new { error = "Approvals arrive with the next server update.", code = "not_ready" }));
        if (ApprovalLinks.Read(secrets, body?.Token, DateTime.UtcNow) is not { } link)
            return (null, "", "", StatusCode(410, new { error = "This link has expired or isn't one Vantra Flow made.", code = "link_invalid" }));
        var a = await repo.ApprovalAsync(link.ApprovalId);
        var approver = a is null ? null : ApprovalGate.Approvers(a.Approvers).FirstOrDefault(x => x.Id == link.ApproverId);
        if (a is null || approver is null || await workspaces.RoleAsync(a.WorkspaceId, link.ApproverId) is null)
            return (null, "", "", StatusCode(410, new { error = "This request isn't there any more.", code = "link_invalid" }));
        return (a, link.ApproverId, link.Decision, null);
    }

    [HttpPost("api/approvals/link/preview")]
    public async Task<IActionResult> Preview([FromBody] LinkBody? body)
    {
        var (a, approver, decision, refusal) = await OpenAsync(body);
        if (refusal is not null) return refusal;
        var dto = ApprovalsController.Dto(a!, approver, DateTime.UtcNow);
        var me = dto.Approvers.First(x => x.Id == approver);
        // A link-holder sees names only, not the other approvers' addresses.
        return Ok(new
        {
            approval = dto with { Approvers = dto.Approvers.Select(x => x with { Email = null }).ToList() },
            decision,
            approverName = me.Name ?? me.Email,
        });
    }

    [HttpPost("api/approvals/link/decide")]
    public async Task<IActionResult> Decide([FromBody] LinkBody? body)
    {
        var (a, approver, linked, refusal) = await OpenAsync(body);
        if (refusal is not null) return refusal;
        var decision = body?.Decision is "approved" or "rejected" ? body.Decision : linked;
        var name = ApprovalGate.Approvers(a!.Approvers).First(x => x.Id == approver);
        var now = DateTime.UtcNow;
        var note = body?.Comment?.Trim() is { Length: > 0 } c ? (c.Length > 1000 ? c[..1000] : c) : null;
        if (!await repo.DecideAsync(a.Id, decision, approver, name.Name ?? name.Email, "email", note, now))
        {
            var current = await repo.ApprovalAsync(a.Id) ?? a;
            var dto = ApprovalsController.Dto(current, approver, now);
            return current.Status == "pending" && current.ExpiresAt <= now
                ? StatusCode(410, new { error = "Its time ran out before anyone decided.", code = "expired", approval = dto with { Approvers = [] } })
                : Conflict(new { error = "It has already been decided.", code = "decided", approval = dto with { Approvers = [] } });
        }
        signal.Poke();
        var done = ApprovalsController.Dto((await repo.ApprovalAsync(a.Id))!, approver, now);
        return Ok(new { approval = done with { Approvers = done.Approvers.Select(x => x with { Email = null }).ToList() } });
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Team;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Comments on a flow (db/011; 503 <c>not_ready</c> before), with @mentions of the workspace's members:
/// <c>@[Priya Shah](vantra-id)</c> in the text. Someone mentioned is emailed (the server's sender, as the author), at most
/// once an hour per flow. Any member comments; only the author edits; the author or an owner or admin deletes.
/// <code>
/// GET    api/w/{ws}/flows/{id}/comments         → oldest first
/// POST   api/w/{ws}/flows/{id}/comments         { body } → 201
/// PATCH  api/w/{ws}/comments/{commentId}        { body } (its author)
/// DELETE api/w/{ws}/comments/{commentId}        (its author, an owner or an admin)
/// </code>
/// </summary>
public sealed class CommentsController(IWorkspaceStore workspaces, ITeamStore team, IMentionMail mail, ILogger<CommentsController> log)
    : FlowControllerBase(workspaces)
{
    public const string RateLimitPolicy = "comments";

    [HttpGet("api/w/{workspaceId:guid}/flows/{flowId}/comments")]
    public async Task<IActionResult> List(Guid workspaceId, string flowId)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await team.ReadyAsync()) return NotReady();
        if (await team.PlacementAsync(workspaceId, flowId) is null) return NoFlow();
        return Ok((await team.CommentsAsync(workspaceId, flowId)).Select(c => Dto(c, role)).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{flowId}/comments")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Add(Guid workspaceId, string flowId, [FromBody] CommentBody? body)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!TeamRules.CanComment(role)) return StatusCode(403, new { error = "You can't comment in this workspace.", code = "not_allowed" });
        if (!await team.ReadyAsync()) return NotReady();
        if (await team.PlacementAsync(workspaceId, flowId) is not { } flow) return NoFlow();
        if (TeamRules.CleanComment(body?.Body) is not { } text) return BadRequest(new { error = $"Write a comment of up to {TeamRules.CommentMax} characters.", code = "invalid_comment" });
        var row = new CommentRow(FlowSecrets.NewRowId("cmt_"), workspaceId, flowId, Sub, DisplayName, text, DateTime.UtcNow, null);
        await team.InsertCommentAsync(row);
        await NotifyAsync(workspaceId, flow, text, [], DateTime.UtcNow);
        return StatusCode(201, Dto(row, role));
    }

    [HttpPatch("api/w/{workspaceId:guid}/comments/{commentId}")]
    public async Task<IActionResult> Edit(Guid workspaceId, string commentId, [FromBody] CommentBody? body)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await team.ReadyAsync()) return NotReady();
        if (await team.CommentAsync(workspaceId, commentId) is not { } c) return NoComment();
        if (!TeamRules.CanEditComment(c.AuthorId, Sub)) return StatusCode(403, new { error = "Only its author can edit a comment.", code = "not_author" });
        if (TeamRules.CleanComment(body?.Body) is not { } text) return BadRequest(new { error = $"Write a comment of up to {TeamRules.CommentMax} characters.", code = "invalid_comment" });
        var now = DateTime.UtcNow;
        await team.UpdateCommentAsync(workspaceId, commentId, text, now);
        // Only people newly mentioned by the edit hear about it.
        if (await team.PlacementAsync(workspaceId, c.FlowId) is { } flow) await NotifyAsync(workspaceId, flow, text, TeamRules.Mentions(c.Body), now);
        return Ok(Dto(c with { Body = text, EditedAt = now }, role));
    }

    [HttpDelete("api/w/{workspaceId:guid}/comments/{commentId}")]
    public async Task<IActionResult> Delete(Guid workspaceId, string commentId)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await team.ReadyAsync()) return NotReady();
        if (await team.CommentAsync(workspaceId, commentId) is not { } c) return NoContent();
        if (!TeamRules.CanDeleteComment(role, c.AuthorId, Sub))
            return StatusCode(403, new { error = "Only its author, an owner or an admin can delete a comment.", code = "not_allowed" });
        await team.DeleteCommentAsync(workspaceId, commentId);
        return NoContent();
    }

    /// <summary>Emails each member newly mentioned, at most once an hour per flow. Best effort: a comment never fails over its emails.</summary>
    private async Task NotifyAsync(Guid workspaceId, FlowPlacementRow flow, string text, IReadOnlyList<string> before, DateTime now)
    {
        var mentioned = TeamRules.Mentions(text);
        if (mentioned.Count == 0 || !mail.CanSend) return;
        try
        {
            var members = await Workspaces.MembersAsync(workspaceId);
            var who = TeamRules.ToNotify(mentioned, before, Sub, members);
            if (who.Count == 0) return;
            var ws = await Workspaces.GetAsync(workspaceId);
            var link = mail.LinkFor(workspaceId, flow.Id);
            foreach (var person in who)
                if (await team.ClaimMentionAsync(workspaceId, flow.Id, person.VantraId, now))
                    await mail.SendAsync(person.Email!, person.Name, DisplayName, Email, ws?.Name ?? "your workspace", flow.Name, TeamRules.Plain(text), link);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Mention emails for a comment didn't go out");
        }
    }

    private CommentDto Dto(CommentRow c, string? role) =>
        new(c.Id, c.FlowId, c.AuthorId, c.AuthorName, c.Body, TeamRules.Mentions(c.Body), Iso(c.CreatedAt), Iso(c.EditedAt),
            TeamRules.CanEditComment(c.AuthorId, Sub), TeamRules.CanDeleteComment(role, c.AuthorId, Sub));

    private ObjectResult NotReady() => StatusCode(503, new { error = "Comments arrive with the next server update.", code = "not_ready" });
    private NotFoundObjectResult NoFlow() => NotFound(new { error = "That flow doesn't exist any more.", code = "no_flow" });
    private NotFoundObjectResult NoComment() => NotFound(new { error = "That comment was deleted.", code = "no_comment" });
}

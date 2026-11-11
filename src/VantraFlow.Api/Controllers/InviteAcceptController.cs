using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Workspaces;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Invitations from the invitee's side (db/005; 503 <c>not_ready</c> before). The token travels in the body, never
/// in a URL, and is looked up by its SHA-256 only.
/// <code>
/// POST api/invites/preview   { token } → what it's for (anonymous): 410 invite_expired | invite_revoked | invite_used, 404 invite_invalid
/// POST api/invites/accept    { token } → { workspaceId, workspaceName, alreadyMember } (signed in)
/// </code>
/// Accepting matches the invited address against the signed-in account's email in the identity database (not the
/// token's claim, which can be stale), ignoring case: 409 <c>wrong_account</c> { email, workspace } otherwise. The
/// studio keeps a link opened while signed out and accepts it once the person signs in or signs up.
/// </summary>
[ApiController]
[Authorize]
[ServiceFilter(typeof(FlowReadyFilter))]
public sealed class InviteAcceptController(IInviteStore invites, IUserDirectory users, IWorkspaceFeatures features, ILogger<InviteAcceptController> log)
    : ControllerBase
{
    public const string RateLimitPolicy = "invite-links";

    [HttpPost("api/invites/preview")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Preview([FromBody] InviteTokenBody? body)
    {
        if (!await features.InvitesReadyAsync()) return NotReady();
        if (!InviteRules.IsTokenShape(body?.Token)) return Invalid();
        var r = await invites.FindByTokenAsync(InviteRules.Hash(body!.Token!));
        if (r is null) return Invalid();
        return InviteRules.Preview(r.Status, r.ExpiresAt, DateTime.UtcNow) switch
        {
            InviteOutcome.Accept => Ok(new InvitePreviewDto(r.WorkspaceName ?? "", ColorOf(r), r.WorkspaceLogo, r.Role, r.InvitedByName,
                InviteRules.EmailHint(r.Email), FlowControllerBase.Iso(r.ExpiresAt))),
            var other => Closed(other, r),
        };
    }

    [HttpPost("api/invites/accept")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Accept([FromBody] InviteTokenBody? body)
    {
        if (!await features.InvitesReadyAsync()) return NotReady();
        if (!InviteRules.IsTokenShape(body?.Token)) return Invalid();
        var sub = User.FindFirstValue("sub") ?? "";
        var me = await users.FindByVantraIdAsync(sub);
        var email = !string.IsNullOrWhiteSpace(me?.Email) ? me.Email : User.FindFirstValue("email");
        var name = me is null ? null : $"{me.FirstName} {me.LastName}".Trim() is { Length: > 0 } n ? n : null;
        var result = await invites.AcceptAsync(InviteRules.Hash(body!.Token!), sub, email, name);
        if (result is not { } r) return Invalid();
        var ws = r.Invite.WorkspaceName ?? "";
        switch (r.Outcome)
        {
            case InviteOutcome.Accept:
                log.LogInformation("Someone joined workspace {Workspace} by invitation", r.Invite.WorkspaceId);
                return Ok(new AcceptInviteDto(r.Invite.WorkspaceId.ToString(), ws, false));
            case InviteOutcome.AlreadyMember:
                return Ok(new AcceptInviteDto(r.Invite.WorkspaceId.ToString(), ws, true));
            case InviteOutcome.WrongAccount:
                return Conflict(new
                {
                    error = $"This invitation was sent to {r.Invite.Email}. Sign in as that address to join {ws}.",
                    code = "wrong_account", email = r.Invite.Email, workspace = ws,
                });
            default:
                return Closed(r.Outcome, r.Invite);
        }
    }

    private ObjectResult Closed(InviteOutcome outcome, InviteRow r)
    {
        var ws = r.WorkspaceName ?? "the workspace";
        return outcome switch
        {
            InviteOutcome.Expired => StatusCode(410, new { error = $"This invitation to {ws} has expired. Ask for a new one.", code = "invite_expired", workspace = ws }),
            InviteOutcome.Revoked => StatusCode(410, new { error = $"This invitation to {ws} was withdrawn.", code = "invite_revoked", workspace = ws }),
            InviteOutcome.Used => StatusCode(410, new { error = $"This invitation to {ws} has already been used.", code = "invite_used", workspace = ws }),
            _ => Invalid(),
        };
    }

    private NotFoundObjectResult Invalid() => NotFound(new { error = "This invitation link isn't valid.", code = "invite_invalid" });

    private ObjectResult NotReady() => StatusCode(503, new { error = "Invitations arrive with the next server update.", code = WorkspaceCodes.NotReady });

    private static string ColorOf(InviteRow r) => string.IsNullOrWhiteSpace(r.WorkspaceColor) ? WorkspaceRules.DefaultColor : r.WorkspaceColor;
}
